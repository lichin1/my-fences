using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using MyFences.Models;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>
/// 桌面上的滑鼠操作，用低階滑鼠攔截（WH_MOUSE_LL）實作：
/// <list type="bullet">
/// <item>雙擊桌面空白處 → 隱藏 / 顯示分區（不吃掉事件；雙擊在圖示上開檔時不觸發）</item>
/// <item>在桌面按住 Ctrl + 左鍵拖曳 → 畫出新分區（這組滑鼠事件會被吃掉，桌面不會跑出選取框）</item>
/// <item>在桌面空白處按下 / 拖曳 → 取消選取、框選 MyFences 畫的桌面圖示（不吃掉事件）</item>
/// <item>在桌面空白處按右鍵 → 記下位置（從 Windows 選單「新增」「貼上」的東西就放在那裡）</item>
/// </list>
/// </summary>
internal sealed class DesktopMouseHook : IDisposable
{
    private const int VK_CONTROL = 0x11;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    private readonly Dispatcher _dispatcher;
    private readonly Action _onDoubleClick;
    private readonly Action<PixelRect?> _onDrawing; // null = 結束（取消或完成）
    private readonly Action<PixelRect> _onDrawn;
    private readonly Action<PixelPoint> _onPress;
    private readonly Action<PixelRect?> _onBoxSelect; // null = 放開了
    private readonly Action<PixelPoint> _onRightClick;
    private readonly LowLevelMouseProc _proc; // 必須留著參照，否則會被 GC 回收
    private IntPtr _hook;

    private bool _hasLastClick;
    private uint _lastClickTime;
    private POINT _lastClickPoint;

    private bool _drawing;
    private POINT _drawStart;
    private POINT _drawCurrent;
    private bool _drawUpdatePending;

    private bool _pressedOnDesktop;
    private bool _boxing;
    private POINT _boxStart;
    private POINT _boxCurrent;
    private bool _boxUpdatePending;

    public DesktopMouseHook(
        Dispatcher dispatcher, Action onDoubleClick, Action<PixelRect?> onDrawing, Action<PixelRect> onDrawn,
        Action<PixelPoint> onPress, Action<PixelRect?> onBoxSelect, Action<PixelPoint> onRightClick)
    {
        _dispatcher = dispatcher;
        _onDoubleClick = onDoubleClick;
        _onDrawing = onDrawing;
        _onDrawn = onDrawn;
        _onPress = onPress;
        _onBoxSelect = onBoxSelect;
        _onRightClick = onRightClick;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    /// <summary>雙擊桌面隱藏分區（可以關掉；Ctrl+拖曳建立分區一直有效）。</summary>
    public bool QuickHideEnabled { get; set; } = true;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 這裡要很快回傳，否則 Windows 會把攔截拿掉；只做簡單判斷，其餘丟回 UI 執行緒
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (_drawing)
            {
                if (msg == WM_MOUSEMOVE)
                {
                    _drawCurrent = info.pt;
                    QueueDrawUpdate();
                }
                else if (msg == WM_LBUTTONUP)
                {
                    _drawing = false;
                    PixelRect rect = RectFrom(_drawStart, info.pt);
                    _dispatcher.InvokeAsync(() =>
                    {
                        _onDrawing(null);
                        _onDrawn(rect);
                    });
                    return new IntPtr(1);
                }

                return msg == WM_LBUTTONDOWN ? new IntPtr(1) : CallNextHookEx(_hook, nCode, wParam, lParam);
            }

            if (_pressedOnDesktop)
            {
                if (msg == WM_MOUSEMOVE)
                {
                    _boxCurrent = info.pt;
                    if (!_boxing && (Math.Abs(info.pt.x - _boxStart.x) > GetSystemMetrics(SM_CXDRAG) ||
                                     Math.Abs(info.pt.y - _boxStart.y) > GetSystemMetrics(SM_CYDRAG)))
                    {
                        _boxing = true;
                        _hasLastClick = false;
                    }

                    if (_boxing)
                        QueueBoxUpdate();
                }
                else if (msg == WM_LBUTTONUP)
                {
                    _pressedOnDesktop = false;
                    if (_boxing)
                    {
                        _boxing = false;
                        PixelRect rect = RectFrom(_boxStart, info.pt);
                        _dispatcher.InvokeAsync(() =>
                        {
                            _onBoxSelect(rect);
                            _onBoxSelect(null);
                        });
                    }
                }
            }

            if (msg == WM_RBUTTONDOWN && DesktopHelper.IsDesktopAt(info.pt, out _))
            {
                var pt = new PixelPoint(info.pt.x, info.pt.y);
                _dispatcher.InvokeAsync(() => _onRightClick(pt));
            }

            if (msg == WM_LBUTTONDOWN)
            {
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool onDesktop = DesktopHelper.IsDesktopAt(info.pt, out IntPtr listView);
                if (ctrl && onDesktop)
                {
                    _drawing = true;
                    _hasLastClick = false;
                    _drawStart = _drawCurrent = info.pt;
                    return new IntPtr(1); // 吃掉，桌面就不會開始自己的選取框
                }

                if (onDesktop)
                {
                    _pressedOnDesktop = true;
                    _boxing = false;
                    _boxStart = _boxCurrent = info.pt;
                    var pt = new PixelPoint(info.pt.x, info.pt.y);
                    _dispatcher.InvokeAsync(() => _onPress(pt));
                }

                OnLeftButtonDown(info.pt, info.time, onDesktop, listView);
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>滑鼠移動很頻繁，只保留最新位置，UI 執行緒有空時再更新一次。</summary>
    private void QueueDrawUpdate()
    {
        if (_drawUpdatePending)
            return;

        _drawUpdatePending = true;
        _dispatcher.InvokeAsync(() =>
        {
            _drawUpdatePending = false;
            if (_drawing)
                _onDrawing(RectFrom(_drawStart, _drawCurrent));
        }, DispatcherPriority.Render);
    }

    private void QueueBoxUpdate()
    {
        if (_boxUpdatePending)
            return;

        _boxUpdatePending = true;
        _dispatcher.InvokeAsync(() =>
        {
            _boxUpdatePending = false;
            if (_boxing)
                _onBoxSelect(RectFrom(_boxStart, _boxCurrent));
        }, DispatcherPriority.Render);
    }

    private static PixelRect RectFrom(POINT a, POINT b) =>
        new(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

    private void OnLeftButtonDown(POINT pt, uint time, bool onDesktop, IntPtr listView)
    {
        if (!QuickHideEnabled || !onDesktop)
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
            // Windows 的桌面圖示被 MyFences 接管（藏起來）時，點到的一定是空白處
            if (!DesktopHelper.IsListViewVisible(listView) || SendMessage(listView, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero)
                _onDoubleClick();
        });
    }
}
