using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using MyFences.Models;
using MyFences.Services;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

public partial class FenceWindow : Window
{
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(FenceWindow), new PropertyMetadata(48.0));

    public static readonly DependencyProperty CellWidthProperty =
        DependencyProperty.Register(nameof(CellWidth), typeof(double), typeof(FenceWindow), new PropertyMetadata(80.0));

    private const double RolledUpHeight = 32; // 標題列 30 + 上下框線
    private const string InternalDragFormat = "MyFences.Item";

    private static readonly (string Name, string Color)[] ColorPresets =
    {
        ("跟隨系統（淺色 / 深色）", ThemeHelper.SystemColor), ("系統強調色", ThemeHelper.AccentColorKey),
        ("黑", "#1E1E1E"), ("深灰", "#3A3A3A"), ("藍", "#1F4E79"), ("綠", "#2E5E3A"),
        ("紫", "#4B2E6B"), ("紅", "#6B2E2E"), ("棕", "#5C4326"),
    };

    private static readonly (string Name, double Size)[] IconSizePresets = { ("小", 32), ("中", 48), ("大", 64) };

    private readonly ObservableCollection<FenceItem> _items = new();
    private readonly DispatcherTimer _portalReloadTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private FileSystemWatcher? _portalWatcher;
    private IntPtr _hwnd;
    private bool _hoverExpanded;
    private Point _dragStart;
    private FenceItem? _pressedItem;

    public FenceWindow(FenceConfig config)
    {
        InitializeComponent();
        Config = config;

        Left = config.Left;
        Top = config.Top;
        Width = config.Width;
        Height = config.RolledUp ? RolledUpHeight : config.Height;

        ItemsList.ItemsSource = _items;
        _portalReloadTimer.Tick += (_, _) =>
        {
            _portalReloadTimer.Stop();
            ReloadItems();
        };

        ApplyAppearance();
        ApplyChrome();
        ReloadItems();
        SetupPortalWatcher();

        SourceInitialized += (_, _) => AttachToDesktop();
        _hoverExpandTimer.Tick += (_, _) =>
        {
            _hoverExpandTimer.Stop();
            if (IsMouseOver)
                ExpandTemporarily();
        };
        LocationChanged += (_, _) => SaveBounds();
        SizeChanged += (_, _) => SaveBounds();
    }

    public FenceConfig Config { get; }
    public bool IsClosingIntentionally { get; private set; }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double CellWidth
    {
        get => (double)GetValue(CellWidthProperty);
        set => SetValue(CellWidthProperty, value);
    }

    private bool IsStandard => Config.Kind == FenceKind.Standard;

    public void CloseIntentionally()
    {
        IsClosingIntentionally = true;
        Close();
    }

    /// <summary>從工作列 / Alt+Tab 隱藏，並固定在桌面層。</summary>
    public void AttachToDesktop()
    {
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
        }

        DesktopHelper.GlueToDesktop(_hwnd);
        DesktopHelper.SendToBottom(_hwnd);
        BlurHelper.SetBlur(_hwnd, Config.Blur);
    }

    /// <summary>視窗在螢幕上的實際像素位置（多螢幕換算用）。</summary>
    public PixelRect? GetPixelBounds()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out RECT r))
            return null;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>新分區放到螢幕像素範圍（高度依這台螢幕的 DPI 換成 WPF 單位）。</summary>
    public void PlaceAtPixels(PixelRect rect)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        ApplyPixelBounds(rect, rect.Height / (scale > 0 ? scale : 1));
    }

    /// <summary>依像素位置擺放分區；收合中的分區只改位置和寬度，展開高度另外給。</summary>
    public void ApplyPixelBounds(PixelRect rect, double expandedHeight)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _hoverExpanded = false;
        Config.Height = Math.Max(MinHeight, expandedHeight);
        SetWindowPos(_hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        Height = Config.RolledUp ? RolledUpHeight : Config.Height;
        SaveBounds();
    }

    public void ApplyAppearance()
    {
        Color baseColor = ThemeHelper.Resolve(Config.Color);
        bool lightBackground = ThemeHelper.IsLight(baseColor);
        Color accent = ThemeHelper.AccentColor;

        // alpha 至少 1：完全透明的像素會讓滑鼠點擊穿透到桌面
        byte bodyAlpha = (byte)Math.Clamp(Config.Opacity * 255, 1, 255);
        byte titleAlpha = (byte)Math.Clamp(Config.Opacity * 255 + 50, 70, 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(bodyAlpha, baseColor.R, baseColor.G, baseColor.B));
        TitleBar.Background = new SolidColorBrush(Color.FromArgb(titleAlpha, baseColor.R, baseColor.G, baseColor.B));
        RootBorder.BorderBrush = Brush(lightBackground ? 0x30000000u : 0x40FFFFFFu);

        // 淺色背景用深色字（白色光暈），深色背景用白字（黑色陰影）
        Resources["FenceText"] = lightBackground ? Brush(0xFF1B1B1Bu) : Brushes.White;
        Resources["FenceSubtleText"] = Brush(lightBackground ? 0x99000000u : 0xB0FFFFFFu);
        Resources["ItemHover"] = Brush(lightBackground ? 0x14000000u : 0x22FFFFFFu);
        Resources["ButtonHover"] = Brush(lightBackground ? 0x18000000u : 0x33FFFFFFu);
        Resources["ButtonPressed"] = Brush(lightBackground ? 0x30000000u : 0x55FFFFFFu);
        Resources["ItemSelected"] = new SolidColorBrush(Color.FromArgb(0x45, accent.R, accent.G, accent.B));
        Resources["ItemSelectedBorder"] = new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B));
        Resources["AccentBrush"] = new SolidColorBrush(accent);
        Resources["TextShadow"] = lightBackground
            ? new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 0.7, Color = Colors.White }
            : new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.9, Color = Colors.Black };

        // 彈出面板跟著 Windows 的淺色 / 深色模式
        bool lightSystem = ThemeHelper.IsLightTheme;
        Resources["PanelBackground"] = Brush(lightSystem ? 0xF5F9F9F9u : 0xF02B2B2Bu);
        Resources["PanelText"] = lightSystem ? Brush(0xFF1B1B1Bu) : Brushes.White;
        Resources["PanelBorder"] = Brush(lightSystem ? 0x22000000u : 0x30FFFFFFu);

        // 毛玻璃是整個矩形視窗，圓角會露出模糊的直角，所以開毛玻璃時改成直角
        RootBorder.CornerRadius = new CornerRadius(Config.Blur ? 0 : 8);
        TitleBar.CornerRadius = Config.Blur ? new CornerRadius(0) : new CornerRadius(7, 7, 0, 0);
        BlurHelper.SetBlur(_hwnd, Config.Blur);
        UpdateFade(animate: false);

        TitleText.Text = Config.Name;
        IconSize = Config.IconSize;
        CellWidth = Config.IconSize + 36;
        EmptyHint.Text = IsStandard ? "把桌面上的檔案拖進來" : "資料夾是空的";
        AddTabButton.Visibility = IsStandard ? Visibility.Visible : Visibility.Collapsed;
        RollUpButton.Content = Config.RolledUp ? "\uE70D" : "\uE70E";
        RollUpButton.ToolTip = Config.RolledUp ? "展開" : "收合";
    }

    private static SolidColorBrush Brush(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    /// <summary>依鎖定 / 收合狀態設定可否縮放。</summary>
    public void ApplyChrome()
    {
        bool resizable = !App.Current.Config.Locked && !Config.RolledUp;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(resizable ? 6 : 0),
            UseAeroCaptionButtons = false,
        });
    }

    public void ReloadItems()
    {
        _items.Clear();

        List<string> paths;
        if (IsStandard)
            paths = Config.CurrentItems.ToList();
        else if (Config.FolderPath is { } folder && Directory.Exists(folder))
            paths = DesktopHelper.EnumerateVisibleEntries(folder);
        else
            paths = new List<string>();

        foreach (string path in paths)
            _items.Add(FenceItem.Create(path));

        RebuildTabs();

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!IsStandard && !Directory.Exists(Config.FolderPath ?? ""))
        {
            EmptyHint.Text = "找不到資料夾：" + Config.FolderPath;
            EmptyHint.Visibility = Visibility.Visible;
        }

        // 圖示在背景慢慢載入，項目多時不會卡住畫面
        foreach (FenceItem item in _items.ToList())
            Dispatcher.InvokeAsync(() => item.Icon ??= IconHelper.GetIcon(item.FullPath), DispatcherPriority.Background);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 擋掉 Alt+F4 之類誤關；要刪除分區請用選單
        if (!IsClosingIntentionally && !App.Current.IsExiting)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _portalWatcher?.Dispose();
        _portalReloadTimer.Stop();
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_MOVING or WM_SIZING)
        {
            SnapWhileDragging(msg == WM_MOVING ? SnapEdges.All : EdgesForSizing(wParam.ToInt32()), lParam);
            handled = true;
            return new IntPtr(1);
        }

        if (msg == WM_EXITSIZEMOVE)
            SnapGuides.Hide();

        if (msg == WM_WINDOWPOSCHANGING)
        {
            // 不管被誰啟動，都維持在最下層（桌面圖示之上、一般視窗之下）
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>拖曳 / 縮放中：吸附到螢幕邊緣與其他分區，並顯示對齊線（按住 Alt 可暫時關閉吸附）。</summary>
    private void SnapWhileDragging(SnapEdges edges, IntPtr lParam)
    {
        var r = Marshal.PtrToStructure<RECT>(lParam);
        var rect = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        System.Drawing.Rectangle wa = System.Windows.Forms.Screen.FromRectangle(
            new System.Drawing.Rectangle(rect.X, rect.Y, rect.Width, rect.Height)).WorkingArea;
        var area = new PixelRect(wa.X, wa.Y, wa.Width, wa.Height);

        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || edges == SnapEdges.None)
        {
            SnapGuides.Hide();
            return;
        }

        SnapResult snapped = LayoutMath.Snap(rect, App.Current.GetVisibleFenceBounds(this), area, edges);
        r.Left = snapped.Rect.X;
        r.Top = snapped.Rect.Y;
        r.Right = snapped.Rect.Right;
        r.Bottom = snapped.Rect.Bottom;
        Marshal.StructureToPtr(r, lParam, false);
        SnapGuides.Show(snapped.GuideX, snapped.GuideY, area);
    }

    private static SnapEdges EdgesForSizing(int wmsz) => wmsz switch
    {
        1 => SnapEdges.Left,
        2 => SnapEdges.Right,
        3 => SnapEdges.Top,
        4 => SnapEdges.Top | SnapEdges.Left,
        5 => SnapEdges.Top | SnapEdges.Right,
        6 => SnapEdges.Bottom,
        7 => SnapEdges.Bottom | SnapEdges.Left,
        8 => SnapEdges.Bottom | SnapEdges.Right,
        _ => SnapEdges.None,
    };

    private void SaveBounds()
    {
        // 建構時設定 Left/Top 也會觸發，handle 建立前的都不算
        if (_hwnd == IntPtr.Zero || WindowState != WindowState.Normal)
            return;

        Config.Left = Left;
        Config.Top = Top;
        Config.Width = Width;
        if (!Config.RolledUp && !_hoverExpanded)
            Config.Height = Height;
        App.Current.RequestSave();
    }

    /// <summary>「平常淡化」：滑鼠不在分區上時變淡。</summary>
    private void UpdateFade(bool animate = true)
    {
        bool menuOpen = RootBorder.ContextMenu?.IsOpen == true;
        double target = Config.FadeWhenIdle && !IsMouseOver && !menuOpen ? 0.3 : 1.0;
        if (!animate)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(target < 1 ? 400 : 150)));
    }

    // ───────────── 標題列按鈕 ─────────────

    private void UpdateTitleButtons()
    {
        bool show = IsMouseOver || AppearancePopup.IsOpen || TitleEditor.IsVisible || RootBorder.ContextMenu?.IsOpen == true;
        TitleButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(150)));
        TitleButtons.IsHitTestVisible = show;
    }

    public void BeginRename()
    {
        // 剛建立的分區還沒載入完成時，等載入後再進入改名
        if (!IsLoaded)
        {
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                Loaded -= onLoaded;
                BeginRename();
            };
            Loaded += onLoaded;
            return;
        }

        TitleEditor.Text = Config.Name;
        TitleEditor.Visibility = Visibility.Visible;
        TitleText.Visibility = Visibility.Hidden;
        Activate();
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (TitleEditor.Visibility != Visibility.Visible)
            return;

        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (commit && !string.IsNullOrWhiteSpace(TitleEditor.Text))
        {
            Config.Name = TitleEditor.Text.Trim();
            ApplyAppearance();
            App.Current.RequestSave();
        }

        UpdateTitleButtons();
    }

    private void OnTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndRename(commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename(commit: false);
            e.Handled = true;
        }
    }

    private void OnTitleEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(commit: true);

    private void OnAddTabClick(object sender, RoutedEventArgs e) => AddTab();

    private void OnRollUpClick(object sender, RoutedEventArgs e) => ToggleRollUp();

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = BuildFenceMenu();
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Closed += (_, _) =>
        {
            UpdateTitleButtons();
            UpdateFade();
        };
        RootBorder.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ───────────── 外觀面板 ─────────────

    private bool _updatingPanel;

    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        _updatingPanel = true;
        ColorSwatches.Children.Clear();
        foreach ((string name, string color) in ColorPresets)
        {
            bool selected = string.Equals(Config.Color, color, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(ThemeHelper.Resolve(color)),
                BorderThickness = new Thickness(selected ? 2.5 : 1),
                Cursor = Cursors.Hand,
                ToolTip = name,
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "PanelBorder");
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                SetColor(color);
                OnAppearanceClick(sender, e); // 重畫選取框
            };
            ColorSwatches.Children.Add(swatch);
        }

        // 「＋」：用 Windows 的調色盤自選顏色（目前是自訂色時，就顯示那個顏色並框起來）
        bool custom = !ColorPresets.Any(c => string.Equals(c.Color, Config.Color, StringComparison.OrdinalIgnoreCase));
        var plus = new Border
        {
            Width = 24,
            Height = 24,
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(12),
            Background = custom ? new SolidColorBrush(ThemeHelper.Resolve(Config.Color)) : Brushes.Transparent,
            BorderThickness = new Thickness(custom ? 2.5 : 1),
            Cursor = Cursors.Hand,
            ToolTip = "自選顏色…",
            Child = new TextBlock
            {
                Text = custom ? "" : "\uE710",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        plus.SetResourceReference(Border.BorderBrushProperty, custom ? "AccentBrush" : "PanelBorder");
        ((TextBlock)plus.Child).SetResourceReference(TextBlock.ForegroundProperty, "PanelText");
        plus.MouseLeftButtonUp += (_, _) =>
        {
            AppearancePopup.IsOpen = false;
            PickCustomColor();
        };
        ColorSwatches.Children.Add(plus);

        OpacitySlider.Value = Math.Round(Config.Opacity * 100);
        OpacityLabel.Text = $"背景不透明度 {OpacitySlider.Value:0}%";

        IconSizeButtons.Children.Clear();
        foreach ((string name, double size) in IconSizePresets)
        {
            bool selected = Math.Abs(Config.IconSize - size) < 0.5;
            var button = new Border
            {
                Padding = new Thickness(14, 3, 14, 4),
                Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = name, FontSize = 12 },
            };
            if (selected)
            {
                button.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                button.BorderBrush = Brushes.Transparent;
                ((TextBlock)button.Child).Foreground = Brushes.White;
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.SetResourceReference(Border.BorderBrushProperty, "PanelBorder");
                ((TextBlock)button.Child).SetResourceReference(TextBlock.ForegroundProperty, "PanelText");
            }
            button.MouseLeftButtonUp += (_, _) =>
            {
                SetIconSize(size);
                OnAppearanceClick(sender, e);
            };
            IconSizeButtons.Children.Add(button);
        }

        BlurCheck.IsChecked = Config.Blur;
        FadeCheck.IsChecked = Config.FadeWhenIdle;
        _updatingPanel = false;

        AppearancePopup.IsOpen = true;
        UpdateTitleButtons();
    }

    private void OnAppearancePopupClosed(object? sender, EventArgs e)
    {
        UpdateTitleButtons();
        UpdateFade();
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingPanel)
            return;

        OpacityLabel.Text = $"背景不透明度 {e.NewValue:0}%";
        SetOpacity(Math.Round(e.NewValue) / 100.0);
    }

    private void OnBlurCheckClick(object sender, RoutedEventArgs e)
    {
        if (BlurCheck.IsChecked != Config.Blur)
            ToggleBlur();
    }

    private void OnFadeCheckClick(object sender, RoutedEventArgs e)
    {
        if (FadeCheck.IsChecked != Config.FadeWhenIdle)
            ToggleFade();
    }

    // ───────────── 分頁 ─────────────

    // 分頁標籤也跟著分區的明暗配色（用動態資源）
    private const string ActiveTabBrush = "ButtonPressed";
    private const string InactiveTabBrush = "ItemHover";

    /// <summary>正在原地改名的分頁（null = 沒有）。</summary>
    private int? _editingTab;

    private void RebuildTabs()
    {
        TabStrip.Children.Clear();
        if (!IsStandard || !Config.HasTabs)
        {
            TabStrip.Visibility = Visibility.Collapsed;
            return;
        }

        TabStrip.Visibility = Visibility.Visible;
        _ = Config.CurrentItems; // 修正超出範圍的 ActiveTab
        for (int i = 0; i < Config.Tabs.Count; i++)
            TabStrip.Children.Add(BuildTabHeader(i));

        var add = new Button
        {
            Style = (Style)FindResource("TitleButton"),
            Content = "\uE710",
            Height = 22,
            ToolTip = "新增分頁",
        };
        add.Click += (_, _) => AddTab();
        TabStrip.Children.Add(add);
    }

    /// <summary>
    /// 分頁標籤：點一下切換、雙擊原地改名、滑鼠移上去出現 ✕ 可刪除、可以把圖示拖到上面。
    /// </summary>
    private Border BuildTabHeader(int index)
    {
        FenceTab tab = Config.Tabs[index];
        bool active = index == Config.ActiveTab;
        var header = new Border
        {
            Tag = index,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2, 4, 3),
            Margin = new Thickness(0, 0, 4, 3),
            Cursor = Cursors.Hand,
            AllowDrop = true,
            ToolTip = $"{tab.Items.Count} 個項目・雙擊改名・把圖示拖到這裡就能移過來",
        };
        header.SetResourceReference(Border.BackgroundProperty, active ? ActiveTabBrush : InactiveTabBrush);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        header.Child = content;

        if (_editingTab == index)
        {
            var editor = new TextBox
            {
                Text = tab.Name,
                MinWidth = 60,
                Padding = new Thickness(2, 0, 2, 0),
                FontSize = 12,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            editor.SetResourceReference(ForegroundProperty, "FenceText");
            editor.SetResourceReference(TextBox.CaretBrushProperty, "FenceText");
            editor.SetResourceReference(BorderBrushProperty, "AccentBrush");
            editor.Loaded += (_, _) =>
            {
                Activate();
                editor.Focus();
                editor.SelectAll();
            };
            editor.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Escape)
                {
                    EndTabRename(index, e.Key == Key.Enter ? editor.Text : null);
                    e.Handled = true;
                }
            };
            editor.LostKeyboardFocus += (_, _) => EndTabRename(index, editor.Text);
            content.Children.Add(editor);
            header.Padding = new Thickness(6, 2, 6, 3);
            return header;
        }

        var label = new TextBlock
        {
            Text = tab.Name,
            FontSize = 12,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "FenceText");
        label.SetResourceReference(EffectProperty, "TextShadow");
        content.Children.Add(label);

        // 滑鼠移到標籤上才出現的 ✕
        var close = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Opacity = 0,
            ToolTip = "刪除分頁",
            Child = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ((TextBlock)close.Child).SetResourceReference(TextBlock.ForegroundProperty, "FenceText");
        close.MouseEnter += (_, _) => close.SetResourceReference(Border.BackgroundProperty, "ButtonPressed");
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true; // 不要觸發切換 / 改名
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            DeleteTab(index);
        };
        content.Children.Add(close);

        header.MouseEnter += (_, _) => close.Opacity = 1;
        header.MouseLeave += (_, _) => close.Opacity = 0;
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                BeginTabRename(index);
                e.Handled = true;
            }
        };
        header.MouseLeftButtonUp += (_, e) =>
        {
            SwitchTab(index);
            e.Handled = true;
        };
        header.DragOver += (_, e) =>
        {
            e.Effects = DropEffectFor(e.Data);
            e.Handled = true;
        };
        header.Drop += (_, e) =>
        {
            e.Handled = true;
            e.Effects = DropEffectFor(e.Data);
            string[] files = GetDroppedPaths(e.Data, out string? _);
            if (files.Length > 0)
                DropOnTab(index, files);
        };
        return header;
    }

    private void SwitchTab(int index)
    {
        if (index == Config.ActiveTab)
            return;

        Config.ActiveTab = index;
        ReloadItems();
        App.Current.RequestSave();
    }

    /// <summary>把項目丟到某個分頁（不切換過去）。</summary>
    private void DropOnTab(int index, string[] files)
    {
        foreach (string file in files)
        {
            foreach (List<string> items in Config.AllItemLists())
                items.RemoveAll(p => App.PathEquals(p, file));
            App.Current.RemoveFromOtherFences(file, this);
            Config.Tabs[index].Items.Add(file);
        }

        ReloadItems();
        App.Current.RequestSave();
    }

    /// <summary>新增分頁並直接進入改名。</summary>
    private void AddTab()
    {
        Config.AddTab($"分頁 {Math.Max(2, Config.Tabs.Count + 1)}");
        _editingTab = Config.ActiveTab;
        ReloadItems();
        App.Current.RequestSave();
    }

    private void BeginTabRename(int index)
    {
        _editingTab = index;
        RebuildTabs();
    }

    /// <summary>結束分頁改名；name 是 null 代表取消。</summary>
    private void EndTabRename(int index, string? name)
    {
        if (_editingTab != index)
            return;

        _editingTab = null;
        if (!string.IsNullOrWhiteSpace(name) && index < Config.Tabs.Count)
        {
            Config.Tabs[index].Name = name.Trim();
            App.Current.RequestSave();
        }

        // 在 LostFocus / KeyDown 事件裡直接拆掉 TextBox 會出錯，排到之後再重建
        Dispatcher.InvokeAsync(RebuildTabs);
    }

    private void DeleteTab(int index)
    {
        FenceTab tab = Config.Tabs[index];
        // 設定檔被手動改成只剩一個分頁時，旁邊沒有分頁可以接手
        FenceTab? neighbour = Config.Tabs.Count > 1 ? Config.Tabs[index > 0 ? index - 1 : 1] : null;
        App.Current.PushUndo(tab.Items.Count > 0 && neighbour != null
            ? $"已刪除「{tab.Name}」，{tab.Items.Count} 個項目移到「{neighbour.Name}」"
            : $"已刪除「{tab.Name}」");
        Config.RemoveTab(index);
        ReloadItems();
        App.Current.RequestSave();
    }

    private void SetupPortalWatcher()
    {
        _portalWatcher?.Dispose();
        _portalWatcher = null;
        if (IsStandard || Config.FolderPath is not { } folder || !Directory.Exists(folder))
            return;

        try
        {
            _portalWatcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false };
            FileSystemEventHandler changed = (_, _) => Dispatcher.InvokeAsync(RestartPortalReloadTimer);
            _portalWatcher.Created += changed;
            _portalWatcher.Deleted += changed;
            _portalWatcher.Renamed += (_, _) => Dispatcher.InvokeAsync(RestartPortalReloadTimer);
            _portalWatcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            _portalWatcher = null;
        }
    }

    private void RestartPortalReloadTimer()
    {
        _portalReloadTimer.Stop();
        _portalReloadTimer.Start();
    }

    // ───────────── 標題列：拖曳、收合 ─────────────

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // 雙擊名稱 = 改名（跟分頁一樣）；雙擊標題列其他地方 = 收合 / 展開
            if (TitleText.IsMouseOver)
                BeginRename();
            else
                ToggleRollUp();
            e.Handled = true;
            return;
        }

        if (App.Current.Config.Locked)
            return;

        // 拖曳中的吸附與對齊線在 WndProc 的 WM_MOVING 處理
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            SnapGuides.Hide();
        }
    }

    private void ToggleRollUp()
    {
        Config.RolledUp = !Config.RolledUp;
        _hoverExpanded = false;
        Height = Config.RolledUp ? RolledUpHeight : Config.Height;
        ApplyChrome();
        ApplyAppearance();
        App.Current.RequestSave();
    }

    // 收合的分區要滑鼠停一下才展開，滑過去不會一直彈開
    private readonly DispatcherTimer _hoverExpandTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateTitleButtons();
        UpdateFade();
        if (Config.RolledUp && !_hoverExpanded)
        {
            _hoverExpandTimer.Stop();
            _hoverExpandTimer.Start();
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverExpandTimer.Stop();
        UpdateTitleButtons();
        UpdateFade();
        if (!_hoverExpanded || ItemsList.ContextMenu?.IsOpen == true || RootBorder.ContextMenu?.IsOpen == true)
            return;

        _hoverExpanded = false;
        Height = RolledUpHeight;
    }

    /// <summary>收合中的分區，滑鼠移上去（或拖檔案進來）時暫時展開。</summary>
    private void ExpandTemporarily()
    {
        if (!Config.RolledUp || _hoverExpanded)
            return;

        _hoverExpanded = true;
        Height = Config.Height;
    }

    // ───────────── 項目：開啟、拖曳 ─────────────

    private static FenceItem? ItemFromSource(object source)
    {
        var element = source as DependencyObject;
        while (element != null && element is not ListBoxItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return (element as ListBoxItem)?.DataContext as FenceItem;
    }

    private static void OpenItem(FenceItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟「{item.DisplayName}」：\n{ex.Message}", "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void ShowInExplorer(FenceItem item)
    {
        if (item.Exists)
            Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        else if (Path.GetDirectoryName(item.FullPath) is { } dir && Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
    }

    private void OnItemsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource) is { } item)
            OpenItem(item);
    }

    private List<FenceItem> SelectedItems() => _items.Where(i => ItemsList.SelectedItems.Contains(i)).ToList();

    /// <summary>鍵盤：Ctrl+Z 復原、Ctrl+A 全選、Delete 移出、Enter 開啟。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (TitleEditor.IsKeyboardFocusWithin)
            return;

        if (e.OriginalSource is TextBox)
            return; // 正在改分頁名稱

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.F2)
        {
            BeginRename();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Z)
        {
            App.Current.Undo();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A)
        {
            ItemsList.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && IsStandard && SelectedItems() is { Count: > 0 } selected)
        {
            RemoveItems(selected.Select(i => i.FullPath).ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SelectedItems() is { Count: > 0 } toOpen)
        {
            foreach (FenceItem item in toOpen.Take(10))
                OpenItem(item);
            e.Handled = true;
        }
    }

    // ───────────── 選取：點選、Ctrl / Shift 多選、框選 ─────────────

    private bool _boxSelecting;
    private Point _boxStart;
    private HashSet<FenceItem> _selectionBeforeBox = new();
    private FenceItem? _selectOnMouseUp;

    private void OnItemsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ItemsList);
        _pressedItem = ItemFromSource(e.OriginalSource);
        _selectOnMouseUp = null;

        if (_pressedItem == null)
        {
            if (IsInScrollBar(e.OriginalSource))
                return;

            // 在空白處按下 = 開始框選
            _boxSelecting = true;
            _boxStart = _dragStart;
            bool additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!additive)
                ItemsList.UnselectAll();
            _selectionBeforeBox = SelectedItems().ToHashSet();
            ItemsList.Focus();
            ItemsList.CaptureMouse();
            e.Handled = true;
            return;
        }

        // 點在已選取的項目上：先不要改選取，才能把整組拖走；沒拖的話放開時再只選這一個
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && ItemsList.SelectedItems.Contains(_pressedItem))
        {
            _selectOnMouseUp = _pressedItem;
            ItemsList.Focus();
            e.Handled = true;
        }
    }

    private void OnItemsPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_boxSelecting)
        {
            _boxSelecting = false;
            ItemsList.ReleaseMouseCapture();
            SelectionBox.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }

        if (_selectOnMouseUp is { } item)
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = item;
            _selectOnMouseUp = null;
        }

        _pressedItem = null;
    }

    private void OnItemsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右鍵點在已選取的項目上就保留多選，否則只選這一個
        if (ItemFromSource(e.OriginalSource) is { } item && !ItemsList.SelectedItems.Contains(item))
        {
            ItemsList.SelectedItems.Clear();
            ItemsList.SelectedItem = item;
        }
    }

    private void UpdateBoxSelection(Point current)
    {
        var box = new Rect(_boxStart, current);
        Canvas.SetLeft(SelectionBox, box.X);
        Canvas.SetTop(SelectionBox, box.Y);
        SelectionBox.Width = box.Width;
        SelectionBox.Height = box.Height;
        SelectionBox.Visibility = Visibility.Visible;

        foreach (FenceItem item in _items)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible)
                continue;

            Rect bounds = container.TransformToAncestor(ItemsList).TransformBounds(new Rect(container.RenderSize));
            bool select = bounds.IntersectsWith(box) || _selectionBeforeBox.Contains(item);
            if (select != container.IsSelected)
                container.IsSelected = select;
        }
    }

    private static bool IsInScrollBar(object source)
    {
        var element = source as DependencyObject;
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ScrollBar)
                return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    // ───────────── 拖出項目 ─────────────

    private void OnItemsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        Point position = e.GetPosition(ItemsList);
        if (_boxSelecting)
        {
            UpdateBoxSelection(position);
            return;
        }

        if (_pressedItem == null)
            return;

        Vector delta = position - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // 按著的項目在選取範圍內就拖整組，否則只拖它
        List<FenceItem> dragged = ItemsList.SelectedItems.Contains(_pressedItem) ? SelectedItems() : new List<FenceItem> { _pressedItem };
        _pressedItem = null;
        _selectOnMouseUp = null;
        List<string> paths = dragged.Select(i => i.FullPath).ToList();

        if (!IsStandard)
        {
            // 資料夾分區裡是真的檔案：可以拖到檔案總管複製 / 建立捷徑（不允許 Move，避免誤搬）
            DragDrop.DoDragDrop(ItemsList, new DataObject(DataFormats.FileDrop, paths.ToArray()),
                DragDropEffects.Copy | DragDropEffects.Link);
            return;
        }

        // 一般分區只放自己的格式（不放 FileDrop）：拖到桌面或檔案總管時，Explorer 不會複製出一份檔案
        var data = new DataObject(InternalDragFormat, Config.Id + "\n" + string.Join("\n", paths));
        string hint = paths.Count == 1 ? "放開就移出分區" : $"放開就把 {paths.Count} 個項目移出分區";
        GiveFeedbackEventHandler feedback = (_, fe) =>
        {
            // 拖到桌面上會「拿出分區」：不要顯示禁止符號，改顯示提示
            if (fe.Effects == DragDropEffects.None && DesktopHelper.IsCursorOverDesktop())
            {
                fe.UseDefaultCursors = false;
                Mouse.SetCursor(Cursors.Arrow);
                CursorHint.ShowNearCursor(hint);
                fe.Handled = true;
            }
            else
            {
                CursorHint.HideHint();
            }
        };
        ItemsList.GiveFeedback += feedback;
        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Move);
        }
        finally
        {
            ItemsList.GiveFeedback -= feedback;
            CursorHint.HideHint();
        }

        // 沒有被任何分區接住、而且放在桌面上 = 拿出分區（桌面圖示會重新出現）
        if (result == DragDropEffects.None && DesktopHelper.IsCursorOverDesktop())
            RemoveItems(paths);
    }

    /// <summary>拖進來的路徑：從其他分區拖來的（自己的格式）或從桌面 / 檔案總管拖來的（FileDrop）。</summary>
    private static string[] GetDroppedPaths(IDataObject data, out string? sourceFenceId)
    {
        sourceFenceId = null;
        if (data.GetDataPresent(InternalDragFormat) && data.GetData(InternalDragFormat) is string text &&
            text.Split('\n') is [var id, .. var paths] && paths.Length > 0)
        {
            sourceFenceId = id;
            return paths;
        }

        return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : Array.Empty<string>();
    }

    private static DragDropEffects DropEffectFor(IDataObject data) =>
        data.GetDataPresent(InternalDragFormat) ? DragDropEffects.Move
        // 從桌面 / 檔案總管拖來時回報 Link 而非 Move，檔案總管就不會刪掉原始檔
        : data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link
        : DragDropEffects.None;

    // ───────────── 拖放進分區 ─────────────

    private Brush? _normalBorderBrush;
    private bool _dropHighlighted;

    private void SetDropHighlight(bool on)
    {
        if (on == _dropHighlighted)
            return;

        _dropHighlighted = on;
        if (on)
        {
            _normalBorderBrush ??= RootBorder.BorderBrush;
            RootBorder.BorderBrush = (Brush)Resources["AccentBrush"];
            Color accent = ThemeHelper.AccentColor;
            RootBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9, Color = accent };
        }
        else
        {
            if (_normalBorderBrush != null)
                RootBorder.BorderBrush = _normalBorderBrush;
            _normalBorderBrush = null;
            RootBorder.Effect = null;
            InsertionLine.Visibility = Visibility.Collapsed;
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        ExpandTemporarily();
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsStandard ? DropEffectFor(e.Data) : DragDropEffects.None;
        e.Handled = true;
        bool accept = e.Effects != DragDropEffects.None;
        SetDropHighlight(accept);
        if (accept)
            ShowInsertionLine(GetDropIndex(e));
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave 在子元素之間移動時也會觸發，真的離開視窗才取消提示
        Point p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X >= ActualWidth || p.Y >= ActualHeight)
            SetDropHighlight(false);
    }

    /// <summary>在插入位置畫一條直線（放在第 index 個項目前面）。</summary>
    private void ShowInsertionLine(int index)
    {
        if (_items.Count == 0)
        {
            InsertionLine.Visibility = Visibility.Collapsed;
            return;
        }

        bool after = index >= _items.Count;
        FenceItem anchor = _items[Math.Min(index, _items.Count - 1)];
        if (ItemsList.ItemContainerGenerator.ContainerFromItem(anchor) is not ListBoxItem container || !container.IsVisible)
        {
            InsertionLine.Visibility = Visibility.Collapsed;
            return;
        }

        Rect bounds = container.TransformToAncestor(ItemsList).TransformBounds(new Rect(container.RenderSize));
        Canvas.SetLeft(InsertionLine, (after ? bounds.Right + 1 : bounds.Left - 3));
        Canvas.SetTop(InsertionLine, bounds.Top + 4);
        InsertionLine.Height = Math.Max(0, bounds.Height - 8);
        InsertionLine.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int index = GetDropIndex(e);
        SetDropHighlight(false);
        string[] files = GetDroppedPaths(e.Data, out string? sourceFenceId);
        if (!IsStandard || files.Length == 0)
            return;

        e.Effects = DropEffectFor(e.Data);
        bool fromThisFence = sourceFenceId == Config.Id;
        List<string> items = Config.CurrentItems;

        foreach (string file in files)
        {
            int existing = items.FindIndex(p => App.PathEquals(p, file));
            if (existing >= 0)
            {
                if (!fromThisFence)
                    continue;

                // 同一個分區內拖曳 = 重新排序
                items.RemoveAt(existing);
                if (existing < index)
                    index--;
            }

            // 同一個分區的其他分頁裡如果有，也拿掉（一個項目只會出現一次）
            foreach (List<string> other in Config.AllItemLists())
            {
                if (other != items)
                    other.RemoveAll(p => App.PathEquals(p, file));
            }

            items.Insert(Math.Clamp(index, 0, items.Count), file);
            index++;
            App.Current.RemoveFromOtherFences(file, this);
        }

        ReloadItems();
        // 剛放進來的項目保持選取，方便接著再拖去別的地方
        foreach (FenceItem item in _items.Where(i => files.Any(f => App.PathEquals(f, i.FullPath))))
            ItemsList.SelectedItems.Add(item);
        App.Current.RequestSave();
    }

    /// <summary>放下的位置：滑鼠在項目左半邊 = 插在它前面，右半邊 = 插在它後面。</summary>
    private int GetDropIndex(DragEventArgs e)
    {
        Point position = e.GetPosition(ItemsList);
        if (ItemsList.InputHitTest(position) is DependencyObject hit && ItemFromSource(hit) is { } target)
        {
            int index = Config.CurrentItems.FindIndex(p => App.PathEquals(p, target.FullPath));
            if (index >= 0 && ItemsList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container)
            {
                Rect bounds = container.TransformToAncestor(ItemsList).TransformBounds(new Rect(container.RenderSize));
                return position.X > bounds.Left + bounds.Width / 2 ? index + 1 : index;
            }

            if (index >= 0)
                return index;
        }

        return Config.CurrentItems.Count;
    }

    // ───────────── 右鍵選單 ─────────────

    private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        FenceItem? item = ItemFromSource(e.OriginalSource);
        ContextMenu menu = item != null ? BuildItemMenu(item) : BuildFenceMenu();
        menu.PlacementTarget = RootBorder;
        menu.Closed += (_, _) =>
        {
            if (!IsMouseOver)
                OnMouseLeave(this, null!);
            else
                UpdateFade();
        };
        RootBorder.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildItemMenu(FenceItem item)
    {
        var menu = new ContextMenu();
        List<FenceItem> selected = SelectedItems();
        if (!selected.Contains(item))
            selected = new List<FenceItem> { item };

        menu.Items.Add(MakeItem("開啟", () => OpenItem(item), bold: true));
        menu.Items.Add(MakeItem("開啟檔案位置", () => ShowInExplorer(item)));
        if (IsStandard)
        {
            menu.Items.Add(new Separator());
            string label = selected.Count == 1 ? "移出分區（放回桌面）" : $"把 {selected.Count} 個項目移出分區";
            menu.Items.Add(MakeItem(label, () => RemoveItems(selected.Select(i => i.FullPath).ToList())));
        }

        return menu;
    }

    /// <summary>
    /// 「⋯」與右鍵選單。改名（雙擊名稱）、新增分頁、外觀、收合都有更直接的做法，這裡只放其他地方沒有的動作。
    /// </summary>
    private ContextMenu BuildFenceMenu()
    {
        var menu = new ContextMenu();
        if (IsStandard)
        {
            menu.Items.Add(MakeItem("自動整理規則…", EditRules));
            menu.Items.Add(MakeItem("依名稱排序", SortByName));
            menu.Items.Add(MakeItem("清除失效的項目", RemoveMissingItems));
        }
        else
        {
            menu.Items.Add(MakeItem("在檔案總管開啟", () => Process.Start("explorer.exe", $"\"{Config.FolderPath}\"")));
            menu.Items.Add(MakeItem("換一個資料夾…", ChangeFolder));
        }

        int pageCount = App.Current.Config.PageCount;
        var pages = Enumerable.Range(0, pageCount)
            .Select(p => (object)MakeItem($"第 {p + 1} 頁", () => App.Current.MoveFenceToPage(this, p), isChecked: Config.Page == p))
            .Append(new Separator())
            .Append(MakeItem("新的一頁", () => App.Current.MoveFenceToPage(this, pageCount)))
            .ToList();
        menu.Items.Add(MakeItem("移到桌面頁面", null, pages));

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("刪除這個分區", Delete));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("MyFences 設定…", () => SettingsWindow.ShowFor(App.Current)));
        return menu;
    }

    private static MenuItem MakeItem(string header, Action? onClick, IList<object>? children = null, bool bold = false, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        if (bold)
            item.FontWeight = FontWeights.Bold;
        if (onClick != null)
            item.Click += (_, _) => onClick();
        if (children != null)
        {
            foreach (object child in children)
            {
                if (child is FrameworkElement { Parent: ItemsControl parent } element)
                    parent.Items.Remove(element);
                item.Items.Add(child);
            }
        }

        return item;
    }

    // ───────────── 動作 ─────────────

    /// <summary>移出分區（檔案不會刪除，桌面上的圖示會重新出現），可以復原。</summary>
    private void RemoveItems(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;

        App.Current.PushUndo(paths.Count == 1
            ? $"「{FenceItem.Create(paths.First()).DisplayName}」已放回桌面"
            : $"{paths.Count} 個項目已放回桌面");
        foreach (List<string> items in Config.AllItemLists())
            items.RemoveAll(p => paths.Any(path => App.PathEquals(p, path)));
        ReloadItems();
        App.Current.RequestSave();
    }

    private void SetColor(string color)
    {
        Config.Color = color;
        ApplyAppearance();
        App.Current.RequestSave();
    }

    /// <summary>用 Windows 內建的調色盤選顏色。</summary>
    private void PickCustomColor()
    {
        Color current = ThemeHelper.Resolve(Config.Color);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return;

        System.Drawing.Color c = dialog.Color;
        SetColor($"#{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    private void ToggleBlur()
    {
        Config.Blur = !Config.Blur;
        ApplyAppearance();
        App.Current.RequestSave();
    }

    private void ToggleFade()
    {
        Config.FadeWhenIdle = !Config.FadeWhenIdle;
        UpdateFade();
        App.Current.RequestSave();
    }

    private void SetOpacity(double opacity)
    {
        Config.Opacity = opacity;
        ApplyAppearance();
        App.Current.RequestSave();
    }

    private void SetIconSize(double size)
    {
        Config.IconSize = size;
        ApplyAppearance();
        App.Current.RequestSave();
    }

    private void SortByName()
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        List<string> sorted = Config.CurrentItems
            .OrderBy(p => !Directory.Exists(p))
            .ThenBy(p => FenceItem.Create(p).DisplayName, comparer)
            .ToList();
        Config.CurrentItems.Clear();
        Config.CurrentItems.AddRange(sorted);
        ReloadItems();
        App.Current.RequestSave();
    }

    private void EditRules()
    {
        if (!RuleEditorWindow.Edit(Config))
            return;

        App.Current.RequestSave();
        if (Config.Rules.Count == 0)
            return;

        // 有放入項目時 SortDesktop 會自己顯示附「復原」的提示
        if (App.Current.SortDesktop() == 0)
            Toast.ShowMessage("規則已儲存");
    }

    private void RemoveMissingItems()
    {
        static bool Missing(string p) => !File.Exists(p) && !Directory.Exists(p);
        int count = Config.AllItemLists().Sum(items => items.Count(Missing));
        if (count == 0)
        {
            Toast.ShowMessage("所有項目都還在，沒有需要清除的");
            return;
        }

        App.Current.PushUndo($"已清除 {count} 個找不到檔案的項目");
        foreach (List<string> items in Config.AllItemLists())
            items.RemoveAll(Missing);
        ReloadItems();
        App.Current.RequestSave();
    }

    private void ChangeFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇要顯示在分區裡的資料夾", InitialDirectory = Config.FolderPath ?? "" };
        if (dialog.ShowDialog() != true)
            return;

        Config.FolderPath = dialog.FolderName;
        SetupPortalWatcher();
        ReloadItems();
        ApplyAppearance();
        App.Current.RequestSave();
    }

    /// <summary>不用再確認：刪錯了可以從右下角的提示復原。</summary>
    private void Delete() => App.Current.DeleteFence(this);
}
