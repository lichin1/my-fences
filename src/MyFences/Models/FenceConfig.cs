using System;
using System.Collections.Generic;
using System.IO;

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

    /// <summary>
    /// 自動整理規則：副檔名（例如 ".lnk"、"pdf"），"folder" 代表資料夾，"*" 代表所有檔案。
    /// </summary>
    public List<string> Rules { get; set; } = new();

    public bool Matches(string path)
    {
        bool isDir = Directory.Exists(path);
        string ext = Path.GetExtension(path);

        foreach (string raw in Rules)
        {
            string rule = raw.Trim().ToLowerInvariant();
            if (rule.Length == 0)
                continue;

            if (rule == "folder")
            {
                if (isDir)
                    return true;
                continue;
            }

            if (isDir)
                continue;

            if (rule == "*")
                return true;

            string ruleExt = rule.StartsWith('.') ? rule : "." + rule;
            if (string.Equals(ext, ruleExt, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
