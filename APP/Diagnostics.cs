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

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaximumLogBytes) return;
        File.Move(LogPath, LogPath + ".old", true);
    }
}

public static class AppDiagnostics
{
    public static FileDiagnosticLogger Logger { get; } = new();
}
