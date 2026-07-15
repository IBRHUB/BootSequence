namespace BootSequence.SystemServices;

public sealed class PendingRestartJournal
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BootSequence",
        "pending-restart.txt");

    public void Arm(string targetId)
    {
        if (!TryNormalizeId(targetId, out string normalized))
        {
            throw new InvalidDataException("The pending boot target is not a GUID");
        }

        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The recovery directory is unavailable");
        Directory.CreateDirectory(directory);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, normalized);
        File.Move(temporaryPath, _path, true);
    }

    public string? Read()
    {
        if (!File.Exists(_path)) return null;
        string value = File.ReadAllText(_path).Trim();
        if (!TryNormalizeId(value, out string normalized))
        {
            throw new InvalidDataException("The pending restart journal is invalid");
        }

        return normalized;
    }

    public void Clear()
    {
        File.Delete(_path);
        File.Delete(_path + ".tmp");
    }

    private static bool TryNormalizeId(string value, out string normalized)
    {
        string candidate = value.Trim().Trim('{', '}');
        if (Guid.TryParse(candidate, out Guid id))
        {
            normalized = $"{{{id:D}}}";
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
