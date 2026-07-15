using System.Diagnostics;

namespace BootSequence.SystemServices;

public enum BootSelectionMethod
{
    BcdEditBootSequence,
    BcdEditRawBootSequence,
    CurrentWmi
}

public sealed class BcdEditService
{
    public void Apply(BootSelectionMethod method, string targetId)
    {
        string id = NormalizeId(targetId);
        switch (method)
        {
            case BootSelectionMethod.BcdEditBootSequence:
                Run("bcdedit.bootsequence", "/bootsequence", id);
                break;
            case BootSelectionMethod.BcdEditRawBootSequence:
                Run("bcdedit.raw-bootsequence", "/set", "{bootmgr}", "bootsequence", id);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    public void Rename(string targetId, string description)
    {
        string id = NormalizeId(targetId);
        string value = description.Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("The Windows name is empty");
        if (value.Any(char.IsControl))
            throw new InvalidDataException("The Windows name contains control characters");

        Run("bcdedit.rename", "/set", id, "description", value);
    }

    private static string NormalizeId(string value)
    {
        string candidate = value.Trim().Trim('{', '}');
        if (!Guid.TryParse(candidate, out Guid id))
        {
            throw new InvalidDataException("The boot target is not a GUID");
        }

        return $"{{{id:D}}}";
    }

    private static void Run(string stage, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "bcdedit.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"{stage} did not start");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0) return;

        string detail = string.IsNullOrWhiteSpace(error) ? output : error;
        detail = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 300) detail = detail[..300];
        throw new InvalidOperationException($"{stage} exit={process.ExitCode} {detail}");
    }
}
