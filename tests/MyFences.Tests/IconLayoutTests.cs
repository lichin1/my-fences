using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class IconLayoutTests
{
    private const int W = 76;  // 常見的桌面圖示間距
    private const int H = 86;

    [Fact]
    public void Columns_fit_the_body_width_and_never_drop_below_one()
    {
        Assert.Equal(4, IconLayout.Columns(new PixelRect(0, 0, 4 * W + 8, 300), W));
        Assert.Equal(1, IconLayout.Columns(new PixelRect(0, 0, 20, 300), W));
    }

    [Fact]
    public void Grid_fills_rows_left_to_right_and_centers_horizontally()
    {
        var body = new PixelRect(100, 200, 4 * W + 8 + 20, 400); // 多 20px → 置中偏移 10 + padding

        var points = IconLayout.Grid(body, 5, W, H);

        int left = 100 + (body.Width - 4 * W) / 2;
        Assert.Equal(new PixelPoint(left, 204), points[0]);
        Assert.Equal(new PixelPoint(left + 3 * W, 204), points[3]);
        Assert.Equal(new PixelPoint(left, 204 + H), points[4]); // 第二列
    }

    [Fact]
    public void Required_height_grows_with_rows()
    {
        var body = new PixelRect(0, 0, 2 * W + 8, 100);

        Assert.Equal(0, IconLayout.RequiredHeight(body, 0, W, H));
        Assert.Equal(H + 8, IconLayout.RequiredHeight(body, 2, W, H));
        Assert.Equal(2 * H + 8, IconLayout.RequiredHeight(body, 3, W, H));
    }

    [Fact]
    public void Index_at_point_matches_the_grid_cell_under_the_icon_center()
    {
        var body = new PixelRect(0, 0, 3 * W + 8, 400);
        var grid = IconLayout.Grid(body, 6, W, H);

        for (int i = 0; i < grid.Count; i++)
            Assert.Equal(i, IconLayout.IndexAt(body, grid[i], 6, W, H));

        // 放在最後面空白處 → 插在最後
        Assert.Equal(6, IconLayout.IndexAt(body, new PixelPoint(grid[0].X, grid[0].Y + 5 * H), 6, W, H));
    }

    [Fact]
    public void Free_spot_skips_fences_and_occupied_cells()
    {
        var area = new PixelRect(0, 0, 4 * W, 3 * H);
        var fence = new PixelRect(0, 0, W, 3 * H);           // 佔掉第一欄
        var occupied = new[] { new PixelPoint(W, 0) };         // 第二欄第一格有圖示

        PixelPoint? spot = IconLayout.FindFreeSpot(area, new[] { fence }, occupied, W, H);

        Assert.Equal(new PixelPoint(W, H), spot); // 第二欄第二格
    }

    [Fact]
    public void Free_spot_respects_reserved_cells_and_returns_null_when_full()
    {
        var area = new PixelRect(0, 0, W, H);
        Assert.Null(IconLayout.FindFreeSpot(area, Array.Empty<PixelRect>(), Array.Empty<PixelPoint>(), W, H,
            new HashSet<PixelPoint> { new(0, 0) }));
    }

    [Fact]
    public void Center_inside_uses_the_icon_center()
    {
        var fence = new PixelRect(100, 100, 200, 200);

        Assert.True(IconLayout.CenterInside(fence, new PixelPoint(80, 80), W, H));   // 中心 (118,123)
        Assert.False(IconLayout.CenterInside(fence, new PixelPoint(20, 20), W, H));  // 中心 (58,63)
    }
}
