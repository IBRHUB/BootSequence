namespace BootSequence.Core;

public enum BootEntryAvailability
{
    Available,
    InvalidConfiguration,
    UnresolvedDevice,
    LoaderMissing,
    InspectionFailed
}

public sealed record BootEntry(
    string Id,
    string Name,
    string Disk,
    string ApplicationPath,
    bool IsCurrent,
    BootEntryAvailability Availability)
{
    public bool IsSelectable => Availability == BootEntryAvailability.Available;
}

public enum VolumeProtectionStatus
{
    Unprotected,
    Protected,
    Unknown
}

public enum BootSequenceMutationResult
{
    Written,
    PendingExists,
    PersistentSequence,
    WriteFailed,
    VerificationFailed
}

public interface IBootConfigurationService
{
    IReadOnlyList<BootEntry> ReadEntries();
    IReadOnlyList<string> ReadOneTimeSequence();
    BootSequenceMutationResult TrySetOneTimeSequenceIfEmpty(string id);
    bool ClearOneTimeSequenceIfMatches(string id);
}

public interface IBitLockerService
{
    VolumeProtectionStatus GetProtectionStatus(string drive);
}

public interface IRestartService
{
    bool Restart();
}

public interface IDiagnosticLogger
{
    void Error(string stage, Exception exception);
}

public enum PrepareResult
{
    Ready,
    PendingExists,
    PersistentSequence,
    InvalidTarget,
    WriteFailed,
    VerificationFailed
}
