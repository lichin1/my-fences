using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using MyFences.Models;
using MyFences.Views;
using static MyFences.Services.NativeMethods;

namespace MyFences.Services;

/// <summary>
/// 把「真正的」桌面圖示收進分區（Fences 的核心功能，但不注入檔案總管）：
/// <list type="bullet">
/// <item>圖示屬於哪個分區由設定檔記錄；MyFences 負責把它們排在分區格子裡，分區移動時跟著走。</item>
/// <item>使用者把圖示拖進分區 = 加入；拖出分區 = 移出（每 0.5 秒檢查一次圖示位置）。</item>
/// <item>不在任何分區的圖示如果被分區蓋住，就移到旁邊的空位。</item>
/// <item>收合、其他分頁、其他桌面頁面、雙擊隱藏時，把圖示「停」到螢幕外（實驗功能）。</item>
/// </list>
/// 檔案本身完全不會被修改。
/// </summary>
internal sealed class DesktopIconManager : IDisposable
{
    private const int MoveThreshold = 3;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    private readonly App _app;
    private readonly Func<IReadOnlyList<FenceWindow>> _windows;
    private readonly DesktopIconView _view = new();
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };

    /// <summary>上次看到（或我們移過去）的圖示位置。</summary>
    private Dictionary<string, PixelPoint> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>目前停在螢幕外的圖示。</summary>
    private readonly HashSet<string> _parked = new(StringComparer.OrdinalIgnoreCase);

    private bool _parkingUnsupported;
    private bool _started;

    public DesktopIconManager(App app, Func<IReadOnlyList<FenceWindow>> windows)
    {
        _app = app;
        _windows = windows;
        _pollTimer.Tick += (_, _) => Poll();
        _layoutTimer.Tick += (_, _) =>
        {
            _layoutTimer.Stop();
            LayoutAll();
        };
    }

    public (int Width, int Height) Cell => _view.Spacing;

    public void Start()
    {
        if (!_view.DisableAutoArrange())
        {
            Toast.ShowMessage("請在桌面按右鍵 →「檢視」，取消勾選「自動排列圖示」和「將圖示對齊格線」，分區才能收納圖示。");
        }

        List<DesktopIcon> icons = _view.Refresh();
        _known = icons.ToDictionary(i => i.Id, i => i.Position, StringComparer.OrdinalIgnoreCase);
        PruneDeletedMembers();
        _started = true;
        LayoutAll();
        _pollTimer.Start();
    }

    /// <summary>分區移動、縮放、切分頁、收合等之後呼叫；會稍微延遲合併成一次排列。</summary>
    public void RequestLayout()
    {
        if (!_started)
            return;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    /// <summary>結束時：停在螢幕外的圖示全部放回桌面空位，並還原「自動排列」設定。</summary>
    public void Shutdown(bool restoreAutoArrange)
    {
        _pollTimer.Stop();
        _layoutTimer.Stop();
        if (!_started)
            return;

        RescueParked();
        if (restoreAutoArrange)
            _view.RestoreAutoArrange();
    }

    /// <summary>把停在螢幕外（或任何跑到螢幕外）的圖示找回來，放到桌面空位。</summary>
    public int RescueParked()
    {
        List<DesktopIcon> icons = _view.Refresh();
        if (icons.Count == 0)
            return 0;

        PixelRect screen = VirtualScreen();
        var lost = icons.Where(i => !screen.Contains(i.Position.X + 1, i.Position.Y + 1)).ToList();
        var occupied = icons.Where(i => screen.Contains(i.Position.X + 1, i.Position.Y + 1)).Select(i => i.Position).ToList();
        var reserved = new HashSet<PixelPoint>();
        var moves = new List<(string, PixelPoint)>();
        PixelRect area = PrimaryWorkArea();
        List<PixelRect> fences = VisibleFenceRects();
        foreach (DesktopIcon icon in lost)
        {
            if (IconLayout.FindFreeSpot(area, fences, occupied, Cell.Width, Cell.Height, reserved) is { } spot)
            {
                reserved.Add(spot);
                moves.Add((icon.Id, spot));
            }
        }

        ApplyMoves(moves);
        _parked.Clear();
        return moves.Count;
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _layoutTimer.Stop();
        _view.Dispose();
    }

    // ───────────── 偵測使用者拖曳 ─────────────

    private void Poll()
    {
        List<DesktopIcon> icons = _view.Refresh();
        if (icons.Count == 0)
            return; // 檔案總管可能正在重新啟動

        var now = icons.ToDictionary(i => i.Id, i => i.Position, StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (DesktopIcon icon in icons)
        {
            if (!_known.TryGetValue(icon.Id, out PixelPoint old))
            {
                changed |= HandleNewIcon(icon);
                continue;
            }

            if (Math.Abs(old.X - icon.Position.X) <= MoveThreshold && Math.Abs(old.Y - icon.Position.Y) <= MoveThreshold)
                continue;

            if (_parked.Contains(icon.Id))
            {
                // 停在螢幕外的圖示被 Windows 搬回來了：重新停好（不是使用者移動的）
                changed = true;
                continue;
            }

            changed |= HandleUserMove(icon.Id, icon.Position);
        }

        _known = now;
        foreach (string gone in _parked.Where(id => !now.ContainsKey(id)).ToList())
            _parked.Remove(gone);

        if (changed)
            LayoutAll();
    }

    /// <summary>使用者把圖示拖到某個位置：落在分區裡 = 加入（插在那一格），落在分區外 = 移出。</summary>
    private bool HandleUserMove(string id, PixelPoint position)
    {
        FenceWindow? target = FenceAt(position);
        FenceWindow? source = FenceOf(id);

        if (target == null)
        {
            if (source == null)
                return false;

            RemoveMember(id);
            _app.RequestSave();
            return true;
        }

        InsertMember(target, id, position);
        _app.RequestSave();
        return true;
    }

    /// <summary>桌面上出現新圖示（新檔案、從別的資料夾拖過來的檔案）。</summary>
    private bool HandleNewIcon(DesktopIcon icon)
    {
        if (FenceOf(icon.Id) != null)
            return true; // 已經是某個分區的（例如改名後重新出現），排好位置就好

        if (_app.Config.AutoSortNewItems && _app.ApplyRules(new[] { icon.Id }, layout: false) > 0)
            return true;

        if (FenceAt(icon.Position) is { } fence)
        {
            // 游標就在這個分區上 = 使用者剛把檔案拖進來；否則是程式存檔時檔案總管自己放在這裡的，移到旁邊
            if (GetCursorPos(out POINT cursor) && fence.GetPixelBounds() is { } bounds && bounds.Contains(cursor.x, cursor.y))
            {
                InsertMember(fence, icon.Id, icon.Position);
                _app.RequestSave();
            }

            return true;
        }

        return false;
    }

    // ───────────── 分區成員 ─────────────

    private IEnumerable<FenceWindow> IconFences() => _windows().Where(w => w.Config.Kind == FenceKind.Standard);

    /// <summary>位置（圖示左上角）落在哪個「看得到圖示」的分區裡。</summary>
    private FenceWindow? FenceAt(PixelPoint position) =>
        IconFences().FirstOrDefault(w => w.IconsVisible && w.GetPixelBounds() is { } r &&
                                         IconLayout.CenterInside(r, position, Cell.Width, Cell.Height));

    private FenceWindow? FenceOf(string id) =>
        IconFences().FirstOrDefault(w => w.Config.AllItemLists().Any(items => items.Any(p => App.PathEquals(p, id))));

    private void RemoveMember(string id)
    {
        foreach (FenceWindow window in IconFences())
        {
            foreach (List<string> items in window.Config.AllItemLists())
                items.RemoveAll(p => App.PathEquals(p, id));
        }
    }

    /// <summary>放進分區目前的分頁，位置依放下的格子決定。</summary>
    private void InsertMember(FenceWindow fence, string id, PixelPoint position)
    {
        RemoveMember(id);
        List<string> items = fence.Config.CurrentItems;
        List<string> present = items.Where(_known.ContainsKey).ToList();

        int index = items.Count;
        if (fence.GetBodyPixelRect() is { } body)
        {
            int slot = IconLayout.IndexAt(body, position, present.Count, Cell.Width, Cell.Height);
            if (slot < present.Count)
                index = items.FindIndex(p => App.PathEquals(p, present[slot]));
        }

        items.Insert(Math.Clamp(index, 0, items.Count), id);
        fence.RefreshEmptyHint();
    }

    /// <summary>已經刪掉的檔案從分區移除（::{GUID} 這類系統圖示不算）。</summary>
    private void PruneDeletedMembers()
    {
        foreach (FenceWindow window in IconFences())
        {
            foreach (List<string> items in window.Config.AllItemLists())
            {
                items.RemoveAll(p => !_known.ContainsKey(p) && !p.StartsWith("::", StringComparison.Ordinal) &&
                                     !System.IO.File.Exists(p) && !System.IO.Directory.Exists(p));
            }
        }
    }

    // ───────────── 排列 ─────────────

    /// <summary>依目前的分區狀態，把所有圖示放到該在的位置。</summary>
    private void LayoutAll()
    {
        if (!_started || _known.Count == 0)
            return;

        var moves = new List<(string Id, PixelPoint Position)>();
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toPark = new List<string>();

        foreach (FenceWindow window in IconFences())
        {
            List<string> current = window.Config.CurrentItems.Where(_known.ContainsKey).ToList();
            foreach (string id in window.Config.AllItemLists().SelectMany(l => l))
                members.Add(id);

            // 其他分頁的圖示
            toPark.AddRange(window.Config.AllItemLists()
                .Where(l => !ReferenceEquals(l, window.Config.CurrentItems))
                .SelectMany(l => l)
                .Where(_known.ContainsKey));

            if (!window.IconsVisible || window.GetBodyPixelRect() is not { } body)
            {
                toPark.AddRange(current);
                continue;
            }

            // 放不下就把分區拉長
            int required = IconLayout.RequiredHeight(body, current.Count, Cell.Width, Cell.Height);
            if (required > body.Height && window.EnsureBodyHeight(required) && window.GetBodyPixelRect() is { } grown)
                body = grown;

            List<PixelPoint> grid = IconLayout.Grid(body, current.Count, Cell.Width, Cell.Height);
            for (int i = 0; i < current.Count; i++)
                Plan(moves, current[i], grid[i]);
        }

        // 收合 / 其他分頁 / 其他頁面：停到螢幕外
        PixelPoint parkSpot = ParkSpot();
        foreach (string id in toPark.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_parkingUnsupported)
                continue;
            Plan(moves, id, parkSpot);
            _parked.Add(id);
        }

        // 不屬於分區、卻被分區蓋住的圖示：移到旁邊的空位
        List<PixelRect> fenceRects = VisibleFenceRects();
        var loose = _known.Where(kv => !members.Contains(kv.Key)).ToList();
        foreach (var (id, _) in loose)
            _parked.Remove(id);

        var occupied = loose.Select(kv => kv.Value).ToList();
        occupied.AddRange(moves.Select(m => m.Position));
        var reserved = new HashSet<PixelPoint>();
        PixelRect area = PrimaryWorkArea();
        foreach (var (id, position) in loose)
        {
            if (!fenceRects.Any(r => IconLayout.CenterInside(r, position, Cell.Width, Cell.Height)))
                continue;

            if (IconLayout.FindFreeSpot(area, fenceRects, occupied, Cell.Width, Cell.Height, reserved) is { } spot)
            {
                reserved.Add(spot);
                moves.Add((id, spot));
            }
        }

        // 從停車位回來的圖示
        foreach (string id in members.Where(id => !toPark.Contains(id, StringComparer.OrdinalIgnoreCase)))
            _parked.Remove(id);

        ApplyMoves(moves);
        CheckParking();
    }

    private void Plan(List<(string, PixelPoint)> moves, string id, PixelPoint target)
    {
        if (_known.TryGetValue(id, out PixelPoint current) &&
            Math.Abs(current.X - target.X) <= MoveThreshold && Math.Abs(current.Y - target.Y) <= MoveThreshold)
        {
            return;
        }

        moves.Add((id, target));
    }

    /// <summary>移動圖示，然後重新讀一次實際位置（Windows 可能微調），避免被當成使用者的拖曳。</summary>
    private void ApplyMoves(List<(string Id, PixelPoint Position)> moves)
    {
        if (moves.Count == 0)
            return;

        _view.Move(moves);
        List<DesktopIcon> icons = _view.Refresh();
        if (icons.Count > 0)
            _known = icons.ToDictionary(i => i.Id, i => i.Position, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>停到螢幕外的圖示如果還在畫面上，代表這台電腦不支援，之後就不再停車。</summary>
    private void CheckParking()
    {
        if (_parkingUnsupported || _parked.Count == 0)
            return;

        PixelRect screen = VirtualScreen();
        bool stillVisible = _parked.Any(id => _known.TryGetValue(id, out PixelPoint p) && screen.Contains(p.X + 1, p.Y + 1));
        if (!stillVisible)
            return;

        _parkingUnsupported = true;
        _parked.Clear();
        Toast.ShowMessage("這台電腦沒辦法把圖示暫時移到螢幕外，收合、分頁和多頁時，圖示會留在桌面上。");
        RescueParked();
        RequestLayout();
    }

    private static PixelRect VirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static PixelPoint ParkSpot()
    {
        PixelRect screen = VirtualScreen();
        return new PixelPoint(screen.Right + 400, screen.Y + 40);
    }

    private static PixelRect PrimaryWorkArea()
    {
        System.Drawing.Rectangle wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
        return new PixelRect(wa.X, wa.Y, wa.Width, wa.Height);
    }

    private List<PixelRect> VisibleFenceRects() =>
        _windows().Where(w => w.IsVisible).Select(w => w.GetPixelBounds()).OfType<PixelRect>().ToList();
}
