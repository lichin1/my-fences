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

    private static long DistanceSquared(PixelRect r, int x, int y)
    {
        long dx = x < r.X ? r.X - x : x >= r.Right ? x - r.Right + 1 : 0;
        long dy = y < r.Y ? r.Y - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }
}
