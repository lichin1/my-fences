using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyFences.Models;
using MyFences.Services;

namespace MyFences.Views;

/// <summary>
/// 圖像化的規則編輯器：每一組條件是一張卡片（組和組之間是「或」，卡片內的條件是「而且」），
/// 不用背規則語法。
/// </summary>
internal sealed class RuleEditorWindow : Window
{
    private static readonly (RuleConditionKind Kind, string Label, string? Unit, string Placeholder)[] Kinds =
    {
        (RuleConditionKind.Extension, "副檔名是", null, "例如 pdf，可以填好幾個：jpg png"),
        (RuleConditionKind.NameContains, "檔名包含", null, "例如 發票（可用 * 萬用字元）"),
        (RuleConditionKind.Folder, "是資料夾", null, ""),
        (RuleConditionKind.AnyFile, "任何檔案", null, ""),
        (RuleConditionKind.NewerThanDays, "最近幾天內修改過", "天", "7"),
        (RuleConditionKind.OlderThanDays, "超過幾天沒修改", "天", "30"),
        (RuleConditionKind.LargerThanMb, "檔案大於", "MB", "100"),
        (RuleConditionKind.SmallerThanMb, "檔案小於", "MB", "1"),
    };

    private static readonly (string Label, RuleCondition Condition)[] Presets =
    {
        ("捷徑", new(RuleConditionKind.Extension, "lnk url")),
        ("資料夾", new(RuleConditionKind.Folder)),
        ("文件", new(RuleConditionKind.Extension, "pdf doc docx xls xlsx csv ppt pptx txt md")),
        ("圖片", new(RuleConditionKind.Extension, "jpg jpeg png gif webp bmp heic")),
        ("影片", new(RuleConditionKind.Extension, "mp4 mov mkv avi")),
        ("壓縮檔", new(RuleConditionKind.Extension, "zip rar 7z")),
        ("安裝檔", new(RuleConditionKind.Extension, "exe msi")),
    };

    private static readonly (string Label, int Value)[] Priorities = { ("高（先比對）", 1), ("一般", 0), ("低（最後比對）", -1) };

    private readonly FenceConfig _fence;
    private readonly List<List<EditCondition>> _groups;
    private readonly StackPanel _groupsPanel = new();
    private readonly TextBlock _preview = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ComboBox _priority = new() { Width = 140 };
    private readonly List<string> _desktopItems = DesktopHelper.EnumerateDesktopItems().ToList();

