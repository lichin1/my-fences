using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class LayoutMathTests
{
    private static ScreenInfo Screen(string name, int x, int y, int w, int h, bool primary = false) =>
        new(name, new PixelRect(x, y, w, h), primary);

    [Fact]
    public void Resolution_change_scales_position_and_size()
    {
        var oldScreens = new[] { Screen("D1", 0, 0, 1920, 1040, primary: true) };
        var newScreens = new[] { Screen("D1", 0, 0, 3840, 2080, primary: true) };

        PixelRect result = LayoutMath.Remap(new PixelRect(100, 200, 300, 400), oldScreens, newScreens);

        Assert.Equal(new PixelRect(200, 400, 600, 800), result);
    }

    [Fact]
    public void Fence_stays_on_the_same_monitor_by_device_name()
    {
        var oldScreens = new[] { Screen("D1", 0, 0, 1920, 1040, true), Screen("D2", 1920, 0, 1920, 1040) };
        // D2 換到左邊、解析度不變
        var newScreens = new[] { Screen("D2", -1920, 0, 1920, 1040), Screen("D1", 0, 0, 1920, 1040, true) };

        PixelRect result = LayoutMath.Remap(new PixelRect(2000, 100, 300, 200), oldScreens, newScreens);

        Assert.Equal(new PixelRect(-1840, 100, 300, 200), result);
    }

    [Fact]
    public void Unplugged_monitor_moves_fence_to_primary_and_keeps_it_on_screen()
    {
        var oldScreens = new[] { Screen("D1", 0, 0, 1920, 1040, true), Screen("D2", 1920, 0, 2560, 1400) };
        var newScreens = new[] { Screen("D1", 0, 0, 1920, 1040, true) };

        PixelRect result = LayoutMath.Remap(new PixelRect(4000, 1000, 400, 300), oldScreens, newScreens);

        Assert.True(result.X >= 0 && result.Right <= 1920);
        Assert.True(result.Y >= 0 && result.Bottom <= 1040);
    }

    [Fact]
    public void Same_screens_leave_fence_alone()
    {
        var screens = new[] { Screen("D1", 0, 0, 1920, 1040, true) };
        var fence = new PixelRect(10, 20, 300, 200);

        Assert.Equal(fence, LayoutMath.Remap(fence, screens, screens));
    }

    [Fact]
    public void Clamp_shrinks_oversized_fence_into_area()
    {
        PixelRect result = LayoutMath.Clamp(new PixelRect(-50, -50, 5000, 300), new PixelRect(0, 0, 1920, 1040));

        Assert.Equal(new PixelRect(0, 0, 1920, 300), result);
    }

    [Fact]
    public void Profile_key_ignores_screen_order()
    {
        var a = new[] { Screen("D1", 0, 0, 1920, 1040, true), Screen("D2", 1920, 0, 1920, 1040) };
        var b = new[] { a[1], a[0] };

        Assert.Equal(LayoutMath.ProfileKey(a), LayoutMath.ProfileKey(b));
        Assert.NotEqual(LayoutMath.ProfileKey(a), LayoutMath.ProfileKey(new[] { a[0] }));
    }
}

public sealed class LayoutSerializationTests
{
    [Fact]
    public void Screen_info_round_trips_through_json()
    {
        var screens = new List<ScreenInfo> { new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1040), true) };

        string json = System.Text.Json.JsonSerializer.Serialize(screens);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ScreenInfo>>(json);

        Assert.Equal(screens, back);
    }
}

public sealed class SnapTests
{
    private static readonly PixelRect Area = new(0, 0, 1920, 1040);

    [Fact]
    public void Moving_near_screen_edge_snaps_to_it()
    {
        var result = LayoutMath.Snap(new PixelRect(7, 300, 200, 100), Array.Empty<PixelRect>(), Area, SnapEdges.All);

        Assert.Equal(new PixelRect(0, 300, 200, 100), result.Rect);
        Assert.Equal(0, result.GuideX);
        Assert.Null(result.GuideY);
    }

    [Fact]
    public void Moving_next_to_another_fence_snaps_beside_it_with_a_gap()
    {
        var other = new PixelRect(100, 100, 300, 200); // 右邊在 400
        var result = LayoutMath.Snap(new PixelRect(413, 105, 200, 100), new[] { other }, Area, SnapEdges.All);

        Assert.Equal(408, result.Rect.X);  // 400 + 8 間距
        Assert.Equal(100, result.Rect.Y);  // 頂端對齊
        Assert.Equal(408, result.GuideX);
        Assert.Equal(100, result.GuideY);
    }

    [Fact]
    public void Far_away_fences_are_ignored()
    {
        var other = new PixelRect(100, 900, 300, 100);
        var result = LayoutMath.Snap(new PixelRect(104, 100, 200, 100), new[] { other }, Area, SnapEdges.All);

        Assert.Equal(104, result.Rect.X);
    }

    [Fact]
    public void Nothing_close_means_no_change()
    {
        var rect = new PixelRect(500, 500, 200, 100);
        var result = LayoutMath.Snap(rect, Array.Empty<PixelRect>(), Area, SnapEdges.All);

        Assert.Equal(rect, result.Rect);
        Assert.Null(result.GuideX);
        Assert.Null(result.GuideY);
    }

    [Fact]
    public void Resizing_only_moves_the_dragged_edge()
    {
        var other = new PixelRect(600, 100, 200, 200);
        // 拖右邊，靠近另一個分區的左邊（600 - 8 = 592）
        var result = LayoutMath.Snap(new PixelRect(100, 100, 488, 200), new[] { other }, Area, SnapEdges.Right);

        Assert.Equal(new PixelRect(100, 100, 492, 200), result.Rect);
    }
}
