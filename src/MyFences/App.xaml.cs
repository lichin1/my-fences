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
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private DesktopWatcher? _desktopWatcher;
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
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            ConfigStore.Save(Config);
        };

        foreach (FenceConfig fence in Config.Fences)
            OpenFence(fence);

        _tray = new TrayIcon(this);

        _hotkeys = new HotkeyManager();
        if (!_hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.F, ToggleFences))
            _tray.ShowBalloon("Ctrl+Alt+F 已被其他程式占用，請改用系統匣選單顯示 / 隱藏分區。");

        _desktopWatcher = new DesktopWatcher(Dispatcher, OnDesktopItemCreated, OnDesktopItemRenamed);

        // Explorer 重新啟動後 Progman 會換一個，要重新把分區黏回桌面
        _progman = DesktopHelper.GetProgman();
        _desktopCheckTimer.Tick += (_, _) => CheckDesktopRestarted();
        _desktopCheckTimer.Start();

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
        if (_mutex != null)
        {
            _saveTimer.Stop();
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
        foreach (FenceWindow window in _windows)
        {
            if (_fencesHidden)
                window.Hide();
            else
                window.Show();
        }
    }

    public void SetLocked(bool locked)
    {
        Config.Locked = locked;
        foreach (FenceWindow window in _windows)
            window.ApplyChrome();
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

            FenceWindow? target = _windows.FirstOrDefault(w => w.Config.Kind == FenceKind.Standard && w.Config.Matches(path));
            if (target == null)
                continue;

            target.Config.Items.Add(path);
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

            if (window.Config.Items.RemoveAll(p => PathEquals(p, path)) > 0)
                window.ReloadItems();
        }
    }

    public static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsInAnyFence(string path) =>
        Config.Fences.Any(f => f.Kind == FenceKind.Standard && f.Items.Any(p => PathEquals(p, path)));

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
        if (!_fencesHidden)
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
        bool updated = false;
        foreach (FenceWindow window in _windows)
        {
            List<string> items = window.Config.Items;
            int index = items.FindIndex(p => PathEquals(p, oldPath));
            if (index < 0)
                continue;

            items[index] = newPath;
            window.ReloadItems();
            updated = true;
        }

        if (updated)
            RequestSave();
        else
            OnDesktopItemCreated(newPath);
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
