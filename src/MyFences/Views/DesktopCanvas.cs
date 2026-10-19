using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyFences.Models;
using MyFences.Services;
using static MyFences.Services.NativeMethods;

namespace MyFences.Views;

/// <summary>
/// 一個螢幕的桌面圖示層（分區外的圖示由 MyFences 自己畫）。
/// 空白處完全透明，滑鼠會直接穿透到 Windows 原本的桌面：右鍵是 Windows 的桌面選單，「新增」「貼上」「檢視」都是原本的。
/// 圖示本身可以點：選取、拖曳、右鍵選單（裡面的動作都交給 Windows 做）。
/// </summary>
internal sealed class DesktopCanvas : Window
{
    public const string InternalDragFormat = "MyFences.Item";
    public const string DesktopDragSource = "desktop";

    // alpha 1：看不出來，但滑鼠點得到（完全透明的地方會穿透到 Windows 桌面）
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));

    private readonly Canvas _canvas = new();
    private readonly Dictionary<string, IconView> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Shapes.Rectangle _box;
    private IntPtr _hwnd;
    private PixelRect _workArea;
    private double _iconSize = 48;

    private IconView? _pressed;
    private Point _pressPoint;
    private IconView? _selectOnMouseUp;
    private TextBox? _renameBox;

    public DesktopCanvas()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 12;
        Title = "MyFences Desktop";

        Color accent = ThemeHelper.AccentColor;
        _box = new System.Windows.Shapes.Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Panel.SetZIndex(_box, 1000);
        _canvas.Children.Add(_box);
        Content = _canvas;

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnKeyDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        SourceInitialized += (_, _) => AttachToDesktop();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => PlaceOn(_workArea));
    }

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// 擁有者視窗：第一層黏在 Progman，之後每一層黏在前一層上面，分區再黏在最後一層上面。
    /// 擁有者一定在下面，所以順序固定是 桌面 → 各螢幕圖示層 → 分區。
    /// </summary>
    public IntPtr OwnerHandle { get; set; }

    public bool IsClosed { get; private set; }
    public PixelRect WorkArea => _workArea;

    public IReadOnlyList<string> SelectedIds => _views.Values.Where(v => v.IsSelected).Select(v => v.Id).ToList();

    public IEnumerable<string> Ids => _views.Keys;

    private double Scale
    {
        get
        {
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return s > 0 ? s : 1;
        }
    }

    public (int Width, int Height) CellPixels => DesktopGrid.CellSize(_iconSize, Scale);

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

        if (OwnerHandle != IntPtr.Zero && IsWindow(OwnerHandle))
            SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, OwnerHandle);
        else
            DesktopHelper.GlueToProgman(_hwnd);
        DesktopHelper.SendToBottom(_hwnd);
    }

    /// <summary>蓋滿這個螢幕的工作區（螢幕像素）。</summary>
    public void PlaceOn(PixelRect workArea)
    {
        _workArea = workArea;
        if (_hwnd == IntPtr.Zero)
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(_hwnd, IntPtr.Zero, workArea.X, workArea.Y, workArea.Width, workArea.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        foreach (IconView view in _views.Values)
            Position(view);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            // 永遠在最下層：Windows 桌面之上、分區和一般視窗之下
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Current.IsExiting && !_closingIntentionally)
            e.Cancel = true; // 擋掉 Alt+F4
        base.OnClosing(e);
    }

    private bool _closingIntentionally;

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        base.OnClosed(e);
    }

    public bool IsClosingIntentionally => _closingIntentionally;

    public void CloseIntentionally()
    {
        _closingIntentionally = true;
        Close();
    }

    // ───────────── 圖示 ─────────────

    /// <summary>換成這組圖示（位置是螢幕像素）。同一個項目沿用原本的圖示，不會閃。</summary>
    public void SetItems(IReadOnlyList<(string Id, PixelPoint Position)> items, double iconSize)
    {
        bool sizeChanged = Math.Abs(iconSize - _iconSize) > 0.1;
        _iconSize = iconSize;
        var keep = new HashSet<string>(items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        foreach (string id in _views.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            if (_renameBox != null && _renameBox.Tag is IconView renaming && renaming.Id == id)
                EndRename(commit: false);
            _canvas.Children.Remove(_views[id]);
            _views.Remove(id);
        }

        foreach ((string id, PixelPoint position) in items)
        {
            if (!_views.TryGetValue(id, out IconView? view) || sizeChanged)
            {
                bool selected = view?.IsSelected == true;
                if (view != null)
                    _canvas.Children.Remove(view);

                view = new IconView(FenceItem.Create(id), iconSize) { IsSelected = selected };
                view.MouseLeftButtonDown += OnIconMouseDown;
                view.MouseRightButtonUp += OnIconRightClick;
                _views[id] = view;
                _canvas.Children.Add(view);
                IconView loaded = view;
                Dispatcher.InvokeAsync(() => loaded.Item.Icon ??= IconHelper.GetIcon(loaded.Id), DispatcherPriority.Background);
            }

            view.Pixel = position;
            Position(view);
        }
    }

    private void Position(IconView view)
    {
        double s = Scale;
        Canvas.SetLeft(view, (view.Pixel.X - _workArea.X) / s);
        Canvas.SetTop(view, (view.Pixel.Y - _workArea.Y) / s);
        view.Width = _iconSize + 36;
    }

    public PixelPoint? PositionOf(string id) => _views.TryGetValue(id, out IconView? v) ? v.Pixel : null;

    public void ClearSelection()
    {
        foreach (IconView view in _views.Values)
            view.IsSelected = false;
    }

    public void Select(IEnumerable<string> ids, bool focus)
    {
        var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        foreach (IconView view in _views.Values)
            view.IsSelected = set.Contains(view.Id);
        if (focus && set.Count > 0)
            TakeFocus();
    }

    /// <summary>從桌面空白處拖出來的框選（螢幕像素；null = 結束）。</summary>
    public void BoxSelect(PixelRect? rect)
    {
        if (rect is not { } r)
        {
            _box.Visibility = Visibility.Collapsed;
            if (_views.Values.Any(v => v.IsSelected))
                TakeFocus();
            return;
        }

        (int cw, int ch) = CellPixels;
        foreach (IconView view in _views.Values)
        {
            var cell = new PixelRect(view.Pixel.X + cw / 6, view.Pixel.Y, cw * 2 / 3, ch);
            view.IsSelected = cell.X < r.Right && r.X < cell.Right && cell.Y < r.Bottom && r.Y < cell.Bottom;
        }

        int left = Math.Max(r.X, _workArea.X), top = Math.Max(r.Y, _workArea.Y);
        int right = Math.Min(r.Right, _workArea.Right), bottom = Math.Min(r.Bottom, _workArea.Bottom);
        if (right <= left || bottom <= top)
        {
            _box.Visibility = Visibility.Collapsed;
            return;
        }

        double s = Scale;
        Canvas.SetLeft(_box, (left - _workArea.X) / s);
        Canvas.SetTop(_box, (top - _workArea.Y) / s);
        _box.Width = (right - left) / s;
        _box.Height = (bottom - top) / s;
        _box.Visibility = Visibility.Visible;
    }

    /// <summary>拖曳中：整片都接得到放下的東西（平常空白處是穿透的）。</summary>
    public void SetDropMode(bool on) => Background = on ? HitBrush : Brushes.Transparent;

    private void TakeFocus()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        Activate();
        SetForegroundWindow(_hwnd);
        Focus();
    }

    private IconView? ViewAt(Point p) =>
        _canvas.InputHitTest(p) is DependencyObject hit ? FindView(hit) : null;

    private static IconView? FindView(DependencyObject? element)
    {
        while (element != null && element is not IconView)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as IconView;
    }

    // ───────────── 滑鼠 ─────────────

    private void OnIconMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IconView view)
            return;

        e.Handled = true;
        App.Current.OnCanvasActivated(this);
        if (_renameBox != null)
            EndRename(commit: true);

        if (e.ClickCount == 2)
        {
            _pressed = null;
            List<string> toOpen = view.IsSelected ? SelectedIds.ToList() : new List<string> { view.Id };
            foreach (string id in toOpen.Take(10))
                FileActions.Open(id);
            return;
        }

        ModifierKeys mods = Keyboard.Modifiers;
        _pressed = view;
        _pressPoint = e.GetPosition(_canvas);
        _selectOnMouseUp = null;
        if ((mods & ModifierKeys.Control) != 0)
            view.IsSelected = !view.IsSelected;
        else if ((mods & ModifierKeys.Shift) != 0)
            view.IsSelected = true;
        else if (view.IsSelected)
            _selectOnMouseUp = view; // 先不要改選取，才能把整組拖走
        else
            Select(new[] { view.Id }, focus: false);

        TakeFocus();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnMouseUp is { } view)
            Select(new[] { view.Id }, focus: false);
        _selectOnMouseUp = null;
        _pressed = null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Vector delta = e.GetPosition(_canvas) - _pressPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        IconView pressed = _pressed;
        _pressed = null;
        _selectOnMouseUp = null;
        if (!pressed.IsSelected)
            Select(new[] { pressed.Id }, focus: false);
        StartDrag(pressed);
    }

    // 拖曳可能從一個螢幕拖到另一個螢幕的圖示層，所以放在 static
    private static PixelPoint _dragOrigin;
    private static HashSet<string> _dragging = new(StringComparer.OrdinalIgnoreCase);

    private void StartDrag(IconView pressed)
    {
        List<string> ids = SelectedIds.ToList();
        GetCursorPos(out POINT cursor);
        _dragOrigin = new PixelPoint(cursor.x, cursor.y);
        _dragging = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

        // 自己的格式（分區、桌面用）+ 檔案清單（拖到檔案總管或其他程式就跟從 Windows 桌面拖出去一樣）
        var data = new DataObject();
        data.SetData(InternalDragFormat, DesktopDragSource + "\n" + string.Join("\n", ids));
        string[] files = FileActions.RealFiles(ids).ToArray();
        if (files.Length > 0)
            data.SetData(DataFormats.FileDrop, files);

        App.Current.SetDesktopDropMode(true);
        try
        {
            DragDrop.DoDragDrop(pressed, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _dragging.Clear();
            App.Current.SetDesktopDropMode(false);
            App.Current.RefreshDesktop(enumerate: true);
        }
    }

    private void OnIconRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not IconView view)
            return;

        e.Handled = true;
        App.Current.OnCanvasActivated(this);
        if (!view.IsSelected)
            Select(new[] { view.Id }, focus: false);
        TakeFocus();

        ContextMenu menu = BuildMenu(view, SelectedIds.ToList());
        menu.PlacementTarget = view;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // ───────────── 放下 ─────────────

    private static string[] DraggedIds(IDataObject data, out string? source)
    {
        source = null;
        if (data.GetDataPresent(InternalDragFormat) && data.GetData(InternalDragFormat) is string text &&
            text.Split('\n') is [var src, .. var ids] && ids.Length > 0)
        {
            source = src;
            return ids;
        }

        return Array.Empty<string>();
    }

    /// <summary>放在資料夾 / 資源回收筒上 = 搬進去 / 刪除（跟 Windows 桌面一樣）。</summary>
    private IconView? DropTargetAt(DragEventArgs e)
    {
        IconView? target = ViewAt(e.GetPosition(_canvas));
        if (target == null || _dragging.Contains(target.Id))
            return null;
        return target.Id == ShellItems.RecycleBin || Directory.Exists(target.Id) ? target : null;
    }

    /// <summary>從檔案總管拖來的檔案：跟 Windows 一樣，同一顆磁碟預設搬移、不同磁碟預設複製；按 Ctrl 複製、Shift 搬移。</summary>
    private static DragDropEffects ExternalEffect(DragEventArgs e, string[] files, string targetDir)
    {
        if (files.Length == 0 || files.All(f => string.Equals(Path.GetDirectoryName(f), targetDir, StringComparison.OrdinalIgnoreCase)))
            return DragDropEffects.None;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
            return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
            return DragDropEffects.Move;
        bool sameDrive = string.Equals(Path.GetPathRoot(files[0]), Path.GetPathRoot(targetDir), StringComparison.OrdinalIgnoreCase);
        return sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private static string[] ExternalFiles(IDataObject data) =>
        !data.GetDataPresent(InternalDragFormat) && data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files
            : Array.Empty<string>();

    private static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        IconView? target;
        if (ExternalFiles(e.Data) is { Length: > 0 } files)
        {
            // 從檔案總管拖到 MyFences 的圖示上
            target = DropTargetAt(e);
            e.Effects = target?.Id == ShellItems.RecycleBin ? DragDropEffects.Move : ExternalEffect(e, files, target?.Id ?? UserDesktop);
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(InternalDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
            target = e.Effects == DragDropEffects.None ? null : DropTargetAt(e);
        }

        foreach (IconView view in _views.Values)
            view.IsDropTarget = view == target;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        foreach (IconView view in _views.Values)
            view.IsDropTarget = false;

        GetCursorPos(out POINT cursor);
        var point = new PixelPoint(cursor.x, cursor.y);

        if (ExternalFiles(e.Data) is { Length: > 0 } files)
        {
            IconView? folderTarget = DropTargetAt(e);
            string dir = folderTarget?.Id ?? UserDesktop;
            DragDropEffects effect = folderTarget?.Id == ShellItems.RecycleBin ? DragDropEffects.Move : ExternalEffect(e, files, dir);
            // 回報 None：檔案操作由我們自己做，來源（檔案總管）不用再刪一次
            e.Effects = DragDropEffects.None;
            if (effect == DragDropEffects.None)
                return;

            Dispatcher.InvokeAsync(() =>
            {
                if (folderTarget?.Id == ShellItems.RecycleBin)
                    App.Current.DeleteItems(files, permanently: false);
                else
                    App.Current.TransferInto(files, dir, move: effect == DragDropEffects.Move, near: folderTarget == null ? point : null);
            });
            return;
        }

        string[] ids = DraggedIds(e.Data, out string? source);
        if (ids.Length == 0)
            return;

        e.Effects = DragDropEffects.Move;

        if (DropTargetAt(e) is { } target)
        {
            // 先讓拖曳結束（檔案操作會跳出 Windows 的進度 / 確認視窗）
            Dispatcher.InvokeAsync(() =>
            {
                if (target.Id == ShellItems.RecycleBin)
                    App.Current.DeleteItems(ids, permanently: false);
                else
                    App.Current.MoveItemsInto(ids, target.Id);
            });
            return;
        }

        App.Current.PlaceOnDesktop(ids, point, source == DesktopDragSource ? _dragOrigin : null);
    }

    // ───────────── 鍵盤 ─────────────

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        List<string> selected = SelectedIds.ToList();
        e.Handled = true;

        if (key == Key.Enter && alt && selected.Count > 0)
            FileActions.ShowProperties(selected[0]);
        else if (key == Key.Enter && selected.Count > 0)
            selected.Take(10).ToList().ForEach(FileActions.Open);
        else if (key == Key.F2 && selected.Count == 1)
            BeginRename(selected[0]);
        else if (key == Key.Delete && selected.Count > 0)
            App.Current.DeleteItems(selected, permanently: shift);
        else if (ctrl && key == Key.A)
            Select(_views.Keys, focus: false);
        else if (ctrl && key == Key.C)
            FileActions.CopyToClipboard(selected, cut: false);
        else if (ctrl && key == Key.X)
            FileActions.CopyToClipboard(selected, cut: true);
        else if (ctrl && key == Key.V)
            App.Current.PasteOnDesktop(PasteSpot());
        else if (ctrl && shift && key == Key.N)
            App.Current.NewFolderOnDesktop(PasteSpot());
        else if (ctrl && key == Key.Z)
            App.Current.Undo();
        else if (key == Key.F5)
            App.Current.RefreshDesktop(enumerate: true);
        else if (key == Key.Escape)
            ClearSelection();
        else
            e.Handled = false;
    }

    /// <summary>貼上的位置：選取的圖示旁邊，沒有選取就放在滑鼠附近。</summary>
    private PixelPoint PasteSpot()
    {
        if (_views.Values.FirstOrDefault(v => v.IsSelected) is { } view)
            return view.Pixel;
        GetCursorPos(out POINT cursor);
        return new PixelPoint(cursor.x, cursor.y);
    }

    // ───────────── 改名 ─────────────

    public void BeginRename(string id)
    {
        if (!_views.TryGetValue(id, out IconView? view) || ShellItems.IsSystemItem(id) || !ShellItems.Exists(id))
            return;

        EndRename(commit: false);
        Select(new[] { id }, focus: true);
        string name = view.Item.DisplayName;
        var box = new TextBox
        {
            Text = name,
            Tag = view,
            Width = view.Width + 20,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 80,
            Padding = new Thickness(1),
        };
        Canvas.SetLeft(box, Canvas.GetLeft(view) - 10);
        Canvas.SetTop(box, Canvas.GetTop(view) + _iconSize + 8);
        Panel.SetZIndex(box, 2000);
        _canvas.Children.Add(box);
        _renameBox = box;

        // 跟檔案總管一樣：先選取主檔名，不含副檔名
        string ext = Path.GetExtension(name);
        int length = !Directory.Exists(id) && ext.Length > 0 && ext.Length < name.Length ? name.Length - ext.Length : name.Length;
        box.Focus();
        box.Select(0, length);

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                EndRename(commit: true);
                ke.Handled = true;
            }
            else if (ke.Key == Key.Escape)
            {
                EndRename(commit: false);
                ke.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => EndRename(commit: true);
    }

    private void EndRename(bool commit)
    {
        if (_renameBox is not { Tag: IconView view } box)
            return;

        _renameBox = null;
        _canvas.Children.Remove(box);
        if (commit && box.Text.Trim() != view.Item.DisplayName)
            App.Current.RenameItem(view.Id, box.Text);
    }

    // ───────────── 右鍵選單 ─────────────

    private ContextMenu BuildMenu(IconView view, List<string> selected)
    {
        var menu = new ContextMenu();
        string id = view.Id;
        bool system = ShellItems.IsSystemItem(id);
        bool hasFiles = FileActions.RealFiles(selected).Count > 0;

        menu.Items.Add(MenuItem("開啟", () => selected.Take(10).ToList().ForEach(FileActions.Open), bold: true));
        if (selected.Count == 1 && FileActions.CanOpenWith(id))
            menu.Items.Add(MenuItem("開啟方式…", () => FileActions.OpenWith(id)));
        if (!system)
            menu.Items.Add(MenuItem("在檔案總管中顯示", () => FileActions.ShowInExplorer(id)));
        if (id == ShellItems.RecycleBin)
            menu.Items.Add(MenuItem("清理資源回收筒", FileActions.EmptyRecycleBin));

        if (hasFiles)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("剪下", () => FileActions.CopyToClipboard(selected, cut: true), "Ctrl+X"));
            menu.Items.Add(MenuItem("複製", () => FileActions.CopyToClipboard(selected, cut: false), "Ctrl+C"));
        }

        if (Directory.Exists(id) && FileActions.ClipboardHasFiles())
            menu.Items.Add(MenuItem("貼上到這個資料夾", () => App.Current.PasteInto(id)));

        var fences = App.Current.FenceTargets();
        if (fences.Count > 0)
        {
            menu.Items.Add(new Separator());
            var sub = MenuItem("放進分區", null);
            foreach ((string fenceId, string name) in fences)
                sub.Items.Add(MenuItem(name, () => App.Current.AddToFence(selected, fenceId)));
            menu.Items.Add(sub);
        }

        if (hasFiles || (selected.Count == 1 && !system))
            menu.Items.Add(new Separator());
        if (hasFiles)
            menu.Items.Add(MenuItem("刪除", () => App.Current.DeleteItems(selected, permanently: false), "Delete"));
        if (selected.Count == 1 && !system)
            menu.Items.Add(MenuItem("重新命名", () => BeginRename(id), "F2"));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("內容", () => FileActions.ShowProperties(id), "Alt+Enter"));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action? onClick, string? gesture = null, bool bold = false)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (bold)
            item.FontWeight = FontWeights.Bold;
        if (onClick != null)
            item.Click += (_, _) => onClick();
        return item;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>桌面上的一個圖示：圖 + 名稱（白字黑影，跟 Windows 桌面一樣）。</summary>
    private sealed class IconView : Border
    {
        private readonly TextBlock _label;
        private bool _selected;
        private bool _dropTarget;

        public IconView(FenceItem item, double iconSize)
        {
            Item = item;
            Background = HitBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(3);
            Padding = new Thickness(2, 3, 2, 3);
            ToolTip = item.DisplayName;
            ToolTipService.SetInitialShowDelay(this, 900);

            var image = new Image { Width = iconSize, Height = iconSize, Margin = new Thickness(0, 0, 0, 3) };
            image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FenceItem.Icon)) { Source = item });
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            _label = new TextBlock
            {
                Text = item.DisplayName,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                LineHeight = 16,
                Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.95, Color = Colors.Black },
            };

            var stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(_label);
            Child = stack;
            Opacity = item.ItemOpacity;
            UpdateLook();
        }

        public FenceItem Item { get; }
        public string Id => Item.FullPath;
        public PixelPoint Pixel { get; set; }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                    return;
                _selected = value;
                Panel.SetZIndex(this, value ? 10 : 0);
                // 選取時名稱完整顯示，跟 Windows 桌面一樣
                _label.MaxHeight = value ? double.PositiveInfinity : 32;
                UpdateLook();
            }
        }

        public bool IsDropTarget
        {
            get => _dropTarget;
            set
            {
                if (_dropTarget == value)
                    return;
                _dropTarget = value;
                UpdateLook();
            }
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            UpdateLook();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            UpdateLook();
        }

        private void UpdateLook()
        {
            Color accent = ThemeHelper.AccentColor;
            if (_selected || _dropTarget)
            {
                Background = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, accent.R, accent.G, accent.B));
            }
            else if (IsMouseOver)
            {
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                Background = HitBrush;
                BorderBrush = Brushes.Transparent;
            }
        }
    }
}
