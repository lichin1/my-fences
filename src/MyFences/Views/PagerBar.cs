using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MyFences.Services;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

/// <summary>
/// 桌面底部中央的頁面圓點（有兩頁以上才顯示）：點一下切換頁面。
/// 跟分區一樣固定在桌面層，不會蓋住其他視窗。
/// </summary>
internal sealed class PagerBar : Window
{
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal };
    private readonly Border _pill;
    private Action<int>? _onSelect;
    private IntPtr _hwnd;

    public PagerBar()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _pill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 6),
            Child = _dots,
        };
        Content = _pill;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            AttachToDesktop();
        };
        SizeChanged += (_, _) => Reposition();
    }

    public void AttachToDesktop()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        DesktopHelper.GlueToDesktop(_hwnd);
        DesktopHelper.SendToBottom(_hwnd);
    }

    /// <summary>更新圓點；count &lt;= 1 時隱藏。</summary>
    public void Update(int count, int current, Func<int, int> fencesOnPage, Action<int> onSelect)
    {
        _onSelect = onSelect;
        if (count <= 1)
        {
            Hide();
            return;
        }

        bool light = ThemeHelper.IsLightTheme;
        Color accent = ThemeHelper.AccentColor;
        _pill.Background = new SolidColorBrush(light ? Color.FromArgb(0xB0, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xA0, 0x20, 0x20, 0x20));
        var idle = new SolidColorBrush(light ? Color.FromArgb(0x60, 0, 0, 0) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));

        _dots.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            int page = i;
            bool active = page == current;
            var dot = new Border
            {
                // 作用中的頁面是長條，其他是小圓點
                Width = active ? 22 : 10,
                Height = 10,
                Margin = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(5),
                Background = active ? new SolidColorBrush(accent) : idle,
                Cursor = Cursors.Hand,
                ToolTip = $"第 {page + 1} 頁（{fencesOnPage(page)} 個分區）",
            };

            // 圓點很小，外面包一層透明的大範圍比較好點
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 4, 0, 4), Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, _) => _onSelect?.Invoke(page);
            _dots.Children.Add(hit);
        }

        if (!IsVisible)
            Show();
        Reposition();
    }

    private void Reposition()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 10;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
            DesktopHelper.KeepJustAboveDesktopIcons(hwnd, lParam); // 跟分區一樣在桌面圖示那一層的正上方

        return IntPtr.Zero;
    }
}
