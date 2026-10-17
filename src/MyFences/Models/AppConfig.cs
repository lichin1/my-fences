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
