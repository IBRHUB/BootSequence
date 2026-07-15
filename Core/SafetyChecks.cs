namespace BootSequence.Core;

public sealed record BitLockerAssessment(
    VolumeProtectionStatus Current,
    VolumeProtectionStatus Target)
{
    public bool ShouldWarn =>
        Current != VolumeProtectionStatus.Unprotected ||
        Target != VolumeProtectionStatus.Unprotected;

    public bool HasUnknown =>
        Current == VolumeProtectionStatus.Unknown ||
        Target == VolumeProtectionStatus.Unknown;
}

public static class SafetyChecks
{
    public static BitLockerAssessment AssessBitLocker(
        IBitLockerService service,
        string currentDrive,
        string targetDrive) =>
        new(
            ReadStatus(service, currentDrive),
            ReadStatus(service, targetDrive));

    private static VolumeProtectionStatus ReadStatus(IBitLockerService service, string drive) =>
        string.IsNullOrWhiteSpace(drive)
            ? VolumeProtectionStatus.Unknown
            : service.GetProtectionStatus(drive);
}
