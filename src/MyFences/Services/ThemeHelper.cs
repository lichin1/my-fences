using System;
using System.Windows.Media;
using Microsoft.Win32;

namespace MyFences.Services;

/// <summary>Windows 的淺色 / 深色模式與強調色，系統設定改變時發出 <see cref="Changed"/>。</summary>
internal static class ThemeHelper
{
    /// <summary>分區顏色設成這個值 = 跟隨系統（深色模式用深灰、淺色模式用淺灰）。</summary>
    public const string SystemColor = "system";

    /// <summary>分區顏色設成這個值 = 使用 Windows 強調色。</summary>
    public const string AccentColorKey = "accent";

    private static bool _listening;

    public static event Action? Changed;

    public static bool IsLightTheme
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
    }

    public static Color AccentColor
    {
        get
        {
            // DWM 的 AccentColor 是 ABGR 格式
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                uint v = unchecked((uint)abgr);
                return Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }

            return Color.FromRgb(0x00, 0x78, 0xD4); // Windows 預設藍
        }
    }

    /// <summary>分區顏色設定轉成實際顏色（支援 system / accent / #RRGGBB）。</summary>
    public static Color Resolve(string? color)
    {
        if (string.IsNullOrEmpty(color) || color == SystemColor)
            return IsLightTheme ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);
        if (color == AccentColorKey)
            return AccentColor;

        try
        {
            return (Color)ColorConverter.ConvertFromString(color);
        }
        catch (FormatException)
        {
            return Colors.Black;
        }
    }

    /// <summary>背景夠亮就用深色字，否則用白字（依人眼感受的亮度）。</summary>
    public static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.6;

    public static void StartListening()
    {
        if (_listening)
            return;

        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => Changed?.Invoke());
        };
    }
}
