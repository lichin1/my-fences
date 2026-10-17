using System;
using System.Collections.Generic;

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

    /// <summary>Standard 分區的項目（完整路徑）。</summary>
    public List<string> Items { get; set; } = new();

    /// <summary>Folder 分區顯示的資料夾。</summary>
    public string? FolderPath { get; set; }

    /// <summary>自動整理規則，語法見 <see cref="FenceRules"/>。</summary>
    public List<string> Rules { get; set; } = new();

    /// <summary>規則優先順序：數字大的分區先比對（同一個項目符合多個分區時用）。</summary>
    public int RulePriority { get; set; }

    public bool Matches(string path) => FenceRules.Matches(Rules, path);
}
