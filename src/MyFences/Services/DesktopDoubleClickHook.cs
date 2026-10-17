using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>
/// 偵測「雙擊桌面空白處」（Fences 的 Quick-Hide）。用低階滑鼠攔截（WH_MOUSE_LL）觀察點擊，
/// 不會吃掉任何滑鼠事件；雙擊在圖示上（會開啟檔案）時不觸發。
/// </summary>
internal sealed class DesktopDoubleClickHook : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _onDoubleClick;
    private readonly LowLevelMouseProc _proc; // 必須留著參照，否則會被 GC 回收
    private IntPtr _hook;
    private bool _hasLastClick;
    private uint _lastClickTime;
    private POINT _lastClickPoint;

    public DesktopDoubleClickHook(Dispatcher dispatcher, Action onDoubleClick)
    {
        _dispatcher = dispatcher;
        _onDoubleClick = onDoubleClick;
        _proc = HookProc;
    }

    public bool Enabled
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value == Enabled)
                return;

            if (value)
            {
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            }
            else
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }

            _hasLastClick = false;
        }
    }

    public void Dispose() => Enabled = false;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 這裡要很快回傳，否則 Windows 會把攔截拿掉；只做簡單判斷，其餘丟回 UI 執行緒
        if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            OnLeftButtonDown(info.pt, info.time);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnLeftButtonDown(POINT pt, uint time)
    {
        if (!DesktopHelper.IsDesktopAt(pt, out IntPtr listView))
        {
            _hasLastClick = false;
            return;
        }

        bool isDoubleClick = _hasLastClick &&
                             unchecked(time - _lastClickTime) <= GetDoubleClickTime() &&
                             Math.Abs(pt.x - _lastClickPoint.x) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2 &&
                             Math.Abs(pt.y - _lastClickPoint.y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;

        if (!isDoubleClick)
        {
            _hasLastClick = true;
            _lastClickTime = time;
            _lastClickPoint = pt;
            return;
        }

        _hasLastClick = false;
        _dispatcher.InvokeAsync(async () =>
        {
            // 等桌面處理完點擊：點在空白處會取消所有選取，點在圖示上則會選取該圖示
            await Task.Delay(100);
            if (listView == IntPtr.Zero || SendMessage(listView, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero)
                _onDoubleClick();
        });
    }
}
