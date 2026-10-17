using System;
using System.Collections.Generic;
using System.Linq;

namespace MyFences.Models;

public readonly record struct PixelPoint(int X, int Y);

/// <summary>
/// 真正的桌面圖示在分區裡的排列方式（都用螢幕像素）。
/// 圖示的位置是指圖示格子的左上角；cellWidth / cellHeight 是 Windows 桌面的圖示間距。
/// </summary>
public static class IconLayout
{
    public const int Padding = 4;

    /// <summary>body 寬度放得下幾欄（至少 1 欄）。</summary>
    public static int Columns(PixelRect body, int cellWidth) =>
        Math.Max(1, (body.Width - 2 * Padding) / Math.Max(1, cellWidth));

    /// <summary>放 count 個圖示需要的內容高度。</summary>
    public static int RequiredHeight(PixelRect body, int count, int cellWidth, int cellHeight)
    {
        if (count <= 0)
            return 0;
        int rows = (count + Columns(body, cellWidth) - 1) / Columns(body, cellWidth);
        return rows * cellHeight + 2 * Padding;
    }

    /// <summary>依序排成格子（由左到右、由上到下），整組水平置中。</summary>
    public static List<PixelPoint> Grid(PixelRect body, int count, int cellWidth, int cellHeight)
    {
        int columns = Columns(body, cellWidth);
        int offsetX = body.X + Math.Max(Padding, (body.Width - columns * cellWidth) / 2);

        var result = new List<PixelPoint>(count);
        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            result.Add(new PixelPoint(offsetX + col * cellWidth, body.Y + Padding + row * cellHeight));
        }

        return result;
    }

    /// <summary>
    /// 圖示被放在 point（圖示左上角）時，應該插在第幾個位置（0..count）。用圖示中心判斷落在哪一格。
    /// </summary>
    public static int IndexAt(PixelRect body, PixelPoint point, int count, int cellWidth, int cellHeight)
    {
        int columns = Columns(body, cellWidth);
        int offsetX = body.X + Math.Max(Padding, (body.Width - columns * cellWidth) / 2);
        int cx = point.X + cellWidth / 2 - offsetX;
        int cy = point.Y + cellHeight / 2 - (body.Y + Padding);
        int col = Math.Clamp(cx / Math.Max(1, cellWidth), 0, columns - 1);
        int row = Math.Max(0, cy / Math.Max(1, cellHeight));
        return Math.Clamp(row * columns + col, 0, count);
    }

    /// <summary>圖示（左上角在 point）的中心是否落在 rect 裡。</summary>
    public static bool CenterInside(PixelRect rect, PixelPoint point, int cellWidth, int cellHeight) =>
        rect.Contains(point.X + cellWidth / 2, point.Y + cellHeight / 2);

    /// <summary>
    /// 找一個空的桌面格子：不在任何分區裡、也沒有其他圖示。照 Windows 的習慣從左上角往下、再往右找。
    /// 找不到就回傳 null。
    /// </summary>
    public static PixelPoint? FindFreeSpot(
        PixelRect workArea, IReadOnlyList<PixelRect> fences, IReadOnlyCollection<PixelPoint> occupied,
        int cellWidth, int cellHeight, ISet<PixelPoint>? reserved = null)
    {
        int columns = Math.Max(1, workArea.Width / Math.Max(1, cellWidth));
        int rows = Math.Max(1, workArea.Height / Math.Max(1, cellHeight));
        for (int col = 0; col < columns; col++)
        {
            for (int row = 0; row < rows; row++)
            {
                var cell = new PixelRect(workArea.X + col * cellWidth, workArea.Y + row * cellHeight, cellWidth, cellHeight);
                if (fences.Any(f => Intersects(f, cell)))
                    continue;

                var spot = new PixelPoint(cell.X, cell.Y);
                if (reserved?.Contains(spot) == true)
                    continue;
                if (occupied.Any(p => cell.Contains(p.X + cellWidth / 2, p.Y + cellHeight / 2)))
                    continue;

                return spot;
            }
        }

        return null;
    }

    private static bool Intersects(PixelRect a, PixelRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}
