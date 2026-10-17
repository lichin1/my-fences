using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MyFences.Models;
using static MyFences.Services.NativeMethods;
using static MyFences.Services.ShellNative;

namespace MyFences.Services;

/// <summary>桌面上的一個圖示：Id 是完整路徑（或 ::{GUID}），Position 是螢幕像素座標（圖示左上角）。</summary>
internal sealed record DesktopIcon(string Id, PixelPoint Position);

/// <summary>
/// 讀取 / 移動「真正的」桌面圖示（檔案總管畫的那些）。用的是 Windows 公開的 IFolderView 介面，
/// 不注入檔案總管。檔案總管重新啟動後，下次呼叫會自動重新連線。
/// </summary>
internal sealed class DesktopIconView : IDisposable
{
    private IFolderView2? _view;
    private IntPtr _listView;
    private uint? _originalFlags;

    /// <summary>最近一次讀到的 PIDL（移動圖示時要用），每次 Refresh 會換新的並釋放舊的。</summary>
    private Dictionary<string, IntPtr> _pidls = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => EnsureConnected();

    /// <summary>桌面圖示的間距（一格的寬高，像素）。</summary>
    public (int Width, int Height) Spacing { get; private set; } = (76, 86);

    /// <summary>讀出所有桌面圖示與位置。失敗（例如檔案總管正在重啟）回傳空清單。</summary>
    public List<DesktopIcon> Refresh()
    {
        var result = new List<DesktopIcon>();
        if (!EnsureConnected())
            return result;

        var pidls = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_view!.ItemCount(SVGIO_ALLVIEW, out int count) != 0)
                throw new COMException("ItemCount failed");

            for (int i = 0; i < count; i++)
            {
                if (_view.Item(i, out IntPtr pidl) != 0 || pidl == IntPtr.Zero)
                    continue;

                if (SHGetNameFromIDList(pidl, SIGDN_DESKTOPABSOLUTEPARSING, out string name) != 0 ||
                    pidls.ContainsKey(name) ||
                    _view.GetItemPosition(pidl, out POINT pt) != 0)
                {
                    Marshal.FreeCoTaskMem(pidl);
                    continue;
                }

                ShellNative.MapWindowPoints(_listView, IntPtr.Zero, ref pt, 1);
                pidls[name] = pidl;
                result.Add(new DesktopIcon(name, new PixelPoint(pt.x, pt.y)));
            }

            if (_view.GetSpacing(out POINT spacing) == 0 && spacing.x > 0 && spacing.y > 0)
                Spacing = (spacing.x, spacing.y);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            foreach (IntPtr p in pidls.Values)
                Marshal.FreeCoTaskMem(p);
            Disconnect();
            return new List<DesktopIcon>();
        }

        FreePidls();
        _pidls = pidls;
        return result;
    }

    /// <summary>把圖示移到指定的螢幕座標（只能移動最近一次 Refresh 讀到的圖示）。</summary>
    public void Move(IReadOnlyCollection<(string Id, PixelPoint Position)> moves)
    {
        if (moves.Count == 0 || !EnsureConnected())
            return;

        var pidls = new List<IntPtr>();
        var points = new List<POINT>();
        foreach ((string id, PixelPoint position) in moves)
        {
            if (!_pidls.TryGetValue(id, out IntPtr pidl))
                continue;

            var pt = new POINT { x = position.X, y = position.Y };
            ShellNative.MapWindowPoints(IntPtr.Zero, _listView, ref pt, 1);
            pidls.Add(pidl);
            points.Add(pt);
        }

        if (pidls.Count == 0)
            return;

        try
        {
            _view!.SelectAndPositionItems((uint)pidls.Count, pidls.ToArray(), points.ToArray(), SVSI_POSITIONITEM);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Disconnect();
        }
    }

    /// <summary>
    /// 關掉桌面的「自動排列圖示」與「將圖示對齊格線」（不關的話，Windows 會把圖示排回去）。
    /// 回傳 false 表示關不掉（需要使用者自己在桌面右鍵 →「檢視」取消勾選）。
    /// </summary>
    public bool DisableAutoArrange()
    {
        if (!EnsureConnected())
            return false;

        try
        {
            if (_view!.GetCurrentFolderFlags(out uint flags) != 0)
                return _view.GetAutoArrange() != 0; // S_FALSE = 沒有自動排列

            _originalFlags ??= flags;
            uint mask = FWF_AUTOARRANGE | FWF_SNAPTOGRID;
            if ((flags & mask) != 0)
                _view.SetCurrentFolderFlags(mask, 0);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            Disconnect();
            return false;
        }
    }

    /// <summary>結束時把「自動排列 / 對齊格線」還原成使用者原本的設定。</summary>
    public void RestoreAutoArrange()
    {
        if (_originalFlags is not { } original || !EnsureConnected())
            return;

        try
        {
            uint mask = FWF_AUTOARRANGE | FWF_SNAPTOGRID;
            _view!.SetCurrentFolderFlags(mask, original & mask);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
        }
    }

    public void Dispose()
    {
        FreePidls();
        Disconnect();
    }

    private bool EnsureConnected()
    {
        if (_view != null && _listView != IntPtr.Zero && IsWindow(_listView))
            return true;

        Disconnect();
        try
        {
            Type? type = Type.GetTypeFromCLSID(ShellGuids.CLSID_ShellWindows);
            if (type == null || Activator.CreateInstance(type) is not IShellWindows windows)
                return false;

            object loc = CSIDL_DESKTOP;
            object root = Type.Missing;
            object dispatch = windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);

            var provider = (IServiceProvider)dispatch;
            Guid sid = ShellGuids.SID_STopLevelBrowser;
            Guid iid = ShellGuids.IID_IShellBrowser;
            if (provider.QueryService(ref sid, ref iid, out IntPtr browserPtr) != 0 || browserPtr == IntPtr.Zero)
                return false;

            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
            Marshal.Release(browserPtr);
            if (browser.QueryActiveShellView(out IntPtr viewPtr) != 0 || viewPtr == IntPtr.Zero)
                return false;

            object shellView = Marshal.GetObjectForIUnknown(viewPtr);
            Marshal.Release(viewPtr);
            _view = shellView as IFolderView2;
            _listView = DesktopHelper.GetDesktopListView();
            return _view != null && _listView != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
        {
            Disconnect();
            return false;
        }
    }

    private void Disconnect()
    {
        if (_view != null && Marshal.IsComObject(_view))
            Marshal.ReleaseComObject(_view);
        _view = null;
        _listView = IntPtr.Zero;
    }

    private void FreePidls()
    {
        foreach (IntPtr pidl in _pidls.Values)
            Marshal.FreeCoTaskMem(pidl);
        _pidls.Clear();
    }
}
