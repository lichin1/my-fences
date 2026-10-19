using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyFences.Models;
using MyFences.Services;

namespace MyFences.Views;

/// <summary>設定視窗：一般、自動整理規則、快照、操作說明。系統匣只留常用的動作。</summary>
internal sealed class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly App _app;
    private readonly StackPanel _rulesPanel = new();
    private readonly ListBox _snapshotList = new() { MinHeight = 260, BorderThickness = new Thickness(1) };

    private SettingsWindow(App app)
    {
        _app = app;
        Title = "MyFences 設定";
        Width = 620;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));
        try
        {
            if (Environment.ProcessPath is { } exe)
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    System.Drawing.Icon.ExtractAssociatedIcon(exe)!.Handle, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception)
        {
        }

        var tabs = new TabControl { Margin = new Thickness(12), Padding = new Thickness(16) };
        tabs.Items.Add(new TabItem { Header = "一般", Content = BuildGeneral(), Padding = new Thickness(12, 4, 12, 4) });
        tabs.Items.Add(new TabItem { Header = "自動整理規則", Content = BuildRules(), Padding = new Thickness(12, 4, 12, 4) });
        tabs.Items.Add(new TabItem { Header = "快照", Content = BuildSnapshots(), Padding = new Thickness(12, 4, 12, 4) });
        tabs.Items.Add(new TabItem { Header = "操作說明", Content = BuildHelp(), Padding = new Thickness(12, 4, 12, 4) });
        Content = tabs;

        Closed += (_, _) => _open = null;
    }

    public static void ShowFor(App app)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        _open = new SettingsWindow(app);
        _open.Show();
        _open.Activate();
    }

    // ───────────── 一般 ─────────────

    private UIElement BuildGeneral()
    {
        var panel = new StackPanel();
        panel.Children.Add(Header("桌面"));
        panel.Children.Add(Toggle("雙擊桌面空白處，隱藏 / 顯示所有分區和桌面圖示",
            () => _app.Config.QuickHideEnabled, v => _app.SetQuickHide(v)));
        panel.Children.Add(Toggle("自動排列桌面圖示（依名稱，從左上角往下排）",
            () => _app.Config.AutoArrange, v => _app.SetAutoArrange(v)));
        panel.Children.Add(Toggle("桌面圖示對齊格線",
            () => _app.Config.SnapToGrid, v => _app.SetSnapToGrid(v)));

        var sizes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        sizes.Children.Add(new TextBlock { Text = "桌面圖示大小：", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        foreach ((string name, double size) in new[] { ("小", 32.0), ("中", 48.0), ("大", 64.0) })
            sizes.Children.Add(Action(name, () => _app.SetDesktopIconSize(size), primary: Math.Abs(_app.Config.DesktopIconSize - size) < 0.1));
        sizes.Children.Add(Action("依名稱排列一次", _app.ArrangeDesktopByName));
        panel.Children.Add(sizes);
        panel.Children.Add(new TextBlock
        {
            Text = "MyFences 執行時會關掉 Windows 的「顯示桌面圖示」，改由 MyFences 顯示（結束時會還原）。桌面空白處的右鍵選單還是 Windows 原本的。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0),
        });

        panel.Children.Add(Header("分區"));
        panel.Children.Add(Toggle("桌面出現新檔案時，自動依規則放進分區",
            () => _app.Config.AutoSortNewItems, v =>
            {
                _app.Config.AutoSortNewItems = v;
                _app.RequestSave();
            }));
        panel.Children.Add(Toggle("鎖定分區的位置與大小",
            () => _app.Config.Locked, v => _app.SetLocked(v)));

        panel.Children.Add(Header("系統"));
        panel.Children.Add(Toggle("開機時自動啟動 MyFences",
            () => StartupHelper.IsEnabled, StartupHelper.SetEnabled));

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Action("開啟設定資料夾", () =>
            Process.Start(new ProcessStartInfo(ConfigStore.ConfigDirectory) { UseShellExecute = true })));
        panel.Children.Add(buttons);
        return Scroll(panel);
    }

    // ───────────── 規則 ─────────────

    private UIElement BuildRules()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "每個分區可以設定規則，桌面上符合的檔案會自動放進去。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(_rulesPanel);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(Action("現在依規則整理桌面", () =>
        {
            if (_app.SortDesktop() == 0)
                Toast.ShowMessage("桌面已經很整齊了，沒有要整理的項目");
        }, primary: true));
        panel.Children.Add(buttons);

        RefreshRules();
        return Scroll(panel);
    }

    private void RefreshRules()
    {
        _rulesPanel.Children.Clear();
        List<FenceConfig> fences = _app.Config.Fences.Where(f => f.Kind == FenceKind.Standard).ToList();
        if (fences.Count == 0)
        {
            _rulesPanel.Children.Add(new TextBlock { Text = "還沒有分區。", Foreground = Brushes.Gray });
            return;
        }

        foreach (FenceConfig fence in fences)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            Button edit = Action("編輯規則…", () =>
            {
                if (RuleEditorWindow.Edit(fence))
                {
                    _app.RequestSave();
                    RefreshRules();
                }
            });
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);

            string summary = fence.Rules.Count == 0
                ? "（沒有規則）"
                : string.Join("　或　", FenceRules.GroupForEditing(fence.Rules)
                    .Select(g => string.Join(" 而且 ", g.Select(FenceRules.Describe))));
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = fence.Name, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = summary, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0) });
            row.Children.Add(text);

            _rulesPanel.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = row,
            });
        }
    }

    // ───────────── 快照 ─────────────

    private UIElement BuildSnapshots()
    {
        var panel = new DockPanel();
        var intro = new TextBlock
        {
            Text = "快照會記下所有分區的位置、內容和規則。每天會自動存一份；整理桌面或還原之前也會自動備份。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(intro, Dock.Top);
        panel.Children.Add(intro);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Action("建立快照", () =>
        {
            // 不用先想名稱：以時間命名，直接建立
            if (_app.CreateSnapshot("手動快照", SnapshotKind.Manual) != null)
            {
                RefreshSnapshots();
                Toast.ShowMessage("已建立快照");
            }
        }, primary: true));
        buttons.Children.Add(Action("還原選取的快照", () =>
        {
            if (_snapshotList.SelectedItem is ListBoxItem { Tag: Snapshot snapshot })
            {
                _app.RestoreSnapshot(snapshot); // 可以從右下角的提示復原
                RefreshSnapshots();
            }
        }));
        buttons.Children.Add(Action("刪除", () =>
        {
            if (_snapshotList.SelectedItem is ListBoxItem { Tag: Snapshot snapshot })
            {
                SnapshotStore.Delete(snapshot);
                RefreshSnapshots();
            }
        }));
        buttons.Children.Add(Action("開啟快照資料夾", () =>
        {
            System.IO.Directory.CreateDirectory(SnapshotStore.SnapshotDirectory);
            Process.Start(new ProcessStartInfo(SnapshotStore.SnapshotDirectory) { UseShellExecute = true });
        }));
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        _snapshotList.MouseDoubleClick += (_, _) =>
        {
            if (_snapshotList.SelectedItem is ListBoxItem { Tag: Snapshot snapshot })
                _app.RestoreSnapshot(snapshot);
        };
        panel.Children.Add(_snapshotList);
        RefreshSnapshots();
        return panel;
    }

    private void RefreshSnapshots()
    {
        _snapshotList.Items.Clear();
        foreach (Snapshot snapshot in SnapshotStore.List())
        {
            string kind = snapshot.Kind switch
            {
                SnapshotKind.Daily => "每日自動",
                SnapshotKind.Backup => "自動備份",
                _ => "手動",
            };
            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };
            var right = new TextBlock { Text = $"{kind}・{snapshot.Fences.Count} 個分區", Foreground = Brushes.Gray };
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);
            row.Children.Add(new TextBlock { Text = $"{snapshot.CreatedAt:yyyy/MM/dd HH:mm}　{snapshot.Name}" });
            _snapshotList.Items.Add(new ListBoxItem { Content = row, Tag = snapshot, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }

        if (_snapshotList.Items.Count == 0)
            _snapshotList.Items.Add(new ListBoxItem { Content = "還沒有快照", IsEnabled = false });
    }

    // ───────────── 操作說明 ─────────────

    private static UIElement BuildHelp()
    {
        var panel = new StackPanel();
        void Section(string title, params (string Gesture, string What)[] rows)
        {
            panel.Children.Add(Header(title));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var g = new TextBlock { Text = rows[i].Gesture, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 8, 3) };
                var w = new TextBlock { Text = rows[i].What, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(g, i);
                Grid.SetRow(w, i);
                Grid.SetColumn(w, 1);
                grid.Children.Add(g);
                grid.Children.Add(w);
            }

            panel.Children.Add(grid);
        }

        Section("桌面",
            ("Ctrl + 左鍵拖曳", "在桌面空白處畫出新分區"),
            ("雙擊桌面空白處", "隱藏 / 顯示所有分區和桌面圖示"),
            ("在空白處按右鍵", "Windows 原本的桌面選單（新增、貼上、顯示設定…）"),
            ("在空白處拖曳", "框選桌面圖示"),
            ("Ctrl + Alt + F", "隱藏 / 顯示所有分區"),
            ("Ctrl + Alt + ← / →", "切換桌面頁面"),
            ("點桌面底部的圓點", "切換到那一頁（有兩頁以上才會出現）"));
        Section("分區",
            ("拖曳標題列", "移動（會自動對齊，按住 Alt 暫時不對齊）"),
            ("雙擊名稱", "直接改名（F2 也可以）"),
            ("雙擊標題列空白處", "收合 / 展開"),
            ("滑鼠移到標題列", "出現新增分頁、外觀、收合、更多設定按鈕"),
            ("把資料夾拖到標題列", "空的分區直接變成資料夾分區；有東西的分區會在旁邊另外建一個"));
        Section("圖示",
            ("把圖示拖進分區", "放進分區（每個圖示只會在一個地方：分區或桌面）"),
            ("把圖示拖回桌面", "移出分區，放在放開的位置"),
            ("拖到資料夾 / 資源回收筒", "搬進去 / 刪除，跟 Windows 桌面一樣"),
            ("右鍵點圖示", "開啟、開啟方式、剪下、複製、刪除、重新命名、內容"),
            ("Ctrl / Shift + 點選、拖框框選", "一次選很多個，可以一起拖"),
            ("Delete / Shift+Delete", "刪除到資源回收筒 / 永久刪除"),
            ("Ctrl + C / X / V", "複製 / 剪下 / 貼上（可以跟檔案總管互相貼）"),
            ("F2（桌面上）", "重新命名圖示；Ctrl+Shift+N 新增資料夾"),
            ("Ctrl + Z", "復原上一個動作（也可以按右下角提示的「復原」）"),
            ("雙擊分頁標籤", "直接改分頁名稱"),
            ("滑鼠移到分頁標籤上", "出現 ✕，點一下刪除分頁"),
            ("拖曳分頁標籤", "調整分頁順序"));
        return Scroll(panel);
    }

    // ───────────── 小工具 ─────────────

    private static ScrollViewer Scroll(UIElement content) =>
        new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static CheckBox Toggle(string text, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            IsChecked = get(),
            Margin = new Thickness(0, 5, 0, 5),
        };
        check.Click += (_, _) =>
        {
            set(check.IsChecked == true);
            check.IsChecked = get();
        };
        return check;
    }

    private static Button Action(string text, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 6),
            Margin = new Thickness(0, 0, 8, 6),
            Cursor = Cursors.Hand,
        };
        if (primary)
        {
            Color accent = ThemeHelper.AccentColor;
            button.Background = new SolidColorBrush(accent);
            button.BorderBrush = new SolidColorBrush(accent);
            button.Foreground = Brushes.White;
        }

        button.Click += (_, _) => onClick();
        return button;
    }
}
