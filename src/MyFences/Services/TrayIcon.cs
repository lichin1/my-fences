using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using MyFences.Models;

namespace MyFences.Services;

/// <summary>系統匣圖示與選單。</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(App app)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("新增分區", null, (_, _) => app.CreateFence(FenceKind.Standard));
        menu.Items.Add("新增資料夾分區…", null, (_, _) => app.CreateFence(FenceKind.Folder));
        menu.Items.Add("依規則整理桌面", null, (_, _) =>
        {
            int count = app.ApplyRules(DesktopHelper.EnumerateDesktopItems());
            ShowBalloon(count > 0 ? $"已放入 {count} 個項目。" : "沒有符合規則的新項目。");
        });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("顯示 / 隱藏所有分區\tCtrl+Alt+F", null, (_, _) => app.ToggleFences());

        var hideIcons = new ToolStripMenuItem("隱藏系統桌面圖示");
        hideIcons.Click += (_, _) => DesktopHelper.SetDesktopIconsVisible(!DesktopHelper.AreDesktopIconsVisible());

        var locked = new ToolStripMenuItem("鎖定分區位置與大小");
        locked.Click += (_, _) => app.SetLocked(!app.Config.Locked);

        var autoSort = new ToolStripMenuItem("新檔案自動依規則整理");
        autoSort.Click += (_, _) =>
        {
            app.Config.AutoSortNewItems = !app.Config.AutoSortNewItems;
            app.RequestSave();
        };

        var autoStart = new ToolStripMenuItem("開機時啟動");
        autoStart.Click += (_, _) => StartupHelper.SetEnabled(!StartupHelper.IsEnabled);

        menu.Items.Add(hideIcons);
        menu.Items.Add(locked);
        menu.Items.Add(autoSort);
        menu.Items.Add(autoStart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("開啟設定資料夾", null, (_, _) =>
            Process.Start(new ProcessStartInfo(ConfigStore.ConfigDirectory) { UseShellExecute = true }));
        menu.Items.Add("結束", null, (_, _) => app.Quit());

        menu.Opening += (_, _) =>
        {
            hideIcons.Checked = !DesktopHelper.AreDesktopIconsVisible();
            locked.Checked = app.Config.Locked;
            autoSort.Checked = app.Config.AutoSortNewItems;
            autoStart.Checked = StartupHelper.IsEnabled;
        };

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MyFences",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ToggleFences();
    }

    public void ShowBalloon(string text) => _icon.ShowBalloonTip(3000, "MyFences", text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch (Exception)
        {
        }

        return SystemIcons.Application;
    }
}