    private RuleEditorWindow(FenceConfig fence)
    {
        _fence = fence;
        _groups = FenceRules.GroupForEditing(fence.Rules)
            .Select(g => g.Select(c => new EditCondition { Kind = c.Kind, Value = c.Value }).ToList())
            .ToList();

        Title = $"「{fence.Name}」的自動整理規則";
        Width = 600;
        Height = 620;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xF9, 0xF9));

        var root = new DockPanel { Margin = new Thickness(20) };

        // 上方說明 + 一鍵加入常用規則
        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = "桌面上符合下面任一組條件的檔案，會自動放進這個分區。",
            TextWrapping = TextWrapping.Wrap,
        });
        top.Children.Add(new TextBlock { Text = "快速加入：", Margin = new Thickness(0, 12, 0, 6), Foreground = Brushes.DimGray });
        var presets = new WrapPanel();
        foreach ((string label, RuleCondition condition) in Presets)
        {
            Button chip = MakeButton("＋ " + label, subtle: true);
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Click += (_, _) =>
            {
                _groups.Add(new List<EditCondition> { new() { Kind = condition.Kind, Value = condition.Value } });
                Rebuild();
            };
            presets.Children.Add(chip);
        }

        top.Children.Add(presets);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // 下方：優先順序、預覽、按鈕
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var priorityRow = new StackPanel { Orientation = Orientation.Horizontal };
        priorityRow.Children.Add(new TextBlock { Text = "同一個檔案符合好幾個分區時，這個分區的優先順序：", VerticalAlignment = VerticalAlignment.Center });
        foreach ((string label, int value) in Priorities)
            _priority.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _priority.SelectedIndex = Array.FindIndex(Priorities, p => p.Value == fence.RulePriority) is var i and >= 0 ? i : 1;
        priorityRow.Children.Add(_priority);
        bottom.Children.Add(priorityRow);
        bottom.Children.Add(_preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button save = MakeButton("儲存並整理桌面", primary: true);
        save.IsDefault = true;
        save.Click += (_, _) => Save();
        Button cancel = MakeButton("取消");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // 中間：條件組卡片
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 6, 0, 0),
            Content = _groupsPanel,
        };
        root.Children.Add(scroll);

        Content = root;
        Rebuild();
    }

    /// <summary>開啟編輯器；按「儲存」回傳 true。</summary>
    public static bool Edit(FenceConfig fence) => new RuleEditorWindow(fence).ShowDialog() == true;

    private void Rebuild()
    {
        _groupsPanel.Children.Clear();
        for (int g = 0; g < _groups.Count; g++)
        {
            if (g > 0)
            {
                _groupsPanel.Children.Add(new TextBlock
                {
                    Text = "或",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }

            _groupsPanel.Children.Add(BuildGroupCard(_groups[g]));
        }

        Button addGroup = MakeButton(_groups.Count == 0 ? "＋ 新增條件" : "＋ 再加一組條件（或）", subtle: true);
        addGroup.HorizontalAlignment = HorizontalAlignment.Left;
        addGroup.Margin = new Thickness(0, 10, 0, 0);
        addGroup.Click += (_, _) =>
        {
            _groups.Add(new List<EditCondition> { new() { Kind = RuleConditionKind.Extension } });
            Rebuild();
        };
        _groupsPanel.Children.Add(addGroup);
        UpdatePreview();
    }

    private Border BuildGroupCard(List<EditCondition> group)
    {
        var panel = new StackPanel();
        for (int c = 0; c < group.Count; c++)
        {
            EditCondition condition = group[c];
            var row = new DockPanel { Margin = new Thickness(0, c == 0 ? 0 : 6, 0, 0) };

            var prefix = new TextBlock
            {
                Text = c == 0 ? "如果" : "而且",
                Width = 40,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
            };
            row.Children.Add(prefix);

            Button remove = MakeButton("✕", subtle: true);
            remove.ToolTip = "刪除這個條件";
            remove.Padding = new Thickness(8, 2, 8, 3);
            remove.Click += (_, _) =>
            {
                group.Remove(condition);
                if (group.Count == 0)
                    _groups.Remove(group);
                Rebuild();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var kind = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var k in Kinds)
                kind.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Kind });
            kind.SelectedIndex = Array.FindIndex(Kinds, k => k.Kind == condition.Kind);
            row.Children.Add(kind);

            var meta = Kinds.First(k => k.Kind == condition.Kind);
            if (FenceRules.NeedsValue(condition.Kind))
            {
                if (meta.Unit != null)
                {
                    var unit = new TextBlock { Text = meta.Unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
                    DockPanel.SetDock(unit, Dock.Right);
                    row.Children.Add(unit);
                }

                var value = new TextBox { Text = condition.Value, Padding = new Thickness(4, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center };
                value.ToolTip = meta.Placeholder;
                if (condition.Value.Length == 0)
                    value.Loaded += (_, _) => value.Focus();
                value.TextChanged += (_, _) =>
                {
                    condition.Value = value.Text;
                    UpdatePreview();
                };
                row.Children.Add(value);
            }

            kind.SelectionChanged += (_, _) =>
            {
                if (kind.SelectedItem is ComboBoxItem { Tag: RuleConditionKind k })
                {
                    condition.Kind = k;
                    condition.Value = "";
                    Rebuild();
                }
            };

            panel.Children.Add(row);
        }

        Button addAnd = MakeButton("＋ 而且…", subtle: true);
        addAnd.HorizontalAlignment = HorizontalAlignment.Left;
        addAnd.Margin = new Thickness(40, 8, 0, 0);
        addAnd.Click += (_, _) =>
        {
            group.Add(new EditCondition { Kind = RuleConditionKind.NameContains });
            Rebuild();
        };
        panel.Children.Add(addAnd);

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = panel,
        };
    }

    /// <summary>把所有卡片轉成規則字串；有錯誤時回傳 null 並說明是哪一組。</summary>
    private List<string>? BuildRules(out string? error)
    {
        error = null;
        var rules = new List<string>();
        for (int g = 0; g < _groups.Count; g++)
        {
            var conditions = _groups[g].Select(c => new RuleCondition(c.Kind, c.Value)).ToList();
            if (FenceRules.ExpandGroup(conditions) is not { } expanded)
            {
                error = $"第 {g + 1} 組條件有空白或格式不對的值（數字要是 0 以上，檔名裡不能有 +）。";
                return null;
            }

            rules.AddRange(expanded.Where(r => !rules.Contains(r)));
        }

        return rules;
    }

    private void UpdatePreview()
    {
        List<string>? rules = BuildRules(out string? error);
        if (rules == null)
        {
            _preview.Text = "⚠ " + error;
            _preview.Foreground = Brushes.Firebrick;
            return;
        }

        _preview.Foreground = Brushes.DimGray;
        if (rules.Count == 0)
        {
            _preview.Text = "沒有規則：這個分區不會自動放入檔案。";
            return;
        }

        int count = _desktopItems.Count(p => FenceRules.Matches(rules, p));
        _preview.Text = $"目前桌面上有 {count} 個項目符合（已經在其他分區裡的不會被搬動）。";
    }

    private void Save()
    {
        if (BuildRules(out string? error) is not { } rules)
        {
            MessageBox.Show(this, error, "MyFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _fence.Rules = rules;
        if (_priority.SelectedItem is ComboBoxItem { Tag: int priority })
            _fence.RulePriority = priority;
        DialogResult = true;
    }

    private static Button MakeButton(string text, bool primary = false, bool subtle = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 5, 14, 6),
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1),
        };

        if (primary)
        {
            Color accent = ThemeHelper.AccentColor;
            button.Background = new SolidColorBrush(accent);
            button.BorderBrush = new SolidColorBrush(accent);
            button.Foreground = Brushes.White;
        }
        else if (subtle)
        {
            button.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        }

        return button;
    }

    private sealed class EditCondition
    {
        public RuleConditionKind Kind { get; set; }
        public string Value { get; set; } = "";
    }
}
