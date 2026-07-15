using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BootSequence.SystemServices;

public sealed class ShutdownMonitor : IDisposable
{
    private const uint WmEndSession = 0x0016;
    private const nuint SubclassId = 0x425357;
    private readonly nint _windowHandle;
    private readonly SubclassProc _windowProcedure;
    private bool _installed;

    public ShutdownMonitor(nint windowHandle)
    {
        _windowHandle = windowHandle;
        _windowProcedure = WindowProcedure;
        _installed = SetWindowSubclass(_windowHandle, _windowProcedure, SubclassId, 0);
        if (!_installed)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Cannot monitor Windows shutdown messages");
        }
    }

    public event EventHandler? ShutdownCanceled;

    private nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmEndSession && wParam == 0)
        {
            ShutdownCanceled?.Invoke(this, EventArgs.Empty);
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_installed) return;
        RemoveWindowSubclass(_windowHandle, _windowProcedure, SubclassId);
        _installed = false;
    }

    private delegate nint SubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProcedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProcedure,
        nuint subclassId);
}
