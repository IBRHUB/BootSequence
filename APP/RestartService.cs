using System.ComponentModel;
using System.Runtime.InteropServices;
using BootSequence.Core;

namespace BootSequence.SystemServices;

public sealed class RestartService : IRestartService
{
    private const uint EwxReboot = 0x00000002;
    private const uint ReasonMajorApplication = 0x00040000;
    private const uint ReasonMinorOther = 0x00000000;
    private const uint ReasonFlagPlanned = 0x80000000;

    public bool Restart()
    {
        NativePrivileges.EnableOrThrow("SeShutdownPrivilege");
        uint reason = ReasonMajorApplication | ReasonMinorOther | ReasonFlagPlanned;
        if (!ExitWindowsEx(EwxReboot, reason))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Windows rejected the restart request");
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}
