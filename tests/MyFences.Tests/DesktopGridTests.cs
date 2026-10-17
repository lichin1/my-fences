using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class DesktopGridTests
{
    private const int W = 84;
    private const int H = 94;
    private static readonly PixelRect Area = new(0, 0, 10 * W, 8 * H);

    [Fact]
    public void Cell_size_scales_with_icon_size_and_dpi()
    {
        Assert.Equal((84, 94), DesktopGrid.CellSize(48, 1.0));
        Assert.Equal((126, 141), DesktopGrid.CellSize(48, 1.5));
    }

    [Fact]
    public void Snap_rounds_to_the_nearest_cell_and_stays_inside()
    {
        Assert.Equal(new PixelPoint(W, 2 * H), DesktopGrid.Snap(new PixelPoint(W + 30, 2 * H - 40), Area, W, H));
        Assert.Equal(new PixelPoint(9 * W, 7 * H), DesktopGrid.Snap(new PixelPoint(5000, 5000), Area, W, H));
        Assert.Equal(new PixelPoint(0, 0), DesktopGrid.Snap(new PixelPoint(-300, -10), Area, W, H));
    }

    [Fact]
    public void Nearest_free_cell_prefers_the_target_and_skips_taken_cells()
    {
        var target = new PixelPoint(3 * W, 3 * H);

        Assert.Equal(target, DesktopGrid.NearestFreeCell(target, Area, Array.Empty<PixelRect>(), Array.Empty<PixelPoint>(), W, H));

        PixelPoint? next = DesktopGrid.NearestFreeCell(target, Area, Array.Empty<PixelRect>(), new[] { target }, W, H);
        Assert.NotNull(next);
        Assert.NotEqual(target, next);
        Assert.True(Math.Abs(next!.Value.X - target.X) <= W && Math.Abs(next.Value.Y - target.Y) <= H);
    }

    [Fact]
    public void Nearest_free_cell_avoids_fences()
    {
        var fence = new PixelRect(2 * W, 2 * H, 3 * W, 3 * H); // 蓋住 (2..4, 2..4)
        PixelPoint? cell = DesktopGrid.NearestFreeCell(new PixelPoint(3 * W, 3 * H), Area, new[] { fence }, Array.Empty<PixelPoint>(), W, H);

        Assert.NotNull(cell);
        Assert.True(DesktopGrid.IsFree(cell!.Value, new[] { fence }, Array.Empty<PixelPoint>(), W, H));
    }

    [Fact]
    public void Arrange_fills_columns_top_down_and_skips_fences()
    {
        var fence = new PixelRect(0, 0, W, 2 * H); // 第一欄前兩格被蓋住

        var points = DesktopGrid.Arrange(3, Area, new[] { fence }, W, H);

        Assert.Equal(new PixelPoint(0, 2 * H), points[0]);
        Assert.Equal(new PixelPoint(0, 3 * H), points[1]);
        Assert.Equal(new PixelPoint(0, 4 * H), points[2]);
    }

    [Fact]
    public void Arrange_overflow_reuses_the_last_cell()
    {
        var tiny = new PixelRect(0, 0, W, H);
        var points = DesktopGrid.Arrange(3, tiny, Array.Empty<PixelRect>(), W, H);

        Assert.All(points, p => Assert.Equal(new PixelPoint(0, 0), p));
    }
}
