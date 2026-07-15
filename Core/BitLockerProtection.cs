namespace BootSequence.Core;

public static class BitLockerProtection
{
    public static VolumeProtectionStatus FromProviderStatus(uint status) => status switch
    {
        0 => VolumeProtectionStatus.Unprotected,
        1 => VolumeProtectionStatus.Protected,
        _ => VolumeProtectionStatus.Unknown
    };
}
