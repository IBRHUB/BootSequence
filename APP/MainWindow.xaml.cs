using BootSequence.Core;
using BootSequence.SystemServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BootSequence;

public sealed partial class MainWindow : Window
{
    private readonly IDiagnosticLogger _logger;
    private readonly IBootConfigurationService _boot;
    private readonly IBitLockerService _bitLocker;
    private readonly IRestartService _restart;
    private readonly PendingRestartJournal _journal;
    private readonly ShutdownMonitor? _shutdownMonitor;
    private string? _pendingBootTarget;

    public MainWindow()
    {
        _logger = AppDiagnostics.Logger;
        _boot = new BcdWmiService(_logger);
        _bitLocker = new BitLockerService();
        _restart = new RestartService();
        _journal = new PendingRestartJournal();
        InitializeComponent();
        Title = "BootSequence";
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(480, 420));

        try
        {
            _shutdownMonitor = new ShutdownMonitor(hwnd);
            _shutdownMonitor.ShutdownCanceled += OnShutdownCanceled;
        }
        catch (Exception exception)
        {
            _logger.Error("shutdown.monitor-install", exception);
        }

        Closed += (_, _) => _shutdownMonitor?.Dispose();
        _ = LoadEntriesAsync();
    }

    private async Task LoadEntriesAsync()
    {
        try
        {
            bool recovered = await Task.Run(RecoverPendingRestart);
            IReadOnlyList<BootEntry> entries = await Task.Run(_boot.ReadEntries);
            Populate(entries);
            if (recovered)
            {
                ShowStatus("Canceled restart recovered; the pending boot change was removed",
                    InfoBarSeverity.Success);
            }
            else if (_shutdownMonitor is null)
            {
                ShowStatus("Restart safety monitor is unavailable. Restart is disabled.",
                    InfoBarSeverity.Error);
            }
        }
        catch (Exception exception)
        {
            _logger.Error("startup.read-boot-state", exception);
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ShowStatus("Can't safely read or recover the boot state. See %LOCALAPPDATA%\\BootSequence\\logs\\BootSequence.log.",
                InfoBarSeverity.Error);
        }
    }

    private void Populate(IReadOnlyList<BootEntry> entries)
    {
        BootList.Items.Clear();
        int available = 0;

        foreach (BootEntry entry in entries)
        {
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock
            {
                Text = entry.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            string suffix = EntrySuffix(entry);
            text.Children.Add(new TextBlock
            {
                Text = entry.Disk + suffix,
                Opacity = 0.7,
                FontSize = 12
            });

            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Content = text,
                IsEnabled = _shutdownMonitor is not null && !entry.IsCurrent && entry.IsSelectable
            };
            if (button.IsEnabled)
            {
                available++;
                button.Click += async (_, _) => await ConfirmAndRestartAsync(entry);
            }
            BootList.Items.Add(button);
        }

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        BootList.Visibility = Visibility.Visible;
        if (available == 0) ShowStatus("No other Windows", InfoBarSeverity.Informational);
    }

    private async Task ConfirmAndRestartAsync(BootEntry entry)
    {
        SetBusy(true);
        BitLockerAssessment assessment;
        try
        {
            assessment = await Task.Run(() => SafetyChecks.AssessBitLocker(
                _bitLocker,
                CurrentDrive(),
                entry.Disk));
        }
        catch (Exception exception)
        {
            _logger.Error("bitlocker.assess", exception);
            assessment = new BitLockerAssessment(
                VolumeProtectionStatus.Unknown,
                VolumeProtectionStatus.Unknown);
        }
        SetBusy(false);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "Save your work first" });
        if (assessment.ShouldWarn)
        {
            content.Children.Add(new TextBlock
            {
                Text = assessment.HasUnknown
                    ? "BitLocker status couldn't be verified. Keep your recovery key ready."
                    : "Keep your BitLocker recovery key ready.",
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Restart to {entry.Name}",
            Content = content,
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        StatusBar.IsOpen = false;
        PrepareResult result = PrepareResult.WriteFailed;
        bool restartStarted = false;
        try
        {
            (result, restartStarted) = await Task.Run(() =>
            {
                var coordinator = new BootCoordinator(_boot, _logger);
                PrepareResult prepared = coordinator.Prepare(entry.Id);
                if (prepared != PrepareResult.Ready)
                {
                    return (prepared, false);
                }

                try
                {
                    _journal.Arm(entry.Id);
                    Volatile.Write(ref _pendingBootTarget, entry.Id);
                }
                catch (Exception exception)
                {
                    _logger.Error("restart.arm-recovery", exception);
                    coordinator.RollBackIfOwned(entry.Id);
                    return (PrepareResult.WriteFailed, false);
                }

                try
                {
                    bool restarted = _restart.Restart();
                    if (!restarted)
                    {
                        RecoverOwnedSequence(entry.Id, "restart.rejected-rollback");
                        Volatile.Write(ref _pendingBootTarget, null);
                    }

                    return (prepared, restarted);
                }
                catch (Exception exception)
                {
                    _logger.Error("restart.request", exception);
                    RecoverOwnedSequence(entry.Id, "restart.request-rollback");
                    Volatile.Write(ref _pendingBootTarget, null);
                    return (prepared, false);
                }
            });
        }
        catch (Exception exception)
        {
            _logger.Error("restart.workflow", exception);
            result = PrepareResult.WriteFailed;
        }

        if (restartStarted) return;
        SetBusy(false);
        ShowStatus(ResultMessage(result), InfoBarSeverity.Error);
    }

    private void SetBusy(bool busy)
    {
        BootList.IsEnabled = !busy;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static string CurrentDrive()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.GetPathRoot(windows) ?? string.Empty;
        return root.Length >= 2 ? root[..2].ToUpperInvariant() : string.Empty;
    }

    private static string ResultMessage(PrepareResult result) => result switch
    {
        PrepareResult.PendingExists => "Restart already planned",
        PrepareResult.PersistentSequence => "Persistent boot sequence detected",
        PrepareResult.InvalidTarget => "Windows entry unavailable",
        PrepareResult.WriteFailed => "Can't set next Windows. See %LOCALAPPDATA%\\BootSequence\\logs\\BootSequence.log.",
        PrepareResult.VerificationFailed => "Boot change not verified. See %LOCALAPPDATA%\\BootSequence\\logs\\BootSequence.log.",
        _ => "Can't restart. See %LOCALAPPDATA%\\BootSequence\\logs\\BootSequence.log."
    };

    private bool RecoverPendingRestart()
    {
        string? targetId = _journal.Read();
        if (targetId is null) return false;
        RecoverOwnedSequence(targetId, "startup.recover-pending");
        return true;
    }

    private void RecoverOwnedSequence(string targetId, string stage)
    {
        bool cleared = _boot.ClearOneTimeSequenceIfMatches(targetId);
        IReadOnlyList<string> remaining = _boot.ReadOneTimeSequence();
        bool stillOwned = remaining.Count == 1 &&
            string.Equals(remaining[0], targetId, StringComparison.OrdinalIgnoreCase);
        if (!cleared && stillOwned)
        {
            throw new IOException($"The owned BootSequence could not be cleared during {stage}");
        }

        _journal.Clear();
    }

    private async void OnShutdownCanceled(object? sender, EventArgs args)
    {
        string? targetId = Interlocked.Exchange(ref _pendingBootTarget, null);
        if (targetId is null) return;

        SetBusy(true);
        try
        {
            await Task.Run(() => RecoverOwnedSequence(targetId, "shutdown.canceled"));
            ShowStatus("Restart was canceled; the pending boot change was removed",
                InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            _logger.Error("shutdown.canceled-cleanup", exception);
            ShowStatus("Restart was canceled, but cleanup could not be verified. See %LOCALAPPDATA%\\BootSequence\\logs\\BootSequence.log.",
                InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string EntrySuffix(BootEntry entry)
    {
        if (entry.IsCurrent) return "  Current";
        return entry.Availability switch
        {
            BootEntryAvailability.Available => string.Empty,
            BootEntryAvailability.UnresolvedDevice => "  Device unavailable",
            BootEntryAvailability.LoaderMissing => "  Loader missing",
            BootEntryAvailability.InspectionFailed => "  Loader unverified",
            _ => "  Unavailable"
        };
    }
}
