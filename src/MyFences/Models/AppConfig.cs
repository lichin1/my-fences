using System.Collections.Generic;

namespace MyFences.Models;

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<FenceConfig> Fences { get; set; } = new();

    /// <summary>鎖定後不能拖曳或縮放分區。</summary>
    public bool Locked { get; set; }

    /// <summary>桌面出現新檔案時，自動依規則放進分區。</summary>
    public bool AutoSortNewItems { get; set; } = true;

    /// <summary>雙擊桌面空白處隱藏 / 顯示所有分區。</summary>
    public bool QuickHideEnabled { get; set; } = true;

    /// <summary>第一次觸發雙擊隱藏時提示過了沒。</summary>
    public bool QuickHideHintShown { get; set; }

    /// <summary>放進分區的桌面檔案，從桌面上隱藏（設成隱藏屬性）。</summary>
    public bool HideFencedDesktopIcons { get; set; } = true;

    /// <summary>由 MyFences 設成隱藏的桌面檔案（只會還原這些，不碰使用者自己隱藏的檔案）。</summary>
    public List<string> HiddenDesktopItems { get; set; } = new();

    /// <summary>桌面頁數（至少 1）。</summary>
    public int PageCount { get; set; } = 1;

    public int CurrentPage { get; set; }

    /// <summary>
    /// 每一種螢幕組合（<see cref="LayoutMath.ProfileKey"/>）下各分區的位置，
    /// 回到同一組螢幕設定時就還原當時的版面。
    /// </summary>
    public Dictionary<string, Dictionary<string, SavedBounds>> MonitorLayouts { get; set; } = new();

    /// <summary>上次結束時的螢幕組合（下次啟動時螢幕變了就自動換算）。</summary>
    public List<ScreenInfo> LastScreens { get; set; } = new();

    public static AppConfig CreateDefault()
    {
        var config = new AppConfig();
        config.Fences.Add(new FenceConfig
        {
            Name = "捷徑",
            Left = 40,
            Top = 40,
            Rules = { ".lnk", ".url" },
        });
        config.Fences.Add(new FenceConfig
        {
            Name = "資料夾",
            Left = 420,
            Top = 40,
            Color = "#1F4E79",
            Rules = { "folder" },
        });
        config.Fences.Add(new FenceConfig
        {
            Name = "文件",
            Left = 40,
            Top = 310,
            Color = "#2E5E3A",
            Rules = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".txt", ".md" },
        });
        return config;
    }
}

/// <summary>某個螢幕組合下，一個分區的位置。</summary>
public sealed class SavedBounds
{
    /// <summary>視窗的螢幕像素座標。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>展開時的高度（WPF 單位），收合中的分區要靠這個還原。</summary>
    public double ExpandedHeight { get; set; }
}
