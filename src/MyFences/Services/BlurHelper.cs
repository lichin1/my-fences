using System;
using System.Runtime.InteropServices;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>視窗背後的毛玻璃效果（模糊桌布）。不支援的系統上呼叫也不會出錯，只是沒效果。</summary>
internal static class BlurHelper
{
    public static void SetBlur(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var accent = new ACCENT_POLICY { AccentState = enabled ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attrib = WCA_ACCENT_POLICY, pvData = ptr, cbData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (EntryPointNotFoundException)
        {
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
