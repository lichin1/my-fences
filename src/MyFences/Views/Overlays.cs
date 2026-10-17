using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyFences.Models;
using MyFences.Services;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

/// <summary>最上層、不搶焦點、不出現在 Alt+Tab 的小視窗；clickThrough 時滑鼠會穿透。</summary>
internal class OverlayWindow : Window
{
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    protected OverlayWindow(bool clickThrough)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (clickThrough)
                ex |= WS_EX_TRANSPARENT;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    protected IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>用螢幕像素擺放（多螢幕、不同 DPI 都準）。</summary>
    public void PlaceAt(PixelRect rect)
    {
        if (!IsVisible)
            Show();
        SetWindowPos(Handle, HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOACTIVATE);
    }

    protected void MoveTo(int x, int y)
    {
        if (!IsVisible)
            Show();
        SetWindowPos(Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
    }

    protected static Color Accent => ThemeHelper.AccentColor;
}

/// <summary>拖曳分區時的對齊線（一條直線、一條橫線）。</summary>
internal static class SnapGuides
{
    private static LineWindow? _vertical;
    private static LineWindow? _horizontal;

    public static void Show(int? x, int? y, PixelRect area)
    {
        _vertical ??= new LineWindow();
        _horizontal ??= new LineWindow();

        if (x is { } gx)
            _vertical.PlaceAt(new PixelRect(gx - 1, area.Y, 2, area.Height));
        else
            _vertical.Hide();

        if (y is { } gy)
            _horizontal.PlaceAt(new PixelRect(area.X, gy - 1, area.Width, 2));
        else
            _horizontal.Hide();
    }

    public static void Hide()
    {
        _vertical?.Hide();
        _horizontal?.Hide();
    }

    private sealed class LineWindow : OverlayWindow
    {
        public LineWindow() : base(clickThrough: true)
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xCC, Accent.R, Accent.G, Accent.B)) };
        }
    }
}

/// <summary>在桌面上 Ctrl+拖曳時畫出的新分區範圍。</summary>
internal sealed class DesktopSelectionOverlay : OverlayWindow
{
    private readonly TextBlock _label;

    public DesktopSelectionOverlay() : base(clickThrough: true)
    {
        _label = new TextBlock
        {
            Text = "放開滑鼠建立分區",
            Foreground = Brushes.White,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.9 },
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B)),
            BorderBrush = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Child = _label,
        };
    }

    public void Update(PixelRect rect, bool bigEnough)
    {
        _label.Text = bigEnough ? "放開滑鼠建立分區" : "再拉大一點";
        PlaceAt(rect);
    }
}

/// <summary>跟著游標的小提示，例如「放開即移出分區」。</summary>
internal sealed class CursorHint : OverlayWindow
{
    private static CursorHint? _instance;
    private readonly TextBlock _text;

    private CursorHint() : base(clickThrough: true)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        _text = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 6),
            Child = _text,
        };
    }

    public static void ShowNearCursor(string text)
    {
        if (!GetCursorPos(out POINT pt))
            return;

        _instance ??= new CursorHint();
        _instance._text.Text = text;
        _instance.MoveTo(pt.x + 18, pt.y + 22);
    }

    public static void HideHint() => _instance?.Hide();
}

/// <summary>右下角的提示，可以附一個按鈕（例如「復原」），幾秒後自動消失。</summary>
internal sealed class Toast : OverlayWindow
{
    private static Toast? _instance;
    private readonly TextBlock _text;
    private readonly Button _action;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private Action? _onAction;

    private Toast() : base(clickThrough: false)
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        bool light = ThemeHelper.IsLightTheme;
        Brush fore = light ? new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)) : Brushes.White;

        _text = new TextBlock { Foreground = fore, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
        _action = new Button
        {
            Margin = new Thickness(16, 0, 0, 0),
            Padding = new Thickness(14, 4, 14, 5),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Accent),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        _action.Click += (_, _) =>
        {
            _onAction?.Invoke();
            Hide();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_text);
        panel.Children.Add(_action);
        Content = new Border
        {
            Background = new SolidColorBrush(light ? Color.FromArgb(0xF5, 0xF9, 0xF9, 0xF9) : Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B)),
            BorderBrush = new SolidColorBrush(light ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 10, 10),
            Child = panel,
        };

        _timer.Tick += (_, _) =>
        {
            // 滑鼠停在提示上就先不要關
            if (IsMouseOver)
                return;
            _timer.Stop();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
            var close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            close.Tick += (_, _) =>
            {
                close.Stop();
                if (!_timer.IsEnabled)
                    Hide();
            };
            close.Start();
        };
    }

    public static void ShowMessage(string message, string? actionText = null, Action? onAction = null)
    {
        _instance ??= new Toast();
        Toast t = _instance;
        t._text.Text = message;
        t._onAction = onAction;
        t._action.Content = actionText;
        t._action.Visibility = actionText == null ? Visibility.Collapsed : Visibility.Visible;

        t.BeginAnimation(OpacityProperty, null);
        t.Opacity = 1;
        if (!t.IsVisible)
        {
            // 先放到畫面外，量好大小再移到右下角，免得第一次出現時閃一下
            t.Left = -10000;
            t.Top = -10000;
        }

        t.Show();
        t.UpdateLayout();
        Rect area = SystemParameters.WorkArea;
        t.Left = area.Right - t.ActualWidth - 16;
        t.Top = area.Bottom - t.ActualHeight - 16;

        t._timer.Stop();
        t._timer.Start();
    }
}
