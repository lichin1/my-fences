using System;
using System.Runtime.InteropServices;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

// Windows Shell 的 COM 介面（只宣告用得到的部分，其餘方法用佔位方法保持 vtable 順序）。
// 參考：Raymond Chen〈Manipulating the positions of desktop icons〉的做法：
// IShellWindows.FindWindowSW(SWC_DESKTOP) → IServiceProvider → IShellBrowser → IShellView → IFolderView2

internal static class ShellGuids
{
    public static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    public static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
}

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IShellWindows
{
    void _get_Count();
    void _Item();
    void _NewEnum();
    void _Register();
    void _RegisterPending();
    void _Revoke();
    void _OnNavigate();
    void _OnActivated();

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW(
        [MarshalAs(UnmanagedType.Struct)] ref object pvarLoc,
        [MarshalAs(UnmanagedType.Struct)] ref object pvarLocRoot,
        int swClass,
        out int phwnd,
        int swfwOptions);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void _GetWindow();
    void _ContextSensitiveHelp();
    void _InsertMenusSB();
    void _SetMenuSB();
    void _RemoveMenusSB();
    void _SetStatusTextSB();
    void _EnableModelessSB();
    void _TranslateAcceleratorSB();
    void _BrowseObject();
    void _GetViewStateStream();
    void _GetControlWindow();
    void _SendControlMsg();

    [PreserveSig]
    int QueryActiveShellView(out IntPtr ppshv);
}

/// <summary>IFolderView + IFolderView2（COM 介面不能繼承，所以全部列在一起）。</summary>
[ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView2
{
    // ── IFolderView ──
    [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
    [PreserveSig] int SetCurrentViewMode(uint viewMode);
    [PreserveSig] int GetFolder(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
    [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
    [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetSelectionMarkedItem(out int piItem);
    [PreserveSig] int GetFocusedItem(out int piItem);
    [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
    [PreserveSig] int GetSpacing(out POINT ppt);
    [PreserveSig] int GetDefaultSpacing(out POINT ppt);
    [PreserveSig] int GetAutoArrange();
    [PreserveSig] int SelectItem(int iItem, uint dwFlags);

    [PreserveSig]
    int SelectAndPositionItems(
        uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        [MarshalAs(UnmanagedType.LPArray)] POINT[] apt,
        uint dwFlags);

    // ── IFolderView2 ──
    void _SetGroupBy();
    void _GetGroupBy();
    void _SetViewProperty();
    void _GetViewProperty();
    void _SetTileViewProperties();
    void _SetExtendedTileViewProperties();
    void _SetText();
    [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
    [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
}

internal static class ShellNative
{
    public const int CSIDL_DESKTOP = 0;
    public const int SWC_DESKTOP = 8;
    public const int SWFO_NEEDDISPATCH = 1;
    public const uint SVGIO_ALLVIEW = 0x2;
    public const uint SVSI_POSITIONITEM = 0x80;
    public const uint FWF_AUTOARRANGE = 0x1;
    public const uint FWF_SNAPTOGRID = 0x4;

    /// <summary>
    /// 桌面是命名空間的根，所以桌面項目的子 PIDL 本身就是絕對 PIDL；
    /// 用 DESKTOPABSOLUTEPARSING 取名稱：檔案就是完整路徑，「本機」「資源回收筒」則是 ::{GUID}。
    /// </summary>
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

    [DllImport("user32.dll")]
    public static extern int MapWindowPoints(IntPtr hWndFrom, IntPtr hWndTo, ref POINT lpPoints, int cPoints);
}
