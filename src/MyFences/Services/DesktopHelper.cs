using System;
using System.Collections.Generic;
using System.IO;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

internal static class DesktopHelper
{
    private const int ToggleDesktopIconsCommand = 0x7402;

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

    public static bool AreDesktopIconsVisible()
    {
        IntPtr defView = GetDefView();
        if (defView == IntPtr.Zero)
            return true;

        IntPtr listView = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        return listView != IntPtr.Zero && IsWindowVisible(listView);
    }

    /// <summary>等同在桌面右鍵 →「檢視」→「顯示桌面圖示」。</summary>
    public static void SetDesktopIconsVisible(bool visible)
    {
        if (AreDesktopIconsVisible() == visible)
            return;

        IntPtr defView = GetDefView();
        if (defView != IntPtr.Zero)
            SendMessage(defView, WM_COMMAND, new IntPtr(ToggleDesktopIconsCommand), IntPtr.Zero);
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
