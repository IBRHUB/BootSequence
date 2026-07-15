using System.Management;
using BootSequence.Core;

namespace BootSequence.SystemServices;

public sealed class BitLockerService : IBitLockerService
{
    public VolumeProtectionStatus GetProtectionStatus(string requestedDrive)
    {
        string drive = NormalizeDrive(requestedDrive);
        if (string.IsNullOrEmpty(drive)) return VolumeProtectionStatus.Unknown;

        var scope = new ManagementScope(@"\\.\ROOT\CIMV2\Security\MicrosoftVolumeEncryption",
            new ConnectionOptions
            {
                EnablePrivileges = true,
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy
            });
        scope.Connect();

        using var searcher = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));
        using ManagementObjectCollection volumes = searcher.Get();
        foreach (ManagementObject volume in volumes)
        {
            using (volume)
            {
                if (!string.Equals(
                    NormalizeDrive(volume["DriveLetter"] as string ?? string.Empty),
                    drive,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using ManagementBaseObject output = volume.InvokeMethod("GetProtectionStatus", null, null)
                    ?? throw new ManagementException("BitLocker status returned no result");
                uint result = Convert.ToUInt32(output["ReturnValue"]);
                uint status = Convert.ToUInt32(output["ProtectionStatus"]);
                if (result != 0)
                {
                    throw new ManagementException($"BitLocker GetProtectionStatus failed with 0x{result:X8}");
                }

                return BitLockerProtection.FromProviderStatus(status);
            }
        }

        return VolumeProtectionStatus.Unknown;
    }

    private static string NormalizeDrive(string drive) =>
        drive.Length >= 2 && drive[1] == ':' ? drive[..2].ToUpperInvariant() : string.Empty;
}
