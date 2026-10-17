using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

namespace MyFences.Models;

/// <summary>分區裡顯示的一個圖示（只在畫面上使用，不存檔）。</summary>
public sealed class FenceItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    private FenceItem(string fullPath, string displayName, bool exists)
    {
        FullPath = fullPath;
        DisplayName = displayName;
        Exists = exists;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; }
    public string DisplayName { get; }
    public bool Exists { get; }
    public double ItemOpacity => Exists ? 1.0 : 0.45;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public static FenceItem Create(string path)
    {
        bool isDir = Directory.Exists(path);
        bool exists = isDir || File.Exists(path);
        return new FenceItem(path, GetDisplayName(path, isDir), exists);
    }

    private static string GetDisplayName(string path, bool isDir)
    {
        if (isDir)
        {
            string name = new DirectoryInfo(path).Name;
            return string.IsNullOrEmpty(name) ? path : name;
        }

        string ext = Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(path);
        }

        return Path.GetFileName(path);
    }
}
