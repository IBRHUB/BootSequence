using BootSequence.Core;
using BootSequence.SystemServices;
using System.Diagnostics;
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
    private readonly BcdEditService _bcdEdit;
    private readonly PendingRestartJournal _journal;
    private readonly ShutdownMonitor? _shutdownMonitor;
    private string? _pendingBootTarget;

    public MainWindow()
    {
        _logger = AppDiagnostics.Logger;
        _boot = new BcdWmiService(_logger);
        _bitLocker = new BitLockerService();
        _restart = new RestartService();
        _bcdEdit = new BcdEditService();
        _journal = new PendingRestartJournal();
        InitializeComponent();
        UpdateTimingText();
        Title = "BootSequence";
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(500, 520));

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
                ShowStatus("Restart canceled", "Pending change removed",
                    InfoBarSeverity.Success);
            }
            else if (_shutdownMonitor is null)
            {
                ShowDiagnostic("shutdown.monitor-install");
            }
        }
        catch (Exception exception)
        {
            _logger.Error("startup.read-boot-state", exception);
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ShowDiagnostic("startup.read-boot-state");
        }
    }

    private void Populate(IReadOnlyList<BootEntry> entries)
    {
        BootList.Children.Clear();
        int available = 0;

        foreach (BootEntry entry in entries)
        {
            var text = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = entry.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            text.Children.Add(new TextBlock
            {
                Text = EntryDetails(entry),
                Opacity = 0.7,
                FontSize = 12
            });

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(text);

            FrameworkElement state = EntryState(entry);
            Grid.SetColumn(state, 1);
            content.Children.Add(state);

            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 64,
                Padding = new Thickness(16, 10, 14, 10),
                CornerRadius = new CornerRadius(8),
                Content = content,
                IsEnabled = _shutdownMonitor is not null && !entry.IsCurrent && entry.IsSelectable
            };
            if (button.IsEnabled)
            {
                available++;
                button.Click += async (_, _) => await ConfirmAndRestartAsync(entry);
            }
            BootList.Children.Add(button);
        }

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        BootList.Visibility = Visibility.Visible;
        if (available == 0)
            ShowStatus("No Windows available", "Check the listed entries", InfoBarSeverity.Informational);
    }

    private async Task ConfirmAndRestartAsync(BootEntry entry)
    {
        BootSelectionMethod method = SelectedMethod();
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
        content.Children.Add(new TextBlock { Text = "Save your work" });
        if (assessment.ShouldWarn)
        {
            content.Children.Add(new TextBlock
            {
                Text = assessment.HasUnknown
                    ? "Drive protection unknown — Keep the recovery key ready"
                    : "Keep the recovery key ready",
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (method == BootSelectionMethod.BcdEditRawBootSequence)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Experimental method",
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
                var timer = Stopwatch.StartNew();
                PrepareResult prepared = Prepare(entry, method, coordinator);
                timer.Stop();
                AppDiagnostics.Logger.RecordTiming(MethodName(method), timer.ElapsedMilliseconds);
                if (prepared != PrepareResult.Ready)
                {
                    return (prepared, false);
                }

                try
                {
                    _journal.Arm(entry.Id);
                }
                catch (Exception exception)
                {
                    _logger.Error("restart.arm-recovery", exception);
                    coordinator.RollBackIfOwned(entry.Id);
                    return (PrepareResult.WriteFailed, false);
                }

                Volatile.Write(ref _pendingBootTarget, entry.Id);

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
        UpdateTimingText();
        ShowResult(result);
    }

    private PrepareResult Prepare(
        BootEntry entry,
        BootSelectionMethod method,
        BootCoordinator coordinator)
    {
        if (method == BootSelectionMethod.CurrentWmi)
            return coordinator.Prepare(entry.Id);

        if (entry.IsCurrent || !entry.IsSelectable)
            return PrepareResult.InvalidTarget;

        try
        {
            _bcdEdit.Apply(method, entry.Id);
            return PrepareResult.Ready;
        }
        catch (Exception exception)
        {
            _logger.Error(MethodStage(method), exception);
            return PrepareResult.WriteFailed;
        }
    }

    private void SetBusy(bool busy)
    {
        BootMethodBox.IsEnabled = !busy;
        BootList.IsHitTestVisible = !busy;
        LoadingRing.IsActive = busy;
        LoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AboutButton_Click(object sender, RoutedEventArgs args)
    {
        MainPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Visible;
        AboutButton.IsEnabled = false;
    }

    private void BackButton_Click(object sender, RoutedEventArgs args)
    {
        AboutPage.Visibility = Visibility.Collapsed;
        MainPage.Visibility = Visibility.Visible;
        AboutButton.IsEnabled = true;
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void ShowDiagnostic(string stage)
    {
        DiagnosticIssue issue = AppDiagnostics.Logger.DescribeLatest(stage);
        ShowStatus(issue.Title, issue.Message, InfoBarSeverity.Error);
    }

    private void UpdateTimingText()
    {
        DiagnosticTiming? timing = AppDiagnostics.Logger.ReadTiming();
        if (timing is null) return;
        LastTimingText.Text = $"Last startup {timing.Milliseconds} ms";
        LastTimingText.Visibility = Visibility.Visible;
    }

    private BootSelectionMethod SelectedMethod() =>
        (BootSelectionMethod)Math.Clamp(BootMethodBox.SelectedIndex, 0, 2);

    private static string MethodName(BootSelectionMethod method) => method switch
    {
        BootSelectionMethod.BcdEditBootSequence => "/bootsequence",
        BootSelectionMethod.BcdEditRawBootSequence => "/set bootsequence",
        BootSelectionMethod.CurrentWmi => "WMI",
        _ => "Unknown"
    };

    private static string MethodStage(BootSelectionMethod method) => method switch
    {
        BootSelectionMethod.BcdEditBootSequence => "bcdedit.bootsequence",
        BootSelectionMethod.BcdEditRawBootSequence => "bcdedit.raw-bootsequence",
        _ => "bcd.prepare-sequence"
    };

    private static string CurrentDrive()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string root = Path.GetPathRoot(windows) ?? string.Empty;
        return root.Length >= 2 ? root[..2].ToUpperInvariant() : string.Empty;
    }

    private void ShowResult(PrepareResult result)
    {
        switch (result)
        {
            case PrepareResult.PendingExists:
                ShowStatus("Restart already planned", "Restart Windows first", InfoBarSeverity.Warning);
                break;
            case PrepareResult.PersistentSequence:
                ShowStatus("Boot order is locked", "Remove the existing sequence", InfoBarSeverity.Error);
                break;
            case PrepareResult.InvalidTarget:
                ShowStatus("Windows unavailable", "Choose another entry", InfoBarSeverity.Error);
                break;
            case PrepareResult.VerificationFailed:
                ShowDiagnostic("bcd.verify-result");
                break;
            default:
                ShowDiagnostic("bcd.prepare-sequence");
                break;
        }
    }

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
            ShowStatus("Restart canceled", "Pending change removed",
                InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            _logger.Error("shutdown.canceled-cleanup", exception);
            ShowDiagnostic("shutdown.canceled-cleanup");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string EntryDetails(BootEntry entry)
    {
        return entry.Availability == BootEntryAvailability.Available
            ? entry.Disk
            : $"{entry.Disk}  Unavailable";
    }

    private static FrameworkElement EntryState(BootEntry entry)
    {
        if (!entry.IsCurrent && entry.IsSelectable)
        {
            return new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 12,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new TextBlock
        {
            Text = entry.IsCurrent ? "Current" : "Unavailable",
            FontSize = 12,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
    }
}
