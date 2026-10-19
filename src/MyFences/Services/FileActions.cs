using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace MyFences.Services;

/// <summary>
/// 圖示上的動作，全部交給 Windows 自己做：刪除進資源回收筒、複製 / 移動的進度和衝突視窗、
/// 剪貼簿格式、「內容」「開啟方式」視窗，都跟在檔案總管裡操作一樣。
/// </summary>
internal static class FileActions
{
    private const uint FO_MOVE = 1;
    private const uint FO_COPY = 2;
    private const uint FO_DELETE = 3;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SSF_NOCONFIRMRECYCLE = 0x00008000;

    public static void Open(string id)
    {
        try
        {
            ShellItems.Open(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"無法開啟「{ShellItems.GetDisplayName(id)}」：\n{ex.Message}", "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>在檔案總管中顯示（選取這個檔案）。</summary>
    public static void ShowInExplorer(string path)
    {
        if (ShellItems.IsSystemItem(path))
            return;
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            Process.Start("explorer.exe", $"\"{dir}\"");
    }

    public static void ShowProperties(string id) => InvokeVerb(id, "properties");

    /// <summary>「開啟方式」：Windows 的「你要如何開啟這個檔案？」。</summary>
    public static void OpenWith(string path) => InvokeVerb(path, "openas");

    public static bool CanOpenWith(string id) => !ShellItems.IsSystemItem(id) && File.Exists(id);

    /// <summary>只有真的檔案 / 資料夾能複製、剪下、刪除、改名（本機、資源回收筒不行）。</summary>
    public static List<string> RealFiles(IEnumerable<string> ids) =>
        ids.Where(id => !ShellItems.IsSystemItem(id) && (File.Exists(id) || Directory.Exists(id))).ToList();

    // ───────────── 剪貼簿 ─────────────

    /// <summary>跟檔案總管一樣放進剪貼簿（檔案清單 + 複製 / 剪下標記），貼到檔案總管也可以。</summary>
    public static void CopyToClipboard(IEnumerable<string> ids, bool cut)
    {
        List<string> files = RealFiles(ids);
        if (files.Count == 0)
            return;

        var list = new StringCollection();
        list.AddRange(files.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (COMException)
        {
            // 剪貼簿被其他程式占用，跟檔案總管一樣就不做了
        }
    }

    public static bool ClipboardHasFiles()
    {
        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>把剪貼簿的檔案貼到資料夾。回傳新出現的項目（失敗或取消時是空的）。</summary>
    public static List<string> PasteInto(string targetDir)
    {
        string[] files;
        bool move;
        try
        {
            if (!Clipboard.ContainsFileDropList())
                return new List<string>();
            files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            int effect = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4
                ? BitConverter.ToInt32(ms.ToArray(), 0)
                : 5;
            move = (effect & 2) != 0 && (effect & 1) == 0;
        }
        catch (COMException)
        {
            return new List<string>();
        }

        List<string> created = move ? Move(files, targetDir) : Copy(files, targetDir);
        if (move && created.Count > 0)
        {
            try
            {
                Clipboard.Clear(); // 剪下的東西貼過一次就沒了，跟檔案總管一樣
            }
            catch (COMException)
            {
            }
        }

        return created;
    }

    // ───────────── 檔案操作 ─────────────

    public static List<string> Copy(IReadOnlyCollection<string> files, string targetDir) => Transfer(FO_COPY, files, targetDir);

    public static List<string> Move(IReadOnlyCollection<string> files, string targetDir) => Transfer(FO_MOVE, files, targetDir);

    /// <summary>刪除：預設進資源回收筒（是否要確認跟著資源回收筒的設定），permanently = Shift+Delete。回傳是否有刪掉東西。</summary>
    public static bool Delete(IEnumerable<string> ids, bool permanently = false)
    {
        List<string> files = RealFiles(ids);
        if (files.Count == 0)
            return false;

        ushort flags = permanently ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING);
        if (!permanently && !ConfirmRecycle())
            flags |= FOF_NOCONFIRMATION;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = ActiveHwnd(),
            wFunc = FO_DELETE,
            pFrom = DoubleNullList(files),
            fFlags = flags,
        };
        SHFileOperation(ref op);
        return files.Any(f => !File.Exists(f) && !Directory.Exists(f));
    }

    public static void EmptyRecycleBin()
    {
        // 0 = 照 Windows 平常的方式：先問、有進度、有音效
        SHEmptyRecycleBin(ActiveHwnd(), null, 0);
    }

    /// <summary>在資料夾裡建立「新增資料夾」（名稱重複就加 (2)、(3)…），回傳路徑。</summary>
    public static string? CreateNewFolder(string dir)
    {
        const string baseName = "新增資料夾";
        string path = Path.Combine(dir, baseName);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName} ({i})");

        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch (Exception ex)
        {
            MessageBox.Show("無法建立資料夾：" + ex.Message, "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private static List<string> Transfer(uint func, IReadOnlyCollection<string> files, string targetDir)
    {
        List<string> sources = files
            .Where(f => !ShellItems.IsSystemItem(f) && (File.Exists(f) || Directory.Exists(f)))
            .Where(f => !(func == FO_MOVE && SameDir(Path.GetDirectoryName(f), targetDir)))
            .Where(f => !IsSameOrInside(targetDir, f)) // 不能把資料夾放進自己裡面
            .ToList();
        if (sources.Count == 0 || !Directory.Exists(targetDir))
            return new List<string>();

        HashSet<string> before = ListDir(targetDir);
        ushort flags = FOF_ALLOWUNDO;
        // 複製到同一個資料夾：跟檔案總管一樣自動取名「xxx - 複製」
        if (sources.Any(f => SameDir(Path.GetDirectoryName(f), targetDir)))
            flags |= FOF_RENAMEONCOLLISION;

        var op = new SHFILEOPSTRUCT
        {
            hwnd = ActiveHwnd(),
            wFunc = func,
            pFrom = DoubleNullList(sources),
            pTo = targetDir + "\0\0",
            fFlags = flags,
        };
        SHFileOperation(ref op);

        HashSet<string> after = ListDir(targetDir);
        after.ExceptWith(before);
        return after.ToList();
    }

    private static HashSet<string> ListDir(string dir)
    {
        try
        {
            return new HashSet<string>(Directory.EnumerateFileSystemEntries(dir), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool SameDir(string? a, string b) =>
        a != null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string folder)
    {
        if (!Directory.Exists(folder))
            return false;
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static string DoubleNullList(IEnumerable<string> paths) => string.Join("\0", paths) + "\0\0";

    /// <summary>資源回收筒的「顯示刪除確認對話方塊」有沒有勾（Windows 10 之後預設沒勾）。</summary>
    private static bool ConfirmRecycle()
    {
        SHGetSettings(out uint state, SSF_NOCONFIRMRECYCLE);
        const uint fNoConfirmRecycle = 1 << 2;
        return (state & fNoConfirmRecycle) == 0;
    }

    private static IntPtr ActiveHwnd()
    {
        Window? window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(window).Handle;
    }

    private static void InvokeVerb(string id, string verb)
    {
        IntPtr pidl = ShellItems.IsSystemItem(id) ? ShellItems.ParsePidl(id) : IntPtr.Zero;
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = ActiveHwnd(),
                lpVerb = verb,
                lpFile = pidl == IntPtr.Zero ? id : null,
                lpIDList = pidl,
                nShow = 1, // SW_SHOWNORMAL
            };
            ShellExecuteEx(ref info);
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void SHGetSettings(out uint lpsfs, uint dwMask);
}
