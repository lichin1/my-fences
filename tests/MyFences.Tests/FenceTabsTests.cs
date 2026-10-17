using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class FenceTabsTests
{
    [Fact]
    public void First_new_tab_keeps_existing_items_in_tab_one()
    {
        var fence = new FenceConfig { Name = "工作", Items = { "a", "b" } };

        fence.AddTab("分頁 2");

        Assert.Equal(new[] { "工作", "分頁 2" }, fence.Tabs.Select(t => t.Name));
        Assert.Equal(new[] { "a", "b" }, fence.Tabs[0].Items);
        Assert.Empty(fence.Items);
        Assert.Equal(1, fence.ActiveTab);
        Assert.Empty(fence.CurrentItems);
    }

    [Fact]
    public void All_item_lists_cover_every_tab()
    {
        var fence = new FenceConfig { Items = { "a" } };
        fence.AddTab("2");
        fence.CurrentItems.Add("b");

        Assert.Equal(new[] { "a", "b" }, fence.AllItemLists().SelectMany(l => l));
    }

    [Fact]
    public void Removing_a_tab_moves_its_items_to_the_neighbour()
    {
        var fence = new FenceConfig { Items = { "a" } };
        fence.AddTab("2");
        fence.CurrentItems.Add("b");
        fence.AddTab("3");
        fence.CurrentItems.Add("c");

        fence.RemoveTab(1);

        Assert.Equal(2, fence.Tabs.Count);
        Assert.Equal(new[] { "a", "b" }, fence.Tabs[0].Items);
        Assert.Equal(0, fence.ActiveTab);
    }

    [Fact]
    public void Removing_down_to_one_tab_turns_tabs_off_without_losing_items()
    {
        var fence = new FenceConfig { Items = { "a" } };
        fence.AddTab("2");
        fence.CurrentItems.Add("b");

        fence.RemoveTab(0);

        Assert.False(fence.HasTabs);
        Assert.Equal(new[] { "b", "a" }, fence.Items);
        Assert.Same(fence.Items, fence.CurrentItems);
    }

    [Fact]
    public void Move_to_tab_moves_only_from_the_current_tab()
    {
        var fence = new FenceConfig { Items = { "a", "b" } };
        fence.AddTab("2");
        fence.ActiveTab = 0;

        fence.MoveToTab("A", 1); // 不分大小寫

        Assert.Equal(new[] { "b" }, fence.Tabs[0].Items);
        Assert.Equal(new[] { "A" }, fence.Tabs[1].Items);
    }

    [Fact]
    public void Active_tab_is_clamped_after_bad_config()
    {
        var fence = new FenceConfig();
        fence.AddTab("2");
        fence.ActiveTab = 99;

        Assert.Same(fence.Tabs[1].Items, fence.CurrentItems);
    }
}
