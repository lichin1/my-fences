using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MyFences.Models;

public enum FenceKind
{
    /// <summary>手動放入的項目清單（存的是路徑參照，不會搬動檔案）。</summary>
    Standard,

    /// <summary>資料夾入口：即時顯示某個資料夾的內容。</summary>
    Folder,
}

public sealed class FenceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新分區";
    public FenceKind Kind { get; set; } = FenceKind.Standard;

    public double Left { get; set; } = 40;
    public double Top { get; set; } = 40;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 240;
    public bool RolledUp { get; set; }

    public string Color { get; set; } = "#1E1E1E";
    public double Opacity { get; set; } = 0.55;
    public double IconSize { get; set; } = 48;

    /// <summary>毛玻璃背景（Windows 10 / 11）。</summary>
    public bool Blur { get; set; }

    /// <summary>平常淡化，滑鼠移上去才恢復。</summary>
    public bool FadeWhenIdle { get; set; }

    /// <summary>在第幾頁桌面（從 0 開始）。</summary>
    public int Page { get; set; }

    /// <summary>Standard 分區沒有分頁時的項目（完整路徑）。有分頁時項目放在 <see cref="Tabs"/>，這裡是空的。</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>分區內的分頁；少於 2 個就是沒有分頁。</summary>
    public List<FenceTab> Tabs { get; set; } = new();

    public int ActiveTab { get; set; }

    [JsonIgnore]
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>目前顯示中的項目清單（有分頁時是作用中分頁的）。</summary>
    [JsonIgnore]
    public List<string> CurrentItems
    {
        get
        {
            if (!HasTabs)
                return Items;
            ActiveTab = Math.Clamp(ActiveTab, 0, Tabs.Count - 1);
            return Tabs[ActiveTab].Items;
        }
    }

    /// <summary>所有分頁的項目清單（用來判斷某個檔案是不是已經在這個分區裡）。</summary>
    public IEnumerable<List<string>> AllItemLists()
    {
        if (!HasTabs)
        {
            yield return Items;
            yield break;
        }

        foreach (FenceTab tab in Tabs)
            yield return tab.Items;
    }

    /// <summary>新增分頁並切換過去。第一次新增時，原本的項目會變成第一個分頁。</summary>
    public void AddTab(string name)
    {
        if (!HasTabs)
        {
            Tabs.Add(new FenceTab { Name = Name, Items = Items });
            Items = new List<string>();
        }

        Tabs.Add(new FenceTab { Name = name });
        ActiveTab = Tabs.Count - 1;
    }

    /// <summary>刪除分頁，裡面的項目移到旁邊的分頁（不會遺失）。只剩一個分頁時就取消分頁。</summary>
    public void RemoveTab(int index)
    {
        if (!HasTabs || index < 0 || index >= Tabs.Count)
            return;

        FenceTab removed = Tabs[index];
        Tabs.RemoveAt(index);
        int target = Math.Max(0, index - 1);
        if (Tabs.Count > 0)
            Tabs[target].Items.AddRange(removed.Items);

        if (Tabs.Count <= 1)
        {
            Items = Tabs.Count == 1 ? Tabs[0].Items : removed.Items;
            Tabs.Clear();
            ActiveTab = 0;
            return;
        }

        ActiveTab = target;
    }

    /// <summary>把項目從目前分頁移到另一個分頁。</summary>
    public void MoveToTab(string path, int tabIndex)
    {
        if (!HasTabs || tabIndex < 0 || tabIndex >= Tabs.Count || tabIndex == ActiveTab)
            return;

        int removed = CurrentItems.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            Tabs[tabIndex].Items.Add(path);
    }

    /// <summary>Folder 分區顯示的資料夾。</summary>
    public string? FolderPath { get; set; }

    /// <summary>自動整理規則，語法見 <see cref="FenceRules"/>。</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>規則優先順序：數字大的分區先比對（同一個項目符合多個分區時用）。</summary>
    public int RulePriority { get; set; }

    public bool Matches(string path) => FenceRules.Matches(Rules, path);
}

public sealed class FenceTab
{
    public string Name { get; set; } = "分頁";
    public List<string> Items { get; set; } = new();
}
