using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

internal static class DesktopHelper
{
    public static IntPtr GetProgman() => FindWindow("Progman", null);

    /// <summary>
    /// 把視窗的擁有者設成 Progman（桌面）。這樣按 Win+D「顯示桌面」時分區不會被縮小，
    /// 而且分區永遠在一般視窗下方。
    /// </summary>
    public static void GlueToDesktop(IntPtr hwnd)
    {
        IntPtr progman = GetProgman();
        if (progman != IntPtr.Zero)
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, progman);
    }

    public static void SendToBottom(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>設定 / 取消隱藏屬性（只用來還原舊版本藏起來的檔案）。失敗回傳 false。</summary>
    public static bool TrySetHidden(string path, bool hidden)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            FileAttributes updated = hidden ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden;
            if (updated != attributes)
                File.SetAttributes(path, updated);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>螢幕上這個點是不是桌面（不是其他視窗）；是的話順便回傳桌面圖示的 ListView。</summary>
    public static bool IsDesktopAt(POINT pt, out IntPtr listView)
    {
        listView = IntPtr.Zero;
        IntPtr hwnd = WindowFromPoint(pt);
        string cls = GetClassName(hwnd);

        if (cls == "SysListView32")
        {
            listView = hwnd;
            hwnd = GetParent(hwnd);
            cls = GetClassName(hwnd);
            if (cls != "SHELLDLL_DefView")
            {
                listView = IntPtr.Zero;
                return false; // 別的程式裡的 ListView
            }
        }

        if (cls == "SHELLDLL_DefView")
        {
            hwnd = GetParent(hwnd);
            cls = GetClassName(hwnd);
        }

        return cls is "Progman" or "WorkerW";
    }

    /// <summary>滑鼠游標現在是不是在桌面上。</summary>
    public static bool IsCursorOverDesktop() => GetCursorPos(out POINT pt) && IsDesktopAt(pt, out _);

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";

        var sb = new System.Text.StringBuilder(64);
        return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static IEnumerable<string> GetDesktopDirectories()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            string dir = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                yield return dir;
        }
    }

    public static IEnumerable<string> EnumerateDesktopItems()
    {
        var result = new List<string>();
        foreach (string dir in GetDesktopDirectories())
            result.AddRange(EnumerateVisibleEntries(dir));
        return result;
    }

    /// <summary>列出資料夾內容（跳過隱藏 / 系統檔，例如 desktop.ini），資料夾在前。</summary>
    public static List<string> EnumerateVisibleEntries(string dir)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        try
        {
            foreach (FileSystemInfo info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;

                (info is DirectoryInfo ? dirs : files).Add(info.FullName);
            }
        }
        catch (Exception)
        {
            // 資料夾不存在或沒有權限：回傳已讀到的部分
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        dirs.Sort((a, b) => comparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        files.Sort((a, b) => comparer.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        dirs.AddRange(files);
        return dirs;
    }

    /// <summary>
    /// 在 WM_WINDOWPOSCHANGING 裡呼叫：讓視窗排在「桌面圖示那一層」的正上方
    /// （一般視窗之下、桌面圖示之上），不管被誰啟動都不會跑到前面。
    /// </summary>
    public static void KeepJustAboveDesktopIcons(IntPtr hwnd, IntPtr lParam)
    {
        var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((pos.flags & SWP_NOZORDER) != 0)
            return;

        IntPtr host = GetIconHost();
        if (host == IntPtr.Zero || host == GetProgman())
        {
            // 桌面圖示在 Progman 裡：擁有者是 Progman 的視窗最低只會到 Progman 上面，正好在圖示之上
            pos.hwndInsertAfter = HWND_BOTTOM;
        }
        else
        {
            // 桌面圖示在另一個最上層 WorkerW 裡：插在它的正上方
            IntPtr above = GetWindow(host, GW_HWNDPREV);
            if (above == hwnd)
                pos.flags |= SWP_NOZORDER;
            else
                pos.hwndInsertAfter = above; // Zero = HWND_TOP（WorkerW 已經是最上面）
        }

        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
    }

    /// <summary>檔案是不是直接放在桌面上（個人桌面或公用桌面，不含子資料夾）。</summary>
    public static bool IsOnDesktop(string path)
    {
        string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
        return parent != null && GetDesktopDirectories().Any(d =>
            string.Equals(d.TrimEnd(Path.DirectorySeparatorChar), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>桌面圖示的 ListView（SysListView32）。</summary>
    public static IntPtr GetDesktopListView()
    {
        IntPtr defView = GetDefView();
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>裝著桌面圖示（SHELLDLL_DefView）的那個最上層視窗：Progman 或某個 WorkerW。</summary>
    public static IntPtr GetIconHost()
    {
        IntPtr defView = GetDefView();
        return defView == IntPtr.Zero ? IntPtr.Zero : GetParent(defView);
    }

    /// <summary>
    /// 取得「桌布層」視窗：畫在桌面圖示後面、桌布前面（動態桌布軟體也是放在這裡）。
    /// 先請 Progman 產生這個 WorkerW（0x052C 訊息），再依 Windows 版本找出來。找不到回傳 Zero。
    /// </summary>
    public static IntPtr GetWallpaperLayer()
    {
        IntPtr progman = GetProgman();
        if (progman == IntPtr.Zero)
            return IntPtr.Zero;

        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0 /* SMTO_NORMAL */, 1000, out _);

        // Windows 11 24H2 之後：WorkerW 是 Progman 的子視窗，排在桌面圖示後面
        IntPtr child = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (child != IntPtr.Zero)
            return child;

        // 之前的版本：桌面圖示搬到一個最上層 WorkerW，桌布層是它後面的下一個 WorkerW
        IntPtr worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
        }

        return IntPtr.Zero;
    }

    private static IntPtr GetDefView()
    {
        IntPtr defView = FindWindowEx(GetProgman(), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
            return defView;

        // 設定過桌布輪播等情況下，DefView 會被搬到某個 WorkerW 底下
        IntPtr worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return defView;
        }

        return IntPtr.Zero;
    }
}
