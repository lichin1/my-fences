using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

internal static class IconHelper
{
    /// <summary>取得檔案 / 資料夾 / 捷徑的系統圖示（優先 48px）。必須在 STA 執行緒呼叫。</summary>
    public static ImageSource? GetIcon(string path)
    {
        if (ShellItems.IsSystemItem(path))
            return GetSystemItemIcon(path);

        return TryGetExtraLargeIcon(path) ?? TryGetLargeIcon(path, 0, 0) ??
               TryGetLargeIcon(path, FILE_ATTRIBUTE_NORMAL, SHGFI_USEFILEATTRIBUTES);
    }

    private const uint SHGFI_PIDL = 0x000000008;

    /// <summary>本機、資源回收筒這類沒有檔案路徑的系統圖示：用 PIDL 取得。</summary>
    private static ImageSource? GetSystemItemIcon(string id)
    {
        IntPtr pidl = ShellItems.ParsePidl(id);
        if (pidl == IntPtr.Zero)
            return null;

        try
        {
            var info = new SHFILEINFO();
            uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
            if (ShellItems.SHGetFileInfoPidl(pidl, 0, ref info, size, SHGFI_PIDL | SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;
            return FromSystemImageList(info.iIcon);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static ImageSource? TryGetExtraLargeIcon(string path)
    {
        var info = new SHFILEINFO();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, 0, ref info, size, SHGFI_SYSICONINDEX) == IntPtr.Zero)
            return null;
        return FromSystemImageList(info.iIcon);
    }

    private static ImageSource? FromSystemImageList(int iconIndex)
    {
        Guid iid = IID_IImageList;
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IntPtr imageList) != 0 || imageList == IntPtr.Zero)
            return null;

        try
        {
            // IImageList 指標可以直接當 HIMAGELIST 使用
            IntPtr hIcon = ImageList_GetIcon(imageList, iconIndex, ILD_TRANSPARENT);
            return hIcon == IntPtr.Zero ? null : ToImageSource(hIcon);
        }
        finally
        {
            Marshal.Release(imageList);
        }
    }

    private static ImageSource? TryGetLargeIcon(string path, uint attributes, uint extraFlags)
    {
        var info = new SHFILEINFO();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
        if (SHGetFileInfo(path, attributes, ref info, size, SHGFI_ICON | SHGFI_LARGEICON | extraFlags) == IntPtr.Zero ||
            info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        return ToImageSource(info.hIcon);
    }

    private static ImageSource? ToImageSource(IntPtr hIcon)
    {
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
