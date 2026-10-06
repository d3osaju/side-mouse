using System.ComponentModel;
using System.Runtime.InteropServices;
using static SideMouse.Native;

namespace SideMouse;

/// <summary>
/// Global low-level mouse hook. The handler returns true to swallow the event so the
/// game (and everything else) never sees it.
/// </summary>
sealed class MouseHook : IDisposable
{
    readonly HookProc _proc; // keep a reference so the GC doesn't collect the delegate
    IntPtr _hook;

    public Func<int, MSLLHOOKSTRUCT, bool>? Handler { get; set; }

    public MouseHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Handler is { } handler)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (handler((int)wParam, data)) return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
