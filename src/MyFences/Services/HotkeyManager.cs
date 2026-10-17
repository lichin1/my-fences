using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>全域快速鍵（RegisterHotKey），用一個 message-only 視窗接收 WM_HOTKEY。</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("MyFencesHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public bool Register(ModifierKeys modifiers, Key key, Action action)
    {
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        // WPF 的 ModifierKeys 數值剛好等於 MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN
        if (!RegisterHotKey(_source.Handle, id, (uint)modifiers | MOD_NOREPEAT, vk))
            return false;

        _actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
            UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }
}
