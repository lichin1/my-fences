using System.ComponentModel;
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

    public static FenceItem Create(string path) =>
        new(path, Services.ShellItems.GetDisplayName(path), Services.ShellItems.Exists(path));
}
