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
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private DesktopWatcher? _desktopWatcher;
    private DesktopDoubleClickHook? _doubleClickHook;
    private IntPtr _progman;
    private bool _fencesHidden;
    private bool _sessionEnding;

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
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SyncDesktopIcons();
            RecordMonitorLayout();
            ConfigStore.Save(Config);
        };
        // 登出 / 關機時不還原桌面圖示，下次開機 MyFences 啟動後也還是藏著，比較不會閃一下
        SessionEnding += (_, _) => _sessionEnding = true;

        Config.PageCount = Math.Max(Math.Max(1, Config.PageCount), Config.Fences.Select(f => f.Page + 1).DefaultIfEmpty(1).Max());
        Config.CurrentPage = Math.Clamp(Config.CurrentPage, 0, Config.PageCount - 1);

        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);

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

        SyncDesktopIcons();

        _doubleClickHook = new DesktopDoubleClickHook(Dispatcher, OnDesktopDoubleClick) { Enabled = Config.QuickHideEnabled };

        TryEnsureDailySnapshot();
        _dailySnapshotTimer.Tick += (_, _) => TryEnsureDailySnapshot();
        _dailySnapshotTimer.Start();

        if (!File.Exists(ConfigStore.ConfigPath))
        {
            ConfigStore.Save(Config);
            _tray.ShowBalloon("已建立預設分區。把檔案拖進分區，或右鍵分區標題列調整設定。");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        _desktopCheckTimer.Stop();
        _dailySnapshotTimer.Stop();
        _doubleClickHook?.Dispose();
        _displayChangeTimer.Stop();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_mutex != null)
        {
            _saveTimer.Stop();
            RecordMonitorLayout();
            // 結束 MyFences 時把藏起來的桌面圖示還回去，免得檔案「不見」
            if (!_sessionEnding)
                SetDesktopIconsUnhidden();
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

    public void CreateFence(FenceKind kind)
    {
        string? folder = null;
        string name = "新分區";
        if (kind == FenceKind.Folder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇要顯示在分區裡的資料夾" };
            if (dialog.ShowDialog() != true)
                return;

            folder = dialog.FolderName;
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
        OpenFence(fence);
        RequestSave();
    }

    public void DeleteFence(FenceWindow window)
    {
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
    }

    public void SetHideFencedDesktopIcons(bool enabled)
    {
        Config.HideFencedDesktopIcons = enabled;
        SyncDesktopIcons();
        RequestSave();
    }

    /// <summary>讓桌面上「已放進分區的檔案」都設成隱藏，拿出分區的還原。</summary>
    public void SyncDesktopIcons()
    {
        IEnumerable<string> fenced = Config.HideFencedDesktopIcons
            ? Config.Fences.Where(f => f.Kind == FenceKind.Standard).SelectMany(f => f.AllItemLists()).SelectMany(l => l)
            : Enumerable.Empty<string>();

        DesktopIconSync.Plan plan = DesktopIconSync.Compute(
            fenced,
            DesktopHelper.GetDesktopDirectories(),
            Config.HiddenDesktopItems,
            p => File.Exists(p) || Directory.Exists(p),
            DesktopHelper.IsHidden);

        foreach (string path in plan.ToUnhide)
        {
            DesktopHelper.TrySetHidden(path, false);
            Config.HiddenDesktopItems.RemoveAll(p => PathEquals(p, path));
        }

        foreach (string path in plan.ToHide)
        {
            // 例如公用桌面的檔案需要系統管理員權限，改不了就算了，照樣顯示在分區裡
            if (DesktopHelper.TrySetHidden(path, true))
                Config.HiddenDesktopItems.Add(path);
        }
    }

    private void SetDesktopIconsUnhidden()
    {
        foreach (string path in Config.HiddenDesktopItems)
            DesktopHelper.TrySetHidden(path, false);
        Config.HiddenDesktopItems.Clear();
        ConfigStore.Save(Config);
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
        if (_doubleClickHook != null)
            _doubleClickHook.Enabled = enabled;
        RequestSave();
    }

    /// <summary>依規則整理整個桌面；整理前先自動備份一份快照。</summary>
    public int SortDesktop()
    {
        CreateSnapshot("整理桌面前自動備份", SnapshotKind.Backup);
        return ApplyRules(DesktopHelper.EnumerateDesktopItems());
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

        foreach (FenceWindow window in _windows.ToList())
        {
            _windows.Remove(window);
            window.CloseIntentionally();
        }

        Config.Fences = snapshot.Fences;
        Config.PageCount = Math.Max(Config.PageCount, Config.Fences.Select(f => f.Page + 1).DefaultIfEmpty(1).Max());
        if (_fencesHidden)
            ToggleFences();
        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);
        RequestSave();
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
            RequestSave();
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
        if (Config.AutoSortNewItems)
            ApplyRules(new[] { path });
    }

    private void OnDesktopItemRenamed(string oldPath, string newPath)
    {
        // 藏起來的檔案改名後屬性還在，追蹤清單也要跟著改名，之後才還原得了
        int tracked = Config.HiddenDesktopItems.FindIndex(p => PathEquals(p, oldPath));
        if (tracked >= 0)
            Config.HiddenDesktopItems[tracked] = newPath;

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
            RequestSave();
        else
            OnDesktopItemCreated(newPath);
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
    }
}
