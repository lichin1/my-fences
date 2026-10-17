using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>
/// 桌面上有哪些東西、叫什麼名字、怎麼開啟。行為跟著檔案總管的設定走：
/// 「顯示隱藏的項目」「隱藏受保護的作業系統檔案」「隱藏副檔名」「桌面圖示設定」。
/// 系統圖示（本機、資源回收筒…）用 ::{CLSID} 當作 Id。
/// </summary>
internal static class ShellItems
{
    public const string ThisPC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    public const string UserFiles = "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}";
    public const string Network = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    public const string ControlPanel = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";

    private static readonly string[] SystemIcons = { ThisPC, UserFiles, Network, RecycleBin, ControlPanel };

    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_PIDL = 0x000000008;

    public static bool IsSystemItem(string id) => id.StartsWith("::", StringComparison.Ordinal);

    /// <summary>桌面上所有項目：先系統圖示（依「桌面圖示設定」），再是個人桌面和公用桌面的檔案。</summary>
    public static List<string> EnumerateDesktop()
    {
        var result = SystemIcons.Where(IsSystemIconShown).ToList();
        bool showHidden = ShowHiddenFiles;
        bool showSuperHidden = ShowProtectedFiles;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in DesktopHelper.GetDesktopDirectories())
        {
            try
            {
                foreach (FileSystemInfo info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    FileAttributes a = info.Attributes;
                    bool hidden = (a & FileAttributes.Hidden) != 0;
                    bool system = (a & FileAttributes.System) != 0;
                    if (hidden && system && !showSuperHidden)
                        continue; // 例如 desktop.ini
                    if (hidden && !showHidden)
                        continue;
                    if (seen.Add(info.FullName))
                        result.Add(info.FullName);
                }
            }
            catch (Exception)
            {
                // 沒權限之類：略過這個資料夾
            }
        }

        return result;
    }

    /// <summary>檔案總管「顯示隱藏的項目」。</summary>
    public static bool ShowHiddenFiles => ReadAdvanced("Hidden", 2) == 1;

    /// <summary>檔案總管「隱藏受保護的作業系統檔案」取消勾選時為 true。</summary>
    public static bool ShowProtectedFiles => ReadAdvanced("ShowSuperHidden", 0) == 1;

    /// <summary>系統圖示有沒有在「桌面圖示設定」裡勾選（Windows 預設只有資源回收筒）。</summary>
    public static bool IsSystemIconShown(string id)
    {
        string clsid = id.Substring(2);
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
        object? value = key?.GetValue(clsid);
        return value is int hidden ? hidden == 0 : id == RecycleBin;
    }

    /// <summary>顯示名稱（跟檔案總管一樣：會依「隱藏副檔名」設定拿掉副檔名，系統圖示顯示「本機」這類名稱）。</summary>
    public static string GetDisplayName(string id)
    {
        var info = new SHFILEINFO();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (IsSystemItem(id))
        {
            IntPtr pidl = ParsePidl(id);
            if (pidl == IntPtr.Zero)
                return id;
            try
            {
                if (SHGetFileInfoPidl(pidl, 0, ref info, size, SHGFI_DISPLAYNAME | SHGFI_PIDL) != IntPtr.Zero && !string.IsNullOrEmpty(info.szDisplayName))
                    return info.szDisplayName;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }

            return id;
        }

        if (SHGetFileInfo(id, 0, ref info, size, SHGFI_DISPLAYNAME) != IntPtr.Zero && !string.IsNullOrEmpty(info.szDisplayName))
            return info.szDisplayName;

        string name = Path.GetFileName(id.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? id : name;
    }

    public static bool Exists(string id) => IsSystemItem(id) || File.Exists(id) || Directory.Exists(id);

    public static void Open(string id)
    {
        if (IsSystemItem(id))
            Process.Start("explorer.exe", "shell:" + id);
        else
            Process.Start(new ProcessStartInfo(id) { UseShellExecute = true });
    }

    /// <summary>改名：輸入的是顯示名稱，副檔名被隱藏時會自動補回原本的副檔名。</summary>
    public static string Rename(string path, string newDisplayName)
    {
        newDisplayName = newDisplayName.Trim();
        if (IsSystemItem(path) || newDisplayName.Length == 0)
            return path;

        string dir = Path.GetDirectoryName(path)!;
        bool isDir = Directory.Exists(path);
        string newName = newDisplayName;
        if (!isDir)
        {
            string ext = Path.GetExtension(path);
            // 顯示名稱沒有副檔名（副檔名被隱藏）而且使用者也沒打副檔名 → 補回原本的
            if (ext.Length > 0 && !GetDisplayName(path).EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
                !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                newName += ext;
            }
        }

        string target = Path.Combine(dir, newName);
        if (string.Equals(target, path, StringComparison.Ordinal))
            return path;

        if (isDir)
            Directory.Move(path, target);
        else
            File.Move(path, target);
        return target;
    }

    /// <summary>把 ::{GUID} 或完整路徑轉成 PIDL（呼叫端要 FreeCoTaskMem）。</summary>
    public static IntPtr ParsePidl(string id)
    {
        return SHParseDisplayName(id, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 ? pidl : IntPtr.Zero;
    }

    private static int ReadAdvanced(string name, int fallback)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue(name) is int value ? value : fallback;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
