using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MyFences.Models;
using MyFences.Services;
using MyFences.Views;

namespace MyFences;

public partial class App : Application
{
    private readonly List<FenceWindow> _windows = new();
    private readonly List<DesktopCanvas> _canvases = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, PixelPoint> _pendingPlacements = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingPrune = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _desktopItems = new();
    private bool _refreshEnumerate;
    private PixelPoint? _lastRightClick;
    private DateTime _lastRightClickTime;
    private string? _renameAfterRefresh;
    private bool _desktopLoaded;
    private bool _canvasRebuildPending;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _desktopCheckTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _dailySnapshotTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _displayChangeTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private List<ScreenInfo> _screens = new();
    private PageIndicator? _pageIndicator;
    private PagerBar? _pager;
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private DesktopWatcher? _desktopWatcher;
    private DesktopMouseHook? _mouseHook;
    private DesktopSelectionOverlay? _drawOverlay;
    private IntPtr _progman;
    private bool _fencesHidden;

    public static new App Current => (App)Application.Current;

    public AppConfig Config { get; private set; } = new();
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\MyFences.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show("MyFences 已經在執行中（請看系統匣）。", "MyFences");
            IsExiting = true;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MyFences 發生錯誤", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        Config = ConfigStore.Load();
        // JSON 讀回來的字典不分大小寫的設定會不見，重建一次
        var positions = new Dictionary<string, SavedPoint>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, SavedPoint value) in Config.DesktopPositions)
            positions[key] = value;
        Config.DesktopPositions = positions;

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            LayoutDesktop(); // 分區移動 / 縮放後，被蓋住的桌面圖示要讓開
            RecordMonitorLayout();
            ConfigStore.Save(Config);
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RefreshDesktopNow(_refreshEnumerate);
        };
        _pruneTimer.Tick += (_, _) =>
        {
            _pruneTimer.Stop();
            PruneDeletedFromFences();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreExplorerIcons();

        UnhideLegacyHiddenFiles();
        TakeOverDesktopIcons();

        Config.PageCount = Math.Max(Math.Max(1, Config.PageCount), Config.Fences.Select(f => f.Page + 1).DefaultIfEmpty(1).Max());
        Config.CurrentPage = Math.Clamp(Config.CurrentPage, 0, Config.PageCount - 1);

        // 先建立桌面圖示層，分區才能黏在它上面（分區一定蓋在桌面圖示之上）
        _screens = GetScreens();
        DesktopHelper.OwnerProvider = () => _canvases.Count > 0 ? _canvases[^1].Handle : IntPtr.Zero;
        UpdateCanvases();

        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);
        UpdatePager();

        // 螢幕組合跟上次結束時不同（例如筆電拔掉外接螢幕後才開機）就換算一次位置
        if (Config.LastScreens.Count > 0)
            RemapForScreens(Config.LastScreens, _screens);
        Config.LastScreens = _screens;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _displayChangeTimer.Tick += (_, _) =>
        {
            _displayChangeTimer.Stop();
            HandleDisplayChange();
        };

        _tray = new TrayIcon(this);

        _hotkeys = new HotkeyManager();
        if (!_hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F, ToggleFences))
            _tray.ShowBalloon("Ctrl+Alt+F 已被其他程式占用，請改用系統匣選單顯示 / 隱藏分區。");
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.Right, () => SwitchPage(Config.CurrentPage + 1));
        _hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.Left, () => SwitchPage(Config.CurrentPage - 1));

        _desktopWatcher = new DesktopWatcher(Dispatcher, OnDesktopItemCreated, OnDesktopItemRenamed, OnDesktopItemChanged);

        // Explorer 重新啟動後 Progman 會換一個，要重新把分區黏回桌面
        _progman = DesktopHelper.GetProgman();
        _desktopCheckTimer.Tick += (_, _) =>
        {
            CheckDesktopRestarted();
            // Windows 的桌面圖示一直保持關閉（例如 Explorer 重新啟動後又打開了）
            if (DesktopHelper.AreDesktopIconsVisible())
                DesktopHelper.SetDesktopIconsVisible(false);
        };
        _desktopCheckTimer.Start();

        RefreshDesktopNow(enumerate: true);

        // Windows 切換淺色 / 深色或換強調色時，分區跟著變
        ThemeHelper.StartListening();
        ThemeHelper.Changed += () =>
        {
            foreach (FenceWindow window in _windows)
                window.ApplyAppearance();
            UpdatePager();
        };

        _mouseHook = new DesktopMouseHook(
            Dispatcher, OnDesktopDoubleClick, OnDesktopDrawing, OnDesktopDrawn,
            OnDesktopPress, OnDesktopBoxSelect, OnDesktopRightClick)
        {
            QuickHideEnabled = Config.QuickHideEnabled,
        };

        TryEnsureDailySnapshot();
        _dailySnapshotTimer.Tick += (_, _) => TryEnsureDailySnapshot();
        _dailySnapshotTimer.Start();

        if (!File.Exists(ConfigStore.ConfigPath))
        {
            ConfigStore.Save(Config);
            _tray.ShowBalloon("MyFences 已接管桌面圖示。把圖示拖進分區；在桌面按住 Ctrl 拖曳可以畫出新分區。");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _desktopCheckTimer.Stop();
        _dailySnapshotTimer.Stop();
        _mouseHook?.Dispose();
        _displayChangeTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_mutex != null)
        {
            _saveTimer.Stop();
            _refreshTimer.Stop();
            RecordMonitorLayout();
            // 結束 MyFences 時把 Windows 的桌面圖示還回去，免得檔案「不見」
            RestoreExplorerIcons();
            ConfigStore.Save(Config);
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }

        _desktopWatcher?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void Quit()
    {
        IsExiting = true;
        Shutdown();
    }

    /// <summary>延遲存檔，避免拖曳 / 縮放時一直寫檔。</summary>
    public void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>建立分區；at 有值時放在那個螢幕像素範圍（Ctrl+拖曳畫出來的）。建好後直接進入改名。</summary>
    public FenceWindow? CreateFence(FenceKind kind, PixelRect? at = null, string? folderPath = null)
    {
        string? folder = folderPath;
        string name = "新分區";
        if (kind == FenceKind.Folder && folder == null)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇要顯示在分區裡的資料夾" };
            if (dialog.ShowDialog() != true)
                return null;

            folder = dialog.FolderName;
        }

        if (folder != null)
        {
            string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            name = string.IsNullOrEmpty(folderName) ? folder : folderName;
        }

        Rect area = SystemParameters.WorkArea;
        int offset = Config.Fences.Count % 6 * 30;
        var fence = new FenceConfig
        {
            Name = name,
            Kind = kind,
            FolderPath = folder,
            Page = Config.CurrentPage,
            Left = area.Left + 60 + offset,
            Top = area.Top + 60 + offset,
        };

        Config.Fences.Add(fence);
        if (_fencesHidden)
            ToggleFences();
        FenceWindow window = OpenFence(fence);
        if (at is { } rect)
            window.PlaceAtPixels(rect);
        if (kind == FenceKind.Standard)
            window.BeginRename();
        RequestSave();
        return window;
    }

    /// <summary>在某個分區旁邊建立資料夾分區（一樣大小，放得下就放右邊）。</summary>
    public void CreateFolderFenceBeside(FenceWindow anchor, string folder)
    {
        PixelRect? at = null;
        if (anchor.GetPixelBounds() is { } bounds)
        {
            System.Drawing.Rectangle wa = System.Windows.Forms.Screen.FromRectangle(
                new System.Drawing.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height)).WorkingArea;
            at = LayoutMath.PlaceBeside(bounds, new PixelRect(wa.X, wa.Y, wa.Width, wa.Height));
        }

        CreateFence(FenceKind.Folder, at, folder);
    }

    public void DeleteFence(FenceWindow window)
    {
        // 分區裡的桌面檔案會回到桌面上
        PushUndo($"已刪除「{window.Config.Name}」，裡面的檔案都放回桌面了");
        Config.Fences.Remove(window.Config);
        _windows.Remove(window);
        window.CloseIntentionally();
        RefreshDesktop(enumerate: false);
        RequestSave();
    }

    public void ToggleFences()
    {
        _fencesHidden = !_fencesHidden;
        UpdateVisibility();
    }

    // ───────────── 桌面頁面 ─────────────

    /// <summary>切到某一頁（超出範圍會繞回第一頁 / 最後一頁）。</summary>
    public void SwitchPage(int page)
    {
        int count = Math.Max(1, Config.PageCount);
        page = ((page % count) + count) % count;
        Config.CurrentPage = page;
        _fencesHidden = false;
        UpdateVisibility();
        RequestSave();

        if (count > 1)
        {
            _pageIndicator ??= new PageIndicator();
            _pageIndicator.ShowMessage($"第 {page + 1} / {count} 頁");
        }
    }

    public void AddPage()
    {
        Config.PageCount++;
        SwitchPage(Config.PageCount - 1);
    }

    /// <summary>刪除目前頁面，上面的分區移到前一頁（不會刪除分區）。</summary>
    public void RemoveCurrentPage()
    {
        if (Config.PageCount <= 1)
            return;

        int current = Config.CurrentPage;
        int target = Math.Max(0, current - 1);
        foreach (FenceConfig fence in Config.Fences)
        {
            if (fence.Page == current)
                fence.Page = target;
            else if (fence.Page > current)
                fence.Page--;
        }

        Config.PageCount--;
        SwitchPage(target);
    }

    public void MoveFenceToPage(FenceWindow window, int page)
    {
        if (page >= Config.PageCount)
            Config.PageCount = page + 1;

        window.Config.Page = page;
        UpdateVisibility();
        RequestSave();
    }

    /// <summary>目前畫面上其他分區的位置（吸附用）。</summary>
    public List<PixelRect> GetVisibleFenceBounds(FenceWindow except) =>
        _windows.Where(w => w != except && w.IsVisible)
            .Select(w => w.GetPixelBounds())
            .OfType<PixelRect>()
            .ToList();

    private bool ShouldShow(FenceWindow window) => !_fencesHidden && window.Config.Page == Config.CurrentPage;

    private void UpdateVisibility()
    {
        // 桌面圖示層先顯示，分區才會蓋在它上面
        foreach (DesktopCanvas canvas in _canvases)
        {
            if (!_fencesHidden && !canvas.IsVisible)
                canvas.Show();
            else if (_fencesHidden && canvas.IsVisible)
                canvas.Hide();
        }

        foreach (FenceWindow window in _windows)
        {
            bool show = ShouldShow(window);
            if (show && !window.IsVisible)
                window.Show();
            else if (!show && window.IsVisible)
                window.Hide();
        }

        UpdatePager();
        LayoutDesktop();
    }

    /// <summary>桌面底部的頁面圓點：兩頁以上、而且分區沒被隱藏時才顯示。</summary>
    private void UpdatePager()
    {
        if (Config.PageCount <= 1 || _fencesHidden)
        {
            _pager?.Hide();
            return;
        }

        _pager ??= new PagerBar();
        _pager.Update(
            Config.PageCount,
            Config.CurrentPage,
            page => Config.Fences.Count(f => f.Page == page),
            SwitchPage);
    }

    public void SetLocked(bool locked)
    {
        Config.Locked = locked;
        foreach (FenceWindow window in _windows)
            window.ApplyChrome();
        RequestSave();
    }

    public void SetQuickHide(bool enabled)
    {
        Config.QuickHideEnabled = enabled;
        if (_mouseHook != null)
            _mouseHook.QuickHideEnabled = enabled;
        RequestSave();
    }

    /// <summary>依規則整理整個桌面；整理前先自動備份一份快照。</summary>
    public int SortDesktop()
    {
        CreateSnapshot("整理桌面前自動備份", SnapshotKind.Backup);
        string undoState = SerializeFences();
        int count = ApplyRules(ShellItems.EnumerateDesktop().Where(id => !ShellItems.IsSystemItem(id)));
        if (count > 0)
            PushUndo($"已整理 {count} 個桌面項目", undoState);
        return count;
    }

    public Snapshot? CreateSnapshot(string name, SnapshotKind kind)
    {
        try
        {
            return SnapshotStore.Create(Config, name, kind);
        }
        catch (Exception ex)
        {
            MessageBox.Show("建立快照失敗：" + ex.Message, "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>還原快照：換掉所有分區（還原前先備份目前狀態，還原錯了可以再還原回來）。</summary>
    public void RestoreSnapshot(Snapshot snapshot)
    {
        if (CreateSnapshot("還原快照前自動備份", SnapshotKind.Backup) == null)
            return;

        PushUndo($"已還原到「{snapshot.Name}」");
        ReplaceFences(snapshot.Fences);
    }

    /// <summary>整組換掉分區（還原快照、復原用）：關掉所有分區視窗再依新設定開回來。</summary>
    private void ReplaceFences(List<FenceConfig> fences)
    {
        foreach (FenceWindow window in _windows.ToList())
        {
            _windows.Remove(window);
            window.CloseIntentionally();
        }

        Config.Fences = fences;
        Config.PageCount = Math.Max(Config.PageCount, Config.Fences.Select(f => f.Page + 1).DefaultIfEmpty(1).Max());
        if (_fencesHidden)
            ToggleFences();
        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);
        UpdatePager();
        RefreshDesktop(enumerate: false);
        RequestSave();
    }

    // ───────────── 復原 ─────────────

    private const int MaxUndo = 30;
    private readonly List<(string State, string Message)> _undoStack = new();

    private string SerializeFences() => System.Text.Json.JsonSerializer.Serialize(Config.Fences, ConfigStore.JsonOptions);

    /// <summary>
    /// 在改動「之前」呼叫：記下目前所有分區的狀態，並在右下角顯示附「復原」按鈕的提示。
    /// state 可以傳入事先記下的狀態（改動完才知道要不要提示時用）。
    /// </summary>
    public void PushUndo(string message, string? state = null)
    {
        _undoStack.Add((state ?? SerializeFences(), message));
        if (_undoStack.Count > MaxUndo)
            _undoStack.RemoveAt(0);
        Toast.ShowMessage(message, "復原", Undo);
    }

    public void Undo()
    {
        if (_undoStack.Count == 0)
        {
            Toast.ShowMessage("沒有可以復原的動作");
            return;
        }

        (string state, string message) = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        var fences = System.Text.Json.JsonSerializer.Deserialize<List<FenceConfig>>(state, ConfigStore.JsonOptions);
        if (fences == null)
            return;

        ReplaceFences(fences);
        Toast.ShowMessage("已復原");
    }

    /// <summary>把還沒在任何分區裡的項目，依各分區的規則放進去。回傳放入的數量。</summary>
    public int ApplyRules(IEnumerable<string> paths)
    {
        int count = 0;
        var touched = new HashSet<FenceWindow>();
        foreach (string path in paths)
        {
            if (IsInAnyFence(path))
                continue;

            // 優先順序高的分區先比對；同順序就照建立順序
            FenceWindow? target = _windows
                .Where(w => w.Config.Kind == FenceKind.Standard)
                .OrderByDescending(w => w.Config.RulePriority)
                .FirstOrDefault(w => w.Config.Matches(path));
            if (target == null)
                continue;

            target.Config.CurrentItems.Add(path);
            touched.Add(target);
            count++;
        }

        foreach (FenceWindow window in touched)
            window.ReloadItems();
        if (count > 0)
        {
            RefreshDesktop(enumerate: false);
            RequestSave();
        }

        return count;
    }

    /// <summary>一個項目只會在一個分區裡：放進新分區時，從其他分區移除。</summary>
    public void RemoveFromOtherFences(string path, FenceWindow except)
    {
        foreach (FenceWindow window in _windows)
        {
            if (window == except || window.Config.Kind != FenceKind.Standard)
                continue;

            int removed = 0;
            foreach (List<string> items in window.Config.AllItemLists())
                removed += items.RemoveAll(p => PathEquals(p, path));
            if (removed > 0)
                window.ReloadItems();
        }
    }

    public static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsInAnyFence(string path) =>
        Config.Fences.Any(f => f.Kind == FenceKind.Standard && f.AllItemLists().Any(items => items.Any(p => PathEquals(p, path))));

    private FenceWindow OpenFence(FenceConfig config)
    {
        var window = new FenceWindow(config);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            // 不是我們關的（例如 Explorer 重啟把擁有者視窗一起帶走）就重新開回來
            if (!window.IsClosingIntentionally && !IsExiting && Config.Fences.Contains(config))
                Dispatcher.InvokeAsync(() => OpenFence(config), DispatcherPriority.Background);
        };
        _windows.Add(window);
        // 先建立視窗 handle（即使不在目前頁面），多螢幕換算才拿得到位置
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        if (ShouldShow(window))
            window.Show();
        return window;
    }

    private void OnDesktopItemCreated(string path)
    {
        _pendingPrune.Remove(path);
        bool explicitPlace = _pendingPlacements.ContainsKey(path) || Config.DesktopPositions.ContainsKey(path);
        if (!explicitPlace)
        {
            // 從 Windows 桌面右鍵「新增」「貼上」：放在按右鍵的地方；從其他地方拖到桌面：放在滑鼠位置
            bool recentRightClick = _lastRightClick != null && DateTime.Now - _lastRightClickTime < TimeSpan.FromMinutes(1);
            if (recentRightClick)
            {
                _pendingPlacements[path] = _lastRightClick!.Value;
                // 「新增資料夾」「新增 文字文件」這類：跟 Windows 一樣直接進入改名
                string name = Path.GetFileName(path);
                if (name.StartsWith("新增", StringComparison.Ordinal) || name.StartsWith("New ", StringComparison.OrdinalIgnoreCase))
                    _renameAfterRefresh = path;
            }
            else if (GetCursorPixel() is { } cursor && DesktopHelper.IsCursorOverDesktop())
            {
                _pendingPlacements[path] = cursor;
            }

            if (Config.AutoSortNewItems && _renameAfterRefresh == null)
                ApplyRules(new[] { path });
        }

        RefreshDesktop(enumerate: true);
    }

    private void OnDesktopItemRenamed(string oldPath, string newPath)
    {
        _pendingPrune.Remove(newPath);
        if (!ReplacePath(oldPath, newPath))
        {
            OnDesktopItemCreated(newPath);
            return;
        }

        RefreshDesktop(enumerate: true);
    }

    /// <summary>被刪除、屬性改變，或事件太多來不及處理（path 是整個桌面資料夾）。</summary>
    private void OnDesktopItemChanged(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            // 有些程式存檔是「刪掉再寫回」：等一下還是不在，才從分區拿掉
            _pendingPrune.Add(path);
            _pruneTimer.Stop();
            _pruneTimer.Start();
        }

        RefreshDesktop(enumerate: true);
    }

    private void PruneDeletedFromFences()
    {
        List<string> gone = _pendingPrune.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        _pendingPrune.Clear();
        if (gone.Count > 0)
            RemoveFromAllFences(gone);
    }

    /// <summary>項目改名 / 搬移後，分區和桌面位置都跟著換。回傳有沒有找到。</summary>
    private bool ReplacePath(string oldPath, string newPath)
    {
        bool found = false;
        foreach (FenceWindow window in _windows)
        {
            foreach (List<string> items in window.Config.AllItemLists())
            {
                int index = items.FindIndex(p => PathEquals(p, oldPath));
                if (index < 0)
                    continue;

                items[index] = newPath;
                window.ReloadItems();
                found = true;
            }
        }

        if (Config.DesktopPositions.Remove(oldPath, out SavedPoint? point))
        {
            Config.DesktopPositions[newPath] = point;
            found = true;
        }

        if (_pendingPlacements.Remove(oldPath, out PixelPoint pending))
            _pendingPlacements[newPath] = pending;

        if (found)
            RequestSave();
        return found;
    }

    private void RemoveFromAllFences(IReadOnlyCollection<string> paths)
    {
        bool changed = false;
        foreach (FenceWindow window in _windows)
        {
            if (window.Config.Kind != FenceKind.Standard)
                continue;

            int removed = 0;
            foreach (List<string> items in window.Config.AllItemLists())
                removed += items.RemoveAll(p => paths.Any(path => PathEquals(p, path)));
            if (removed > 0)
            {
                window.ReloadItems();
                changed = true;
            }
        }

        if (changed)
        {
            RefreshDesktop(enumerate: false);
            RequestSave();
        }
    }

    // ───────────── 桌面圖示（分區外）─────────────

    /// <summary>使用者自己的桌面資料夾（新增、貼上的東西放這裡，跟 Windows 一樣）。</summary>
    private static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>
    /// 每個桌面項目只有一個家（MECE）：在某個分區的分頁裡，或者在桌面上的某個位置。
    /// 稍後重新整理（短時間內很多變動只做一次）；enumerate = 要重新讀桌面資料夾。
    /// </summary>
    public void RefreshDesktop(bool enumerate)
    {
        _refreshEnumerate |= enumerate;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void RefreshDesktopNow(bool enumerate)
    {
        _refreshTimer.Stop();
        _refreshEnumerate = false;
        if (enumerate || !_desktopLoaded)
        {
            _desktopItems = ShellItems.EnumerateDesktop();
            _desktopLoaded = true;
        }

        LayoutDesktop();

        if (_renameAfterRefresh is { } rename)
        {
            _renameAfterRefresh = null;
            CanvasOf(rename)?.BeginRename(rename);
        }
    }

    /// <summary>分區外的桌面項目（每頁都顯示）。</summary>
    private List<string> LooseItems()
    {
        var fenced = new HashSet<string>(
            Config.Fences.Where(f => f.Kind == FenceKind.Standard).SelectMany(f => f.AllItemLists()).SelectMany(l => l),
            StringComparer.OrdinalIgnoreCase);
        return _desktopItems.Where(id => !fenced.Contains(id)).ToList();
    }

    /// <summary>算出每個桌面圖示的位置，交給各螢幕的圖示層顯示。</summary>
    private void LayoutDesktop()
    {
        if (_canvases.Count == 0 || !_desktopLoaded)
            return;

        List<string> loose = LooseItems();
        var looseSet = new HashSet<string>(loose, StringComparer.OrdinalIgnoreCase);
        // 不在桌面上的位置記錄清掉；但資料夾本身暫時讀不到（例如 OneDrive 還沒好）時先留著
        foreach (string key in Config.DesktopPositions.Keys.Where(k => !looseSet.Contains(k)).ToList())
        {
            bool folderMissing = !ShellItems.IsSystemItem(key) && Path.GetDirectoryName(key) is { } dir && !Directory.Exists(dir);
            if (!folderMissing)
                Config.DesktopPositions.Remove(key);
        }

        List<PixelRect> allFences = _windows.Select(w => w.GetLayoutBounds()).OfType<PixelRect>().ToList();
        List<PixelRect> visibleFences = _windows.Where(ShouldShow).Select(w => w.GetLayoutBounds()).OfType<PixelRect>().ToList();

        var placed = new Dictionary<string, PixelPoint>(StringComparer.OrdinalIgnoreCase);
        var occupied = new List<PixelPoint>();

        if (Config.AutoArrange)
        {
            foreach (string id in SortForArrange(loose))
            {
                PixelPoint spot = FindSpot(null, allFences, occupied);
                placed[id] = spot;
                occupied.Add(spot);
            }
        }
        else
        {
            // 1. 原本就有位置、還在螢幕上、沒被分區蓋住的，不動
            var needSpot = new List<string>();
            foreach (string id in loose)
            {
                if (!_pendingPlacements.ContainsKey(id) &&
                    Config.DesktopPositions.TryGetValue(id, out SavedPoint? saved) &&
                    CanvasAt(new PixelPoint(saved.X, saved.Y)) is { } canvas)
                {
                    var p = new PixelPoint(saved.X, saved.Y);
                    (int cw, int ch) = canvas.CellPixels;
                    if (!visibleFences.Any(f => IconLayout.CenterInside(f, p, cw, ch)))
                    {
                        placed[id] = p;
                        occupied.Add(p);
                        continue;
                    }
                }

                needSpot.Add(id);
            }

            // 2. 其他的找空位：有指定位置（放下、貼上、右鍵新增）就放在附近，否則從左上角往下找
            foreach (string id in needSpot)
            {
                PixelPoint? target = _pendingPlacements.TryGetValue(id, out PixelPoint t) ? t : null;
                PixelPoint spot = FindSpot(target, allFences, occupied);
                placed[id] = spot;
                occupied.Add(spot);
            }
        }

        _pendingPlacements.Clear();
        foreach ((string id, PixelPoint p) in placed)
            Config.DesktopPositions[id] = new SavedPoint { X = p.X, Y = p.Y };

        foreach (DesktopCanvas canvas in _canvases)
        {
            var mine = placed.Where(kv => CanvasAt(kv.Value) == canvas).Select(kv => (kv.Key, kv.Value)).ToList();
            canvas.SetItems(mine, Config.DesktopIconSize);
        }
    }

    /// <summary>自動排列的順序：系統圖示、資料夾、檔案，各自依名稱。</summary>
    private static IEnumerable<string> SortForArrange(IEnumerable<string> ids)
    {
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return ids
            .OrderBy(id => ShellItems.IsSystemItem(id) ? 0 : Directory.Exists(id) ? 1 : 2)
            .ThenBy(id => ShellItems.IsSystemItem(id) ? "" : Path.GetFileName(id), comparer);
    }

    /// <summary>找一個空格子：有 target 就找離它最近的，否則照 Windows 的習慣從主螢幕左上角往下找。</summary>
    private PixelPoint FindSpot(PixelPoint? target, List<PixelRect> fences, List<PixelPoint> occupied)
    {
        DesktopCanvas first = target is { } t0 ? CanvasAt(t0) ?? _canvases[0] : _canvases[0];
        foreach (DesktopCanvas canvas in _canvases.OrderBy(c => c == first ? 0 : 1))
        {
            (int cw, int ch) = canvas.CellPixels;
            PixelPoint? spot = target is { } t && canvas == first
                ? DesktopGrid.NearestFreeCell(new PixelPoint(t.X - cw / 2, t.Y - ch / 3), canvas.WorkArea, fences, occupied, cw, ch)
                : IconLayout.FindFreeSpot(canvas.WorkArea, fences, occupied, cw, ch);
            if (spot is { } found)
                return found;
        }

        // 整個桌面都滿了：疊在左上角
        return new PixelPoint(_canvases[0].WorkArea.X, _canvases[0].WorkArea.Y);
    }

    private DesktopCanvas? CanvasAt(PixelPoint p)
    {
        foreach (DesktopCanvas canvas in _canvases)
        {
            (int cw, int ch) = canvas.CellPixels;
            if (IconLayout.CenterInside(canvas.WorkArea, p, cw, ch))
                return canvas;
        }

        return null;
    }

    private DesktopCanvas? CanvasOf(string id) => _canvases.FirstOrDefault(c => c.PositionOf(id) != null);

    private static PixelPoint? GetCursorPixel() =>
        NativeMethods.GetCursorPos(out NativeMethods.POINT pt) ? new PixelPoint(pt.x, pt.y) : null;

    /// <summary>
    /// 把項目放到桌面上的某個位置：從桌面拖來的（origin 有值）整組一起平移；從分區拖來的會移出分區。
    /// </summary>
    public void PlaceOnDesktop(IReadOnlyList<string> ids, PixelPoint point, PixelPoint? origin)
    {
        if (origin is { } from)
        {
            int dx = point.X - from.X, dy = point.Y - from.Y;
            var moving = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var occupied = Config.DesktopPositions.Where(kv => !moving.Contains(kv.Key)).Select(kv => new PixelPoint(kv.Value.X, kv.Value.Y)).ToList();
            foreach (string id in ids)
            {
                if (!Config.DesktopPositions.TryGetValue(id, out SavedPoint? old))
                    continue;

                var target = new PixelPoint(old.X + dx, old.Y + dy);
                DesktopCanvas canvas = CanvasAt(target) ?? CanvasAt(point) ?? _canvases[0];
                (int cw, int ch) = canvas.CellPixels;
                PixelPoint spot = target;
                if (Config.SnapToGrid)
                {
                    spot = DesktopGrid.Snap(target, canvas.WorkArea, cw, ch);
                    if (!DesktopGrid.IsFree(spot, Array.Empty<PixelRect>(), occupied, cw, ch))
                        spot = DesktopGrid.NearestFreeCell(spot, canvas.WorkArea, Array.Empty<PixelRect>(), occupied, cw, ch) ?? spot;
                }
                else
                {
                    spot = new PixelPoint(
                        Math.Clamp(target.X, canvas.WorkArea.X, canvas.WorkArea.Right - cw),
                        Math.Clamp(target.Y, canvas.WorkArea.Y, canvas.WorkArea.Bottom - ch));
                }

                Config.DesktopPositions[id] = new SavedPoint { X = spot.X, Y = spot.Y };
                occupied.Add(spot);
            }

            if (Config.AutoArrange)
                Toast.ShowMessage("「自動排列」開著，圖示會照名稱排好（可以在設定裡關掉）");
        }
        else
        {
            // 從分區拖出來：桌面上的檔案回到桌面，放在放開的地方
            string undo = SerializeFences();
            foreach (string id in ids)
            {
                Config.DesktopPositions.Remove(id);
                _pendingPlacements[id] = point;
            }

            int before = CountFenced();
            RemoveFromAllFencesQuiet(ids);
            if (CountFenced() != before)
                PushUndo(ids.Count == 1 ? $"「{FenceItem.Create(ids[0]).DisplayName}」已放回桌面" : $"{ids.Count} 個項目已放回桌面", undo);
        }

        RefreshDesktopNow(enumerate: false);
        CanvasAt(point)?.Select(ids, focus: true);
        RequestSave();
    }

    private int CountFenced() => Config.Fences.Sum(f => f.AllItemLists().Sum(l => l.Count));

    private void RemoveFromAllFencesQuiet(IReadOnlyCollection<string> paths)
    {
        foreach (FenceWindow window in _windows)
        {
            int removed = 0;
            foreach (List<string> items in window.Config.AllItemLists())
                removed += items.RemoveAll(p => paths.Any(path => PathEquals(p, path)));
            if (removed > 0)
                window.ReloadItems();
        }
    }

    /// <summary>移出分區、放回桌面上的空位（分區選單「放回桌面」）。</summary>
    public void ReturnToDesktop(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;

        PushUndo(paths.Count == 1 ? $"「{FenceItem.Create(paths.First()).DisplayName}」已放回桌面" : $"{paths.Count} 個項目已放回桌面");
        RemoveFromAllFencesQuiet(paths);
        RefreshDesktopNow(enumerate: false);
        RequestSave();
    }

    /// <summary>拖曳中讓桌面圖示層整片都接得到東西。</summary>
    public void SetDesktopDropMode(bool on)
    {
        foreach (DesktopCanvas canvas in _canvases)
            canvas.SetDropMode(on);
    }

    internal void OnCanvasActivated(DesktopCanvas active)
    {
        foreach (DesktopCanvas canvas in _canvases.Where(c => c != active))
            canvas.ClearSelection();
    }

    /// <summary>刪除（進資源回收筒），刪掉的東西也從分區拿掉。</summary>
    public void DeleteItems(IReadOnlyCollection<string> ids, bool permanently)
    {
        if (!FileActions.Delete(ids, permanently))
            return;

        RemoveFromAllFences(ids.Where(id => !File.Exists(id) && !Directory.Exists(id)).ToList());
        RefreshDesktop(enumerate: true);
    }

    /// <summary>把項目搬進資料夾（放在桌面上的資料夾圖示上）。</summary>
    public void MoveItemsInto(IReadOnlyCollection<string> ids, string folder)
    {
        List<string> files = FileActions.RealFiles(ids);
        if (files.Count == 0)
            return;

        FileActions.Move(files, folder);
        RemoveFromAllFences(files.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList());
        RefreshDesktop(enumerate: true);
    }

    /// <summary>從檔案總管拖來的檔案：複製 / 搬到資料夾；放到桌面時，新的東西放在 near 附近。</summary>
    public void TransferInto(IReadOnlyCollection<string> files, string folder, bool move, PixelPoint? near)
    {
        List<string> created = move ? FileActions.Move(files, folder) : FileActions.Copy(files, folder);
        if (near is { } point)
        {
            foreach (string path in created)
                _pendingPlacements[path] = point;
        }

        RemoveFromAllFences(files.Where(f => !File.Exists(f) && !Directory.Exists(f)).ToList());
        RefreshDesktopNow(enumerate: true);
    }

    /// <summary>Ctrl+V 貼到桌面，新的東西放在 near 附近。</summary>
    public void PasteOnDesktop(PixelPoint near)
    {
        List<string> created = FileActions.PasteInto(UserDesktop);
        foreach (string path in created)
            _pendingPlacements[path] = near;
        RefreshDesktopNow(enumerate: true);
        if (created.Count > 0)
            CanvasAt(near)?.Select(created, focus: true);
    }

    public void PasteInto(string folder)
    {
        FileActions.PasteInto(folder);
        RefreshDesktop(enumerate: true);
    }

    /// <summary>在分區裡 Ctrl+V：檔案貼到桌面資料夾，然後放進這個分區。</summary>
    public void PasteIntoFence(FenceWindow window)
    {
        List<string> created = FileActions.PasteInto(UserDesktop);
        if (created.Count == 0)
            return;

        AddToFence(created, window.Config.Id);
    }

    public void NewFolderOnDesktop(PixelPoint near)
    {
        if (FileActions.CreateNewFolder(UserDesktop) is not { } path)
            return;

        _pendingPlacements[path] = near;
        _renameAfterRefresh = path;
        RefreshDesktopNow(enumerate: true);
    }

    public void RenameItem(string id, string newName)
    {
        try
        {
            string newPath = ShellItems.Rename(id, newName);
            if (PathEquals(newPath, id) && newPath == id)
                return;

            ReplacePath(id, newPath);
            RefreshDesktopNow(enumerate: true);
            CanvasOf(newPath)?.Select(new[] { newPath }, focus: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show("無法重新命名：" + ex.Message, "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>目前頁面上可以放東西的分區（右鍵「放進分區」用）。</summary>
    public List<(string Id, string Name)> FenceTargets() =>
        _windows.Where(w => w.Config.Kind == FenceKind.Standard && w.Config.Page == Config.CurrentPage)
            .Select(w => (w.Config.Id, w.Config.Name))
            .ToList();

    public void AddToFence(IReadOnlyCollection<string> ids, string fenceId)
    {
        FenceWindow? window = _windows.FirstOrDefault(w => w.Config.Id == fenceId);
        if (window == null)
            return;

        foreach (string id in ids)
        {
            RemoveFromOtherFences(id, window);
            foreach (List<string> other in window.Config.AllItemLists())
                other.RemoveAll(p => PathEquals(p, id));
            window.Config.CurrentItems.Add(id);
        }

        window.ReloadItems();
        RefreshDesktopNow(enumerate: false);
        RequestSave();
    }

    public void SetDesktopIconSize(double size)
    {
        Config.DesktopIconSize = size;
        RefreshDesktopNow(enumerate: false);
        RequestSave();
    }

    public void SetAutoArrange(bool enabled)
    {
        Config.AutoArrange = enabled;
        RefreshDesktopNow(enumerate: false);
        RequestSave();
    }

    public void SetSnapToGrid(bool enabled)
    {
        Config.SnapToGrid = enabled;
        RequestSave();
    }

    /// <summary>依名稱排列一次（不打開自動排列）。</summary>
    public void ArrangeDesktopByName()
    {
        Config.DesktopPositions.Clear();
        foreach (string id in SortForArrange(LooseItems()))
            _pendingPlacements.Remove(id);
        bool auto = Config.AutoArrange;
        Config.AutoArrange = true;
        RefreshDesktopNow(enumerate: true);
        Config.AutoArrange = auto;
        RequestSave();
    }

    // ───────────── 接管 Windows 的桌面圖示 ─────────────

    /// <summary>舊版本用「隱藏屬性」藏過的檔案，全部還原（只為了升級）。</summary>
    private void UnhideLegacyHiddenFiles()
    {
        if (Config.HiddenDesktopItems.Count == 0)
            return;

        foreach (string path in Config.HiddenDesktopItems)
            DesktopHelper.TrySetHidden(path, false);
        Config.HiddenDesktopItems.Clear();
        ConfigStore.Save(Config);
    }

    /// <summary>關掉 Windows 的「顯示桌面圖示」，改由 MyFences 畫。原本的狀態先記下來（當掉重開也記得）。</summary>
    private void TakeOverDesktopIcons()
    {
        if (Config.ExplorerIconsWereVisible == null)
        {
            Config.ExplorerIconsWereVisible = DesktopHelper.AreDesktopIconsVisible();
            ConfigStore.Save(Config);
        }

        DesktopHelper.SetDesktopIconsVisible(false);
    }

    private void RestoreExplorerIcons()
    {
        if (Config.ExplorerIconsWereVisible is not { } wasVisible)
            return;

        try
        {
            if (wasVisible)
                DesktopHelper.SetDesktopIconsVisible(true);
            Config.ExplorerIconsWereVisible = null;
            ConfigStore.Save(Config);
        }
        catch (Exception)
        {
            // 結束中，盡力就好；下次啟動還記得要還原
        }
    }

    // ───────────── 桌面上的滑鼠 ─────────────

    private void OnDesktopPress(PixelPoint point)
    {
        // 點在桌面空白處：取消選取（跟 Windows 一樣）
        foreach (DesktopCanvas canvas in _canvases)
            canvas.ClearSelection();
    }

    private void OnDesktopBoxSelect(PixelRect? rect)
    {
        foreach (DesktopCanvas canvas in _canvases)
            canvas.BoxSelect(rect);
    }

    private void OnDesktopRightClick(PixelPoint point)
    {
        _lastRightClick = point;
        _lastRightClickTime = DateTime.Now;
    }

    // ───────────── 各螢幕的桌面圖示層 ─────────────

    /// <summary>每個螢幕一層；螢幕變了就調整（第一層一直留著，分區都黏在它上面）。</summary>
    private void UpdateCanvases()
    {
        for (int i = 0; i < _screens.Count; i++)
        {
            if (i >= _canvases.Count)
            {
                DesktopCanvas canvas = CreateCanvas();
                canvas.OwnerHandle = i == 0 ? IntPtr.Zero : _canvases[i - 1].Handle;
                _canvases.Add(canvas);
            }

            _canvases[i].PlaceOn(_screens[i].WorkArea);
        }

        while (_canvases.Count > Math.Max(1, _screens.Count))
        {
            DesktopCanvas extra = _canvases[^1];
            _canvases.RemoveAt(_canvases.Count - 1);
            extra.CloseIntentionally();
        }

        for (int i = 0; i < _canvases.Count; i++)
        {
            if (!_fencesHidden && !_canvases[i].IsVisible)
            {
                _canvases[i].Show();
                _canvases[i].PlaceOn(_screens[Math.Min(i, _screens.Count - 1)].WorkArea);
            }
        }
    }

    private DesktopCanvas CreateCanvas()
    {
        var canvas = new DesktopCanvas();
        canvas.Closed += (_, _) =>
        {
            if (canvas.IsClosingIntentionally || IsExiting || _canvasRebuildPending)
                return;

            // 例如 Explorer 重新啟動時被一起關掉：等這一串關閉結束後，整組重建（分區會自己重開、黏回來）
            _canvasRebuildPending = true;
            Dispatcher.InvokeAsync(RebuildCanvases);
        };
        return canvas;
    }

    private void RebuildCanvases()
    {
        _canvasRebuildPending = false;
        foreach (DesktopCanvas canvas in _canvases.Where(c => !c.IsClosed).ToList())
            canvas.CloseIntentionally();
        _canvases.Clear();
        UpdateCanvases();
        foreach (FenceWindow window in _windows.ToList())
            window.AttachToDesktop();
        _pager?.AttachToDesktop();
        RefreshDesktop(enumerate: true);
    }

    private void OnDesktopDoubleClick()
    {
        ToggleFences();
        if (_fencesHidden && !Config.QuickHideHintShown)
        {
            Config.QuickHideHintShown = true;
            RequestSave();
            _tray?.ShowBalloon("你剛剛雙擊了桌面空白處，分區已隱藏。再雙擊一次就會出現。\n不想要這個功能，可以在系統匣選單關掉。");
        }
    }

    // ───────────── Ctrl+拖曳建立分區 ─────────────

    private const int MinDrawWidth = 120;
    private const int MinDrawHeight = 80;

    private void OnDesktopDrawing(PixelRect? rect)
    {
        if (rect is not { } r)
        {
            _drawOverlay?.Hide();
            return;
        }

        _drawOverlay ??= new DesktopSelectionOverlay();
        _drawOverlay.Update(r, r.Width >= MinDrawWidth && r.Height >= MinDrawHeight);
    }

    private void OnDesktopDrawn(PixelRect rect)
    {
        if (rect.Width < MinDrawWidth || rect.Height < MinDrawHeight)
            return;

        // 框住的桌面圖示直接放進新分區（跟 Fences 一樣）
        List<string> inside = new();
        foreach (DesktopCanvas canvas in _canvases)
        {
            (int cw, int ch) = canvas.CellPixels;
            inside.AddRange(canvas.Ids.Where(id => canvas.PositionOf(id) is { } p && IconLayout.CenterInside(rect, p, cw, ch)));
        }

        if (CreateFence(FenceKind.Standard, rect) is { } window && inside.Count > 0)
            AddToFence(inside, window.Config.Id);
    }

    private void TryEnsureDailySnapshot()
    {
        try
        {
            SnapshotStore.EnsureDailySnapshot(Config);
        }
        catch (Exception)
        {
            // 自動快照失敗不打擾使用者，下個小時會再試
        }
    }

    // ───────────── 多螢幕 / 解析度 ─────────────

    private static List<ScreenInfo> GetScreens() =>
        System.Windows.Forms.Screen.AllScreens
            .OrderByDescending(s => s.Primary) // 主螢幕放第一個（桌面圖示預設從主螢幕排起）
            .Select(s => new ScreenInfo(
                s.DeviceName,
                new PixelRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height),
                s.Primary))
            .ToList();

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 螢幕設定變更時會連續觸發好幾次，等穩定下來再處理
        Dispatcher.InvokeAsync(() =>
        {
            _displayChangeTimer.Stop();
            _displayChangeTimer.Start();
        });
    }

    private void HandleDisplayChange()
    {
        List<ScreenInfo> newScreens = GetScreens();
        if (LayoutMath.ProfileKey(newScreens) == LayoutMath.ProfileKey(_screens) &&
            newScreens.Select(s => s.WorkArea).SequenceEqual(_screens.Select(s => s.WorkArea)))
        {
            return;
        }

        RemapForScreens(_screens, newScreens);
        _screens = newScreens;
        UpdateCanvases();
        RefreshDesktop(enumerate: false);
        Config.LastScreens = newScreens;
        RecordMonitorLayout();
        RequestSave();
    }

    /// <summary>
    /// 螢幕組合從 oldScreens 變成 newScreens：這組螢幕以前用過就還原當時的版面，
    /// 沒用過就從舊版面等比例換算。
    /// </summary>
    private void RemapForScreens(List<ScreenInfo> oldScreens, List<ScreenInfo> newScreens)
    {
        string oldKey = LayoutMath.ProfileKey(oldScreens);
        string newKey = LayoutMath.ProfileKey(newScreens);
        if (oldKey == newKey)
            return;

        Config.MonitorLayouts.TryGetValue(newKey, out Dictionary<string, SavedBounds>? saved);
        Config.MonitorLayouts.TryGetValue(oldKey, out Dictionary<string, SavedBounds>? previous);

        foreach (FenceWindow window in _windows)
        {
            string id = window.Config.Id;
            if (saved != null && saved.TryGetValue(id, out SavedBounds? known))
            {
                window.ApplyPixelBounds(new PixelRect(known.X, known.Y, known.Width, known.Height), known.ExpandedHeight);
                continue;
            }

            // Windows 可能已經自己把視窗挪過位置，優先用變更前記下來的位置來換算
            PixelRect? source = previous != null && previous.TryGetValue(id, out SavedBounds? before)
                ? new PixelRect(before.X, before.Y, before.Width, before.Height)
                : window.GetPixelBounds();
            if (source is not { } from)
                continue;

            PixelRect to = LayoutMath.Remap(from, oldScreens, newScreens);
            double scaleY = from.Height == 0 ? 1 : (double)to.Height / from.Height;
            window.ApplyPixelBounds(to, window.Config.Height * scaleY);
        }
    }

    /// <summary>記下目前螢幕組合下每個分區的位置。</summary>
    private void RecordMonitorLayout()
    {
        if (_screens.Count == 0)
            return;

        // 螢幕已經變了但還沒換算完，先不要記，免得把被 Windows 挪亂的位置存成舊組合的版面
        string key = LayoutMath.ProfileKey(_screens);
        if (key != LayoutMath.ProfileKey(GetScreens()))
            return;

        if (!Config.MonitorLayouts.TryGetValue(key, out Dictionary<string, SavedBounds>? layout))
            Config.MonitorLayouts[key] = layout = new Dictionary<string, SavedBounds>();

        layout.Clear();
        foreach (FenceWindow window in _windows)
        {
            if (window.GetPixelBounds() is { } r)
            {
                layout[window.Config.Id] = new SavedBounds
                {
                    X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, ExpandedHeight = window.Config.Height,
                };
            }
        }
    }

    private void CheckDesktopRestarted()
    {
        IntPtr progman = DesktopHelper.GetProgman();
        if (progman == IntPtr.Zero || progman == _progman)
            return;

        _progman = progman;
        foreach (DesktopCanvas canvas in _canvases)
            canvas.AttachToDesktop();
        foreach (FenceWindow window in _windows)
            window.AttachToDesktop();
        _pager?.AttachToDesktop();
    }
}
