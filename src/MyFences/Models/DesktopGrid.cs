using System;
using System.Collections.Generic;
using System.Linq;

namespace MyFences.Models;

/// <summary>
/// 分區外桌面圖示的格線計算（螢幕像素）。位置都是圖示格子的左上角，格線從工作區左上角開始。
/// </summary>
public static class DesktopGrid
{
    /// <summary>圖示格子的大小：比圖示寬一點放名稱，高度留兩行字。</summary>
    public static (int Width, int Height) CellSize(double iconSize, double dpiScale) =>
        ((int)Math.Round((iconSize + 36) * dpiScale), (int)Math.Round((iconSize + 46) * dpiScale));

    /// <summary>對齊到最近的格子（不會超出工作區）。</summary>
    public static PixelPoint Snap(PixelPoint point, PixelRect workArea, int cellWidth, int cellHeight)
    {
        int columns = Math.Max(1, workArea.Width / Math.Max(1, cellWidth));
        int rows = Math.Max(1, workArea.Height / Math.Max(1, cellHeight));
        int col = Math.Clamp((int)Math.Round((point.X - workArea.X) / (double)cellWidth), 0, columns - 1);
        int row = Math.Clamp((int)Math.Round((point.Y - workArea.Y) / (double)cellHeight), 0, rows - 1);
        return new PixelPoint(workArea.X + col * cellWidth, workArea.Y + row * cellHeight);
    }

    /// <summary>
    /// 離 target 最近的空格子（不被分區蓋住、沒有其他圖示、也沒被 reserved 預訂）。
    /// 放下 / 貼上檔案時用：盡量放在滑鼠位置附近。找不到回傳 null。
    /// </summary>
    public static PixelPoint? NearestFreeCell(
        PixelPoint target, PixelRect workArea, IReadOnlyList<PixelRect> obstacles,
        IReadOnlyCollection<PixelPoint> occupied, int cellWidth, int cellHeight, ISet<PixelPoint>? reserved = null)
    {
        int columns = Math.Max(1, workArea.Width / Math.Max(1, cellWidth));
        int rows = Math.Max(1, workArea.Height / Math.Max(1, cellHeight));

        PixelPoint? best = null;
        long bestDistance = long.MaxValue;
        for (int col = 0; col < columns; col++)
        {
            for (int row = 0; row < rows; row++)
            {
                var cell = new PixelPoint(workArea.X + col * cellWidth, workArea.Y + row * cellHeight);
                long dx = cell.X - target.X, dy = cell.Y - target.Y;
                long distance = dx * dx + dy * dy;
                if (distance >= bestDistance || !IsFree(cell, obstacles, occupied, cellWidth, cellHeight, reserved))
                    continue;

                best = cell;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// 自動排列：照順序從左上角往下、再往右放進空格子（避開分區）。放不下的回傳在最後一格（重疊）。
    /// </summary>
    public static List<PixelPoint> Arrange(int count, PixelRect workArea, IReadOnlyList<PixelRect> obstacles, int cellWidth, int cellHeight)
    {
        var result = new List<PixelPoint>(count);
        var reserved = new HashSet<PixelPoint>();
        PixelPoint last = new(workArea.X, workArea.Y);
        for (int i = 0; i < count; i++)
        {
            if (IconLayout.FindFreeSpot(workArea, obstacles, Array.Empty<PixelPoint>(), cellWidth, cellHeight, reserved) is { } spot)
            {
                reserved.Add(spot);
                last = spot;
            }

            result.Add(last);
        }

        return result;
    }

    /// <summary>這個格子沒被分區蓋住、也沒有其他圖示。</summary>
    public static bool IsFree(
        PixelPoint cell, IReadOnlyList<PixelRect> obstacles, IReadOnlyCollection<PixelPoint> occupied,
        int cellWidth, int cellHeight, ISet<PixelPoint>? reserved = null)
    {
        var rect = new PixelRect(cell.X, cell.Y, cellWidth, cellHeight);
        if (reserved?.Contains(cell) == true)
            return false;
        if (obstacles.Any(o => o.X < rect.Right && rect.X < o.Right && o.Y < rect.Bottom && rect.Y < o.Bottom))
            return false;
        return !occupied.Any(p => rect.Contains(p.X + cellWidth / 2, p.Y + cellHeight / 2));
    }
}
