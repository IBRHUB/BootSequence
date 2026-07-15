using System.ComponentModel;
using System.Management;
using BootSequence.Core;

namespace BootSequence.SystemServices;

public sealed class FileDiagnosticLogger : IDiagnosticLogger
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BootSequence",
        "logs",
        "BootSequence.log");

    private string TimingPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BootSequence",
        "last-bcd-timing.txt");

    public void Error(string stage, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                RotateIfNeeded();

                string code = exception switch
                {
                    Win32Exception win32 => $" win32={win32.NativeErrorCode}",
                    ManagementException management => $" wmi=0x{(uint)management.ErrorCode:X8}",
                    _ => $" hresult=0x{exception.HResult:X8}"
                };
                string safeStage = new(stage.Where(character =>
                    char.IsLetterOrDigit(character) || character is '.' or '-' or '_').ToArray());
                string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
                if (message.Length > 1000) message = message[..1000];
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.UtcNow:O} stage={safeStage} type={exception.GetType().Name}{code} message={message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never hide the original failure.
        }
    }

    public DiagnosticIssue DescribeLatest(string fallbackStage)
    {
        string context = fallbackStage;
        try
        {
            lock (Sync)
            {
                if (File.Exists(LogPath))
                {
                    string? line = File.ReadLines(LogPath).LastOrDefault();
                    string? timestamp = line?.Split(' ', 2)[0];
                    if (DateTimeOffset.TryParse(timestamp, out DateTimeOffset loggedAt) &&
                        DateTimeOffset.UtcNow - loggedAt.ToUniversalTime() <= TimeSpan.FromMinutes(1))
                    {
                        context = $"{line} fallback-stage={fallbackStage}";
                    }
                }
            }
        }
        catch
        {
            // The fallback stage still provides a safe, useful message.
        }

        return DiagnosticIssueCatalog.Describe(context);
    }

    public void RecordTiming(string method, long milliseconds)
    {
        try
        {
            lock (Sync)
            {
                string? directory = Path.GetDirectoryName(TimingPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string safeMethod = new(method.Where(character =>
                    char.IsLetterOrDigit(character) || character is ' ' or '-' or '/' or '.').ToArray());
                File.WriteAllText(TimingPath, $"{safeMethod}|{Math.Max(0, milliseconds)}");
            }
        }
        catch
        {
            // Timing is diagnostic only.
        }
    }

    public DiagnosticTiming? ReadTiming()
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(TimingPath)) return null;
                string[] parts = File.ReadAllText(TimingPath).Split('|', 2);
                bool knownMethod = parts.Length == 2 &&
                    parts[0] is "WMI" or "/bootsequence" or "/set bootsequence";
                return knownMethod && long.TryParse(parts[1], out long milliseconds)
                    ? new DiagnosticTiming(parts[0], milliseconds)
                    : null;
            }
        }
        catch
        {
            return null;
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaximumLogBytes) return;
        File.Move(LogPath, LogPath + ".old", true);
    }
}

public sealed record DiagnosticIssue(string Title, string Message);
public sealed record DiagnosticTiming(string Method, long Milliseconds);

internal static class DiagnosticIssueCatalog
{
    public static DiagnosticIssue Describe(string context)
    {
        string value = context.ToLowerInvariant();

        // Ten concise, actionable problems, selected from the latest log record.
        if (Has(value, "unauthorizedaccessexception", "win32=5", "0x80070005", "access is denied", "privilege"))
            return new("Admin access needed", "Reopen as administrator");

        if (Has(value, "bitlocker"))
            return new("Drive protection unknown", "Keep the recovery key ready");

        if (Has(value, "loader.inspect", "filenotfoundexception", "directorynotfoundexception"))
            return new("Windows unavailable", "Choose another entry");

        if (Has(value, "bcd.verify", "verificationfailed", "not verified", "verified"))
            return new("Boot change not verified", "No boot change was kept");

        if (Has(value, "bcd.rollback", "recover-pending", "canceled-cleanup", "shutdown.canceled"))
            return new("Cleanup not confirmed", "Check the boot order first");

        if (Has(value, "monitor-install", "arm-recovery"))
            return new("Restart safety unavailable", "Restart is disabled");

        if (Has(value, "restart.request", "restart.rejected", "exitwindowsex", "rejected the restart"))
            return new("Restart was blocked", "Close apps and try again");

        if (Has(value, "bcd.prepare", "bcd.write", "bcdedit.", "setobjectlistelement", "writefailed"))
            return new("Can't save boot choice", "Try again");

        if (Has(value, "startup.read", "bcd.validate", "read-boot-state", "open the system bcd"))
            return new("Can't read boot options", "Try again");

        if (Has(value, "managementexception", "comexception", "wmi=", "provider"))
            return new("Boot options unavailable", "Restart Windows and try again");

        return new("Unexpected error", "Try again");
    }

    private static bool Has(string value, params string[] patterns) =>
        patterns.Any(pattern => value.Contains(pattern, StringComparison.Ordinal));
}

public static class AppDiagnostics
{
    public static FileDiagnosticLogger Logger { get; } = new();
}
