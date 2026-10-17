using System;
using System.Collections.Generic;
using System.Linq;

namespace MyFences.Models;

/// <summary>螢幕像素座標的矩形。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>一台螢幕：裝置名稱（例如 \\.\DISPLAY1）與工作區（扣掉工作列）。</summary>
public readonly record struct ScreenInfo(string DeviceName, PixelRect WorkArea, bool IsPrimary);

/// <summary>多螢幕 / 解析度變更時，分區位置的換算。</summary>
public static class LayoutMath
{
    /// <summary>螢幕組合的代號，同一組螢幕設定會得到同一個字串。</summary>
    public static string ProfileKey(IEnumerable<ScreenInfo> screens) =>
        string.Join("|", screens
            .OrderBy(s => s.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(s => $"{s.DeviceName}:{s.WorkArea.X},{s.WorkArea.Y},{s.WorkArea.Width}x{s.WorkArea.Height}"));

    /// <summary>
    /// 把分區從舊的螢幕組合換算到新的：找出分區原本在哪台螢幕，
    /// 換到新組合裡的同一台（找不到就換到主螢幕），依比例縮放位置與大小，並確保在螢幕內。
    /// </summary>
    public static PixelRect Remap(PixelRect fence, IReadOnlyList<ScreenInfo> oldScreens, IReadOnlyList<ScreenInfo> newScreens)
    {
        if (oldScreens.Count == 0 || newScreens.Count == 0)
            return fence;

        ScreenInfo from = FindScreen(fence, oldScreens);
        ScreenInfo to = newScreens.FirstOrDefault(s => string.Equals(s.DeviceName, from.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (to.DeviceName == null)
            to = newScreens.FirstOrDefault(s => s.IsPrimary) is { DeviceName: not null } primary ? primary : newScreens[0];

        PixelRect a = from.WorkArea;
        PixelRect b = to.WorkArea;
        if (a == b)
            return Clamp(fence, b);

        double sx = (double)b.Width / Math.Max(1, a.Width);
        double sy = (double)b.Height / Math.Max(1, a.Height);
        var scaled = new PixelRect(
            b.X + (int)Math.Round((fence.X - a.X) * sx),
            b.Y + (int)Math.Round((fence.Y - a.Y) * sy),
            Math.Max(1, (int)Math.Round(fence.Width * sx)),
            Math.Max(1, (int)Math.Round(fence.Height * sy)));
        return Clamp(scaled, b);
    }

    /// <summary>分區中心所在的螢幕；不在任何螢幕上就找最近的。</summary>
    public static ScreenInfo FindScreen(PixelRect fence, IReadOnlyList<ScreenInfo> screens)
    {
        foreach (ScreenInfo screen in screens)
        {
            if (screen.WorkArea.Contains(fence.CenterX, fence.CenterY))
                return screen;
        }

        return screens.OrderBy(s => DistanceSquared(s.WorkArea, fence.CenterX, fence.CenterY)).First();
    }

    /// <summary>縮小到放得進工作區，再把位置推回工作區內。</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect area)
    {
        int width = Math.Min(rect.Width, area.Width);
        int height = Math.Min(rect.Height, area.Height);
        int x = Math.Clamp(rect.X, area.X, area.Right - width);
        int y = Math.Clamp(rect.Y, area.Y, area.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>在 anchor 旁邊放一個一樣大的新分區：優先右邊，放不下就左邊，再不行就下面，最後確保在螢幕內。</summary>
    public static PixelRect PlaceBeside(PixelRect anchor, PixelRect area, int gap = 8)
    {
        var right = new PixelRect(anchor.Right + gap, anchor.Y, anchor.Width, anchor.Height);
        if (right.Right <= area.Right)
            return Clamp(right, area);

        var left = new PixelRect(anchor.X - gap - anchor.Width, anchor.Y, anchor.Width, anchor.Height);
        if (left.X >= area.X)
            return Clamp(left, area);

        return Clamp(new PixelRect(anchor.X, anchor.Bottom + gap, anchor.Width, anchor.Height), area);
    }

    /// <summary>
    /// 拖曳 / 縮放分區時的吸附：靠近螢幕邊緣、其他分區的邊（對齊或並排、留 gap 間距）就吸過去。
    /// edges = All 是移動（保持大小）；否則是縮放，只吸附正在拖的那幾個邊。
    /// 回傳吸附後的矩形，以及要畫對齊線的位置（沒吸附就是 null）。
    /// </summary>
    public static SnapResult Snap(
        PixelRect rect, IReadOnlyList<PixelRect> others, PixelRect area, SnapEdges edges,
        int threshold = 10, int gap = 8)
    {
        bool move = edges == SnapEdges.All;
        int left = rect.X, top = rect.Y, right = rect.Right, bottom = rect.Bottom;

        // 只考慮另一個軸向上靠得夠近的分區，免得跟畫面另一頭的分區對齊
        const int nearby = 200;
        var nearX = others.Where(o => o.Y - nearby < bottom && o.Bottom + nearby > top).ToList();
        var nearY = others.Where(o => o.X - nearby < right && o.Right + nearby > left).ToList();

        var leftTargets = new List<int> { area.X };
        var rightTargets = new List<int> { area.Right };
        var topTargets = new List<int> { area.Y };
        var bottomTargets = new List<int> { area.Bottom };
        foreach (PixelRect o in nearX)
        {
            leftTargets.AddRange(new[] { o.X, o.Right + gap });
            rightTargets.AddRange(new[] { o.Right, o.X - gap });
        }

        foreach (PixelRect o in nearY)
        {
            topTargets.AddRange(new[] { o.Y, o.Bottom + gap });
            bottomTargets.AddRange(new[] { o.Bottom, o.Y - gap });
        }

        (int? dx, int? guideX) = Best(
            (edges & SnapEdges.Left) != 0 ? (left, leftTargets) : null,
            (edges & SnapEdges.Right) != 0 ? (right, rightTargets) : null,
            threshold);
        (int? dy, int? guideY) = Best(
            (edges & SnapEdges.Top) != 0 ? (top, topTargets) : null,
            (edges & SnapEdges.Bottom) != 0 ? (bottom, bottomTargets) : null,
            threshold);

        if (move)
        {
            return new SnapResult(
                new PixelRect(rect.X + (dx ?? 0), rect.Y + (dy ?? 0), rect.Width, rect.Height), guideX, guideY);
        }

        // 縮放：只動正在拖的邊
        if (dx is { } ddx)
        {
            if ((edges & SnapEdges.Left) != 0) left += ddx;
            else right += ddx;
        }

        if (dy is { } ddy)
        {
            if ((edges & SnapEdges.Top) != 0) top += ddy;
            else bottom += ddy;
        }

        return new SnapResult(new PixelRect(left, top, right - left, bottom - top), guideX, guideY);
    }

    /// <summary>在兩組（邊, 目標）裡找最近的吸附點；回傳位移量與對齊線位置。</summary>
    private static (int? Delta, int? Guide) Best((int Edge, List<int> Targets)? a, (int Edge, List<int> Targets)? b, int threshold)
    {
        int? bestDelta = null;
        int? guide = null;
        foreach (var (edge, targets) in new[] { a, b }.Where(x => x != null).Select(x => x!.Value))
        {
            foreach (int target in targets)
            {
                int delta = target - edge;
                if (Math.Abs(delta) <= threshold && (bestDelta == null || Math.Abs(delta) < Math.Abs(bestDelta.Value)))
                {
                    bestDelta = delta;
                    guide = target;
                }
            }
        }

        return (bestDelta, guide);
    }

    private static long DistanceSquared(PixelRect r, int x, int y)
    {
        long dx = x < r.X ? r.X - x : x >= r.Right ? x - r.Right + 1 : 0;
        long dy = y < r.Y ? r.Y - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }
}

[Flags]
public enum SnapEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    All = Left | Top | Right | Bottom,
}

public readonly record struct SnapResult(PixelRect Rect, int? GuideX, int? GuideY);
