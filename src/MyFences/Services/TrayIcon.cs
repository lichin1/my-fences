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

        // 常用的放這裡，其他開關都在「設定」視窗
        menu.Items.Add("新增分區（也可以在桌面 Ctrl+拖曳）", null, (_, _) => app.CreateFence(FenceKind.Standard));
        menu.Items.Add("新增資料夾分區…", null, (_, _) => app.CreateFence(FenceKind.Folder));
        menu.Items.Add("依規則整理桌面", null, (_, _) =>
        {
            // 有放入項目時會顯示附「復原」的提示
            if (app.SortDesktop() == 0)
                Views.Toast.ShowMessage("桌面上沒有符合規則的新項目。");
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("顯示 / 隱藏所有分區\tCtrl+Alt+F", null, (_, _) => app.ToggleFences());
        menu.Items.Add(BuildPagesMenu(app));
        menu.Items.Add(new ToolStripSeparator());
        var settings = new ToolStripMenuItem("設定…") { Font = new Font(menu.Font, FontStyle.Bold) };
        settings.Click += (_, _) => Views.SettingsWindow.ShowFor(app);
        menu.Items.Add(settings);
        menu.Items.Add("結束", null, (_, _) => app.Quit());

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
