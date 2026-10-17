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
