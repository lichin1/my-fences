using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            int count = app.SortDesktop();
            ShowBalloon(count > 0 ? $"已放入 {count} 個項目。" : "沒有符合規則的新項目。");
        });
        menu.Items.Add(BuildSnapshotMenu(app));
        menu.Items.Add(BuildPagesMenu(app));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("顯示 / 隱藏所有分區\tCtrl+Alt+F", null, (_, _) => app.ToggleFences());

        var quickHide = new ToolStripMenuItem("雙擊桌面空白處隱藏分區");
        quickHide.Click += (_, _) => app.SetQuickHide(!app.Config.QuickHideEnabled);

        var hideFenced = new ToolStripMenuItem("放進分區的圖示從桌面隱藏");
        hideFenced.Click += (_, _) => app.SetHideFencedDesktopIcons(!app.Config.HideFencedDesktopIcons);

        var hideIcons = new ToolStripMenuItem("隱藏所有系統桌面圖示");
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

        menu.Items.Add(quickHide);
        menu.Items.Add(hideFenced);
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
            quickHide.Checked = app.Config.QuickHideEnabled;
            hideFenced.Checked = app.Config.HideFencedDesktopIcons;
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

    /// <summary>桌面頁面子選單。</summary>
    private static ToolStripMenuItem BuildPagesMenu(App app)
    {
        var root = new ToolStripMenuItem("桌面頁面");
        root.DropDownItems.Add("(載入中)");
        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            for (int i = 0; i < app.Config.PageCount; i++)
            {
                int page = i;
                var item = new ToolStripMenuItem($"第 {page + 1} 頁") { Checked = page == app.Config.CurrentPage };
                item.Click += (_, _) => app.SwitchPage(page);
                root.DropDownItems.Add(item);
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add("新增頁面", null, (_, _) => app.AddPage());
            var remove = new ToolStripMenuItem("刪除目前頁面（分區移到前一頁）") { Enabled = app.Config.PageCount > 1 };
            remove.Click += (_, _) => app.RemoveCurrentPage();
            root.DropDownItems.Add(remove);
            root.DropDownItems.Add(new ToolStripMenuItem("Ctrl+Alt+← / → 切換頁面") { Enabled = false });
        };
        return root;
    }

    /// <summary>快照子選單：每次打開時重新列出快照。</summary>
    private ToolStripMenuItem BuildSnapshotMenu(App app)
    {
        var root = new ToolStripMenuItem("快照");
        root.DropDownItems.Add("(載入中)");
        root.DropDownOpening += (_, _) =>
        {
            root.DropDownItems.Clear();
            root.DropDownItems.Add("建立快照…", null, (_, _) =>
            {
                string? name = Views.InputDialog.Ask("建立快照", "快照名稱：", DateTime.Now.ToString("yyyy/MM/dd HH:mm"));
                if (name != null && app.CreateSnapshot(name.Trim(), SnapshotKind.Manual) != null)
                    ShowBalloon("已建立快照。");
            });
            root.DropDownItems.Add(new ToolStripSeparator());

            List<Snapshot> snapshots = SnapshotStore.List();
            if (snapshots.Count == 0)
                root.DropDownItems.Add(new ToolStripMenuItem("（還沒有快照）") { Enabled = false });

            foreach (Snapshot snapshot in snapshots.Take(20))
            {
                string kind = snapshot.Kind switch
                {
                    SnapshotKind.Daily => "每日",
                    SnapshotKind.Backup => "自動備份",
                    _ => "手動",
                };
                var item = new ToolStripMenuItem($"{snapshot.CreatedAt:MM/dd HH:mm}  {snapshot.Name}（{kind}，{snapshot.Fences.Count} 個分區）");
                item.DropDownItems.Add("還原這個快照", null, (_, _) =>
                {
                    var answer = System.Windows.MessageBox.Show(
                        $"要還原「{snapshot.Name}」嗎？\n目前的分區會被換掉（還原前會自動備份一份）。",
                        "MyFences", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                    if (answer == System.Windows.MessageBoxResult.Yes)
                        app.RestoreSnapshot(snapshot);
                });
                item.DropDownItems.Add("刪除", null, (_, _) => SnapshotStore.Delete(snapshot));
                root.DropDownItems.Add(item);
            }

            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add("開啟快照資料夾", null, (_, _) =>
            {
                System.IO.Directory.CreateDirectory(SnapshotStore.SnapshotDirectory);
                Process.Start(new ProcessStartInfo(SnapshotStore.SnapshotDirectory) { UseShellExecute = true });
            });
        };
        return root;
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
