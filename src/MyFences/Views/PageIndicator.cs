using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

/// <summary>切換桌面頁面時，在畫面下方短暫顯示「第 2 / 3 頁」。</summary>
internal sealed class PageIndicator : Window
{
    private const long WS_EX_TRANSPARENT = 0x00000020;

    private readonly TextBlock _text;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    public PageIndicator()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;

        _text = new TextBlock { Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 10, 24, 12),
            Child = _text,
        };

        SourceInitialized += (_, _) =>
        {
            // 滑鼠點擊穿透、不出現在 Alt+Tab
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) =>
            {
                if (!_hideTimer.IsEnabled)
                    Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    public void ShowMessage(string message)
    {
        _text.Text = message;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Show();
        UpdateLayout();

        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 80;

        _hideTimer.Stop();
        _hideTimer.Start();
    }
}
