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

    private const double RolledUpHeight = 30; // 標題列 28 + 上下框線
    private const double GridSize = 10;
    private const string InternalDragFormat = "MyFences.Item";

    private static readonly (string Name, string Color)[] ColorPresets =
    {
        ("黑", "#1E1E1E"), ("深灰", "#3A3A3A"), ("藍", "#1F4E79"), ("綠", "#2E5E3A"),
        ("紫", "#4B2E6B"), ("紅", "#6B2E2E"), ("棕", "#5C4326"),
    };

    private static readonly double[] OpacityPresets = { 0.0, 0.25, 0.4, 0.55, 0.7, 0.85, 1.0 };
    private static readonly (string Name, int Value)[] RulePriorityPresets = { ("高（先比對）", 1), ("一般", 0), ("低（最後比對）", -1) };
    private static readonly (string Name, double Size)[] IconSizePresets = { ("小", 32), ("中", 48), ("大", 64) };

    private readonly ObservableCollection<FenceItem> _items = new();
    private readonly DispatcherTimer _portalReloadTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private FileSystemWatcher? _portalWatcher;
    private IntPtr _hwnd;
    private bool _loaded;
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
        Loaded += (_, _) => _loaded = true;
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
    }

    public void ApplyAppearance()
    {
        Color baseColor;
        try
        {
            baseColor = (Color)ColorConverter.ConvertFromString(Config.Color);
        }
        catch (FormatException)
        {
            baseColor = Colors.Black;
        }

        // alpha 至少 1：完全透明的像素會讓滑鼠點擊穿透到桌面
        byte bodyAlpha = (byte)Math.Clamp(Config.Opacity * 255, 1, 255);
        byte titleAlpha = (byte)Math.Clamp(Config.Opacity * 255 + 50, 70, 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(bodyAlpha, baseColor.R, baseColor.G, baseColor.B));
        TitleBar.Background = new SolidColorBrush(Color.FromArgb(titleAlpha, baseColor.R, baseColor.G, baseColor.B));

        TitleText.Text = Config.Name;
        IconSize = Config.IconSize;
        CellWidth = Config.IconSize + 36;
        EmptyHint.Text = IsStandard ? "把檔案或捷徑拖到這裡" : "資料夾是空的";
    }

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
            paths = Config.Items.ToList();
        else if (Config.FolderPath is { } folder && Directory.Exists(folder))
            paths = DesktopHelper.EnumerateVisibleEntries(folder);
        else
            paths = new List<string>();

        foreach (string path in paths)
            _items.Add(FenceItem.Create(path));

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

    private void SaveBounds()
    {
        if (!_loaded || WindowState != WindowState.Normal)
            return;

        Config.Left = Left;
        Config.Top = Top;
        Config.Width = Width;
        if (!Config.RolledUp && !_hoverExpanded)
            Config.Height = Height;
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
            ToggleRollUp();
            e.Handled = true;
            return;
        }

        if (App.Current.Config.Locked)
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Left = Math.Round(Left / GridSize) * GridSize;
        Top = Math.Round(Top / GridSize) * GridSize;
    }

    private void ToggleRollUp()
    {
        Config.RolledUp = !Config.RolledUp;
        _hoverExpanded = false;
        Height = Config.RolledUp ? RolledUpHeight : Config.Height;
        ApplyChrome();
        App.Current.RequestSave();
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => ExpandTemporarily();

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
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

    private void OnItemsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ItemsList.SelectedItem is not FenceItem item)
            return;

        if (e.Key == Key.Enter)
        {
            OpenItem(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && IsStandard)
        {
            RemoveItem(item);
            e.Handled = true;
        }
    }

    private void OnItemsPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ItemsList);
        _pressedItem = ItemFromSource(e.OriginalSource);
    }

    private void OnItemsPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource) is { } item)
            ItemsList.SelectedItem = item;
    }

    private void OnItemsPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Vector delta = e.GetPosition(ItemsList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        FenceItem item = _pressedItem;
        _pressedItem = null;

        var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
        data.SetData(InternalDragFormat, Config.Id);
        // 不允許 Move：拖到檔案總管時只會複製或建立捷徑，不會誤搬原始檔
        DragDrop.DoDragDrop(ItemsList, data, DragDropEffects.Copy | DragDropEffects.Link);
    }

    // ───────────── 拖放進分區 ─────────────

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        ExpandTemporarily();
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 回報 Link 而非 Move，檔案總管就不會刪掉原始檔
        e.Effects = IsStandard && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!IsStandard || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        int index = GetDropIndex(e);
        bool fromThisFence = e.Data.GetDataPresent(InternalDragFormat) &&
                             e.Data.GetData(InternalDragFormat) as string == Config.Id;

        foreach (string file in files)
        {
            int existing = Config.Items.FindIndex(p => App.PathEquals(p, file));
            if (existing >= 0)
            {
                if (!fromThisFence)
                    continue;

                // 同一個分區內拖曳 = 重新排序
                Config.Items.RemoveAt(existing);
                if (existing < index)
                    index--;
            }

            Config.Items.Insert(Math.Clamp(index, 0, Config.Items.Count), file);
            index++;
            App.Current.RemoveFromOtherFences(file, this);
        }

        ReloadItems();
        App.Current.RequestSave();
    }

    private int GetDropIndex(DragEventArgs e)
    {
        if (ItemsList.InputHitTest(e.GetPosition(ItemsList)) is DependencyObject hit &&
            ItemFromSource(hit) is { } target)
        {
            int index = Config.Items.FindIndex(p => App.PathEquals(p, target.FullPath));
            if (index >= 0)
                return index;
        }

        return Config.Items.Count;
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
        };
        RootBorder.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildItemMenu(FenceItem item)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("開啟", () => OpenItem(item), bold: true));
        menu.Items.Add(MakeItem("開啟檔案位置", () => ShowInExplorer(item)));
        if (IsStandard)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("從分區移除（不會刪除檔案）", () => RemoveItem(item)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("分區設定", null, BuildFenceMenu().Items.Cast<object>().ToList()));
        return menu;
    }

    private ContextMenu BuildFenceMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("重新命名…", Rename));

        var colors = ColorPresets
            .Select(c => (object)MakeItem(c.Name, () => SetColor(c.Color), isChecked: string.Equals(Config.Color, c.Color, StringComparison.OrdinalIgnoreCase)))
            .Append(new Separator())
            .Append(MakeItem("自訂…", CustomColor))
            .ToList();
        menu.Items.Add(MakeItem("顏色", null, colors));

        var opacities = OpacityPresets
            .Select(o => (object)MakeItem($"{o:P0}", () => SetOpacity(o), isChecked: Math.Abs(Config.Opacity - o) < 0.01))
            .ToList();
        menu.Items.Add(MakeItem("背景不透明度", null, opacities));

        var sizes = IconSizePresets
            .Select(s => (object)MakeItem(s.Name, () => SetIconSize(s.Size), isChecked: Math.Abs(Config.IconSize - s.Size) < 0.5))
            .ToList();
        menu.Items.Add(MakeItem("圖示大小", null, sizes));

        menu.Items.Add(MakeItem(Config.RolledUp ? "展開" : "收合（雙擊標題列）", ToggleRollUp));
        menu.Items.Add(new Separator());

        if (IsStandard)
        {
            menu.Items.Add(MakeItem("依名稱排序", SortByName));
            menu.Items.Add(MakeItem("自動整理規則…", EditRules));
            var priorities = RulePriorityPresets
                .Select(p => (object)MakeItem(p.Name, () => SetRulePriority(p.Value), isChecked: Config.RulePriority == p.Value))
                .ToList();
            menu.Items.Add(MakeItem("規則優先順序", null, priorities));
            menu.Items.Add(MakeItem("清除已不存在的項目", RemoveMissingItems));
        }
        else
        {
            menu.Items.Add(MakeItem("開啟資料夾", () => Process.Start("explorer.exe", $"\"{Config.FolderPath}\"")));
            menu.Items.Add(MakeItem("變更資料夾…", ChangeFolder));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("新增分區", () => App.Current.CreateFence(FenceKind.Standard)));
        menu.Items.Add(MakeItem("刪除此分區", Delete));
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

    private void RemoveItem(FenceItem item)
    {
        Config.Items.RemoveAll(p => App.PathEquals(p, item.FullPath));
        ReloadItems();
        App.Current.RequestSave();
    }

    private void Rename()
    {
        string? name = InputDialog.Ask("重新命名分區", "分區名稱：", Config.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        Config.Name = name.Trim();
        ApplyAppearance();
        App.Current.RequestSave();
    }

    private void SetColor(string color)
    {
        Config.Color = color;
        ApplyAppearance();
        App.Current.RequestSave();
    }

    private void CustomColor()
    {
        string? color = InputDialog.Ask("自訂顏色", "輸入色碼（例如 #335577）：", Config.Color);
        if (string.IsNullOrWhiteSpace(color))
            return;

        try
        {
            ColorConverter.ConvertFromString(color.Trim());
        }
        catch (FormatException)
        {
            MessageBox.Show("色碼格式不正確。", "MyFences");
            return;
        }

        SetColor(color.Trim());
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
        Config.Items = Config.Items
            .OrderBy(p => !Directory.Exists(p))
            .ThenBy(p => FenceItem.Create(p).DisplayName, comparer)
            .ToList();
        ReloadItems();
        App.Current.RequestSave();
    }

    private void EditRules()
    {
        string current = string.Join(", ", Config.Rules);
        while (true)
        {
            string? text = InputDialog.Ask(
                "自動整理規則",
                "桌面上符合規則的項目會自動放進這個分區。多條規則用逗號分隔，留白 = 不自動整理。\n\n" +
                "  pdf 或 .pdf　副檔名\n" +
                "  folder　　　資料夾\n" +
                "  *　　　　　所有檔案\n" +
                "  name:報表　 檔名包含「報表」（可用 * 萬用字元）\n" +
                "  newer:7　　 7 天內修改過\n" +
                "  older:30　　超過 30 天沒動\n" +
                "  larger:100　大於 100 MB；smaller:1　小於 1 MB\n\n" +
                "用 + 表示「而且」，例如：pdf+name:發票, xlsx+newer:7",
                current);
            if (text == null)
                return;

            List<string> rules = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            List<string> invalid = FenceRules.FindInvalidConditions(rules);
            if (invalid.Count > 0)
            {
                MessageBox.Show("看不懂這些規則：\n" + string.Join("\n", invalid), "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                current = text;
                continue;
            }

            Config.Rules = rules;
            break;
        }

        App.Current.RequestSave();
        if (Config.Rules.Count == 0)
            return;

        int count = App.Current.SortDesktop();
        if (count > 0)
            MessageBox.Show($"已依新規則放入 {count} 個項目。", "MyFences");
    }

    private void SetRulePriority(int priority)
    {
        Config.RulePriority = priority;
        App.Current.RequestSave();
    }

    private void RemoveMissingItems()
    {
        int removed = Config.Items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p));
        ReloadItems();
        App.Current.RequestSave();
        MessageBox.Show($"已清除 {removed} 個項目。", "MyFences");
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

    private void Delete()
    {
        var result = MessageBox.Show(
            $"確定要刪除分區「{Config.Name}」？\n（只會移除分區，裡面的檔案不受影響）",
            "MyFences", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            App.Current.DeleteFence(this);
    }
}
