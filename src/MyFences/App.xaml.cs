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
    private DesktopIconManager? _icons;
    private DesktopSelectionOverlay? _drawOverlay;
    private IntPtr _progman;
    private bool _fencesHidden;
    private bool _sessionEnding;

    public static new App Current => (App)Application.Current;

    public AppConfig Config { get; private set; } = new();

    /// <summary>負責把真正的桌面圖示排進分區。</summary>
    internal DesktopIconManager? Icons => _icons;
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
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            RecordMonitorLayout();
            ConfigStore.Save(Config);
        };
        // 登出 / 關機時不還原「自動排列」設定，免得下次開機桌面圖示被重新排過
        SessionEnding += (_, _) => _sessionEnding = true;

        UnhideLegacyHiddenFiles();

        // 先請檔案總管準備好「桌布層」（分區底色畫在這裡，桌面圖示的下面）；
        // 這會改變桌面的視窗結構，所以要在建立分區視窗之前做
        DesktopHelper.GetWallpaperLayer();

        Config.PageCount = Math.Max(Math.Max(1, Config.PageCount), Config.Fences.Select(f => f.Page + 1).DefaultIfEmpty(1).Max());
        Config.CurrentPage = Math.Clamp(Config.CurrentPage, 0, Config.PageCount - 1);

        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);
        UpdatePager();

        // 螢幕組合跟上次結束時不同（例如筆電拔掉外接螢幕後才開機）就換算一次位置
        _screens = GetScreens();
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

        _desktopWatcher = new DesktopWatcher(Dispatcher, OnDesktopItemCreated, OnDesktopItemRenamed);

        // Explorer 重新啟動後 Progman 會換一個，要重新把分區黏回桌面
        _progman = DesktopHelper.GetProgman();
        _desktopCheckTimer.Tick += (_, _) => CheckDesktopRestarted();
        _desktopCheckTimer.Start();

        // 等分區視窗都排好版，再開始把桌面圖示收進分區
        _icons = new DesktopIconManager(this, () => _windows);
        Dispatcher.InvokeAsync(() => _icons.Start(), DispatcherPriority.ApplicationIdle);

        // Windows 切換淺色 / 深色或換強調色時，分區跟著變
        ThemeHelper.StartListening();
        ThemeHelper.Changed += () =>
        {
            foreach (FenceWindow window in _windows)
                window.ApplyAppearance();
            UpdatePager();
        };

        _mouseHook = new DesktopMouseHook(Dispatcher, OnDesktopDoubleClick, OnDesktopDrawing, OnDesktopDrawn)
        {
            QuickHideEnabled = Config.QuickHideEnabled,
        };

        TryEnsureDailySnapshot();
        _dailySnapshotTimer.Tick += (_, _) => TryEnsureDailySnapshot();
        _dailySnapshotTimer.Start();

        if (!File.Exists(ConfigStore.ConfigPath))
        {
            ConfigStore.Save(Config);
            _tray.ShowBalloon("已建立預設分區。把桌面上的檔案拖進分區；在桌面按住 Ctrl 拖曳可以畫出新分區。");
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
            RecordMonitorLayout();
            // 停在螢幕外的圖示放回桌面；正常結束時也把「自動排列」還原成原本的設定
            _icons?.Shutdown(restoreAutoArrange: !_sessionEnding);
            _icons?.Dispose();
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
        // 裡面的圖示留在原地，變回一般的桌面圖示
        PushUndo($"已刪除「{window.Config.Name}」，裡面的圖示留在桌面上");
        Config.Fences.Remove(window.Config);
        _windows.Remove(window);
        window.CloseIntentionally();
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
        foreach (FenceWindow window in _windows)
        {
            bool show = ShouldShow(window);
            if (show && !window.IsVisible)
                window.Show();
            else if (!show && window.IsVisible)
                window.Hide();
        }

        UpdatePager();
        _icons?.RequestLayout();
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

    /// <summary>
    /// 舊版本用「隱藏屬性」把放進分區的桌面檔案藏起來；現在改成移動真正的桌面圖示，
    /// 所以啟動時把以前藏起來的檔案全部還原。
    /// </summary>
    private void UnhideLegacyHiddenFiles()
    {
        if (Config.HiddenDesktopItems.Count == 0)
            return;

        foreach (string path in Config.HiddenDesktopItems)
            DesktopHelper.TrySetHidden(path, false);
        Config.HiddenDesktopItems.Clear();
        ConfigStore.Save(Config);
    }

    /// <summary>把跑到螢幕外的桌面圖示找回來（設定視窗的按鈕）。</summary>
    public int RescueIcons() => _icons?.RescueParked() ?? 0;

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
        int count = ApplyRules(DesktopHelper.EnumerateDesktopItems());
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
        _icons?.RequestLayout();
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
    public int ApplyRules(IEnumerable<string> paths, bool layout = true)
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
            window.RefreshEmptyHint();
        if (count > 0)
        {
            RequestSave();
            if (layout)
                _icons?.RequestLayout();
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

    // 新檔案由 DesktopIconManager 在桌面上看到圖示時處理（它知道圖示放在哪裡）
    private static void OnDesktopItemCreated(string path)
    {
    }

    private void OnDesktopItemRenamed(string oldPath, string newPath)
    {
        bool updated = false;
        foreach (FenceWindow window in _windows)
        {
            foreach (List<string> items in window.Config.AllItemLists())
            {
                int index = items.FindIndex(p => PathEquals(p, oldPath));
                if (index < 0)
                    continue;

                items[index] = newPath;
                window.ReloadItems();
                updated = true;
            }
        }

        if (updated)
        {
            RequestSave();
            _icons?.RequestLayout();
        }
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

        CreateFence(FenceKind.Standard, rect);
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
        if (LayoutMath.ProfileKey(newScreens) == LayoutMath.ProfileKey(_screens))
            return;

        RemapForScreens(_screens, newScreens);
        _screens = newScreens;
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
        foreach (FenceWindow window in _windows)
            window.AttachToDesktop();
        _pager?.AttachToDesktop();
    }
}
