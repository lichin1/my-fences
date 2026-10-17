using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using MyFences.Models;
using MyFences.Services;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

/// <summary>
/// 分區的底色：放在「桌布層」（桌面圖示後面），所以真正的桌面圖示會畫在它上面、也點得到。
/// 用最單純的 Win32 子視窗 + 半透明，避免 WPF 透明視窗在別的程式底下的相容問題。
/// 找不到桌布層時就不顯示（分區照常運作，只是沒有底色）。
/// </summary>
internal sealed class FenceBackdrop : IDisposable
{
    private const string ClassName = "MyFencesBackdrop";
    private const int WM_ERASEBKGND = 0x0014;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_HIDE = 0;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static WndProcDelegate? _wndProc; // 留著參照，免得被 GC
    private static bool _classRegistered;

    private IntPtr _hwnd;
    private IntPtr _parent;
    private IntPtr _brush;
    private uint _color;

    /// <summary>body = 要畫底色的螢幕範圍（null = 隱藏）。</summary>
    public void Update(PixelRect? body, Color color, double opacity, int cornerRadius)
    {
        if (body is not { } rect || rect.Width <= 0 || rect.Height <= 0)
        {
            Hide();
            return;
        }

        if (!EnsureWindow())
            return;

        SetColor(color);

        var origin = new POINT { x = rect.X, y = rect.Y };
        ScreenToClient(_parent, ref origin);
        SetWindowPos(_hwnd, IntPtr.Zero, origin.x, origin.y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Clamp(opacity * 255, 1, 255), LWA_ALPHA);

        // 只有下方兩個角是圓的（上面接著標題列）：做一個比較高的圓角矩形，上緣超出視窗
        IntPtr region = CreateRoundRectRgn(0, -cornerRadius * 2, rect.Width + 1, rect.Height + 1, cornerRadius * 2, cornerRadius * 2);
        SetWindowRgn(_hwnd, region, true); // 系統會接手 region，不用自己刪
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, SW_HIDE);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Brushes.Remove(_hwnd);
            DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;
        if (_brush != IntPtr.Zero)
            DeleteObject(_brush);
        _brush = IntPtr.Zero;
    }

    private bool EnsureWindow()
    {
        // 檔案總管重新啟動後桌布層會換一個，要重建
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && IsWindow(_parent))
            return true;

        if (_hwnd != IntPtr.Zero)
        {
            Brushes.Remove(_hwnd);
            DestroyWindow(_hwnd);
        }

        _hwnd = IntPtr.Zero;

        _parent = DesktopHelper.GetWallpaperLayer();
        if (_parent == IntPtr.Zero || !RegisterClass())
            return false;

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE, ClassName, "", WS_CHILD,
            0, 0, 1, 1, _parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd != IntPtr.Zero && _brush != IntPtr.Zero)
            Brushes[_hwnd] = _brush; // 重建視窗時沿用原本的顏色
        return _hwnd != IntPtr.Zero;
    }

    private void SetColor(Color color)
    {
        uint colorRef = (uint)(color.R | (color.G << 8) | (color.B << 16));
        if (_brush != IntPtr.Zero && colorRef == _color)
            return;

        if (_brush != IntPtr.Zero)
            DeleteObject(_brush);
        _brush = CreateSolidBrush(colorRef);
        _color = colorRef;
        Brushes[_hwnd] = _brush;
        InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    // 每個底色視窗用自己的筆刷畫背景
    private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> Brushes = new();

    private static bool RegisterClass()
    {
        if (_classRegistered)
            return true;

        _wndProc = (hwnd, msg, wParam, lParam) =>
        {
            if (msg == WM_ERASEBKGND && Brushes.TryGetValue(hwnd, out IntPtr brush))
            {
                GetClientRect(hwnd, out RECT rc);
                FillRect(wParam, ref rc, brush);
                return new IntPtr(1);
            }

            return DefWindowProc(hwnd, msg, wParam, lParam);
        };

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        _classRegistered = RegisterClassEx(ref wc) != 0;
        return _classRegistered;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
