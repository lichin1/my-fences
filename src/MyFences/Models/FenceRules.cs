using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MyFences.Models;

/// <summary>
/// 自動整理規則。每條規則之間是「或」，同一條規則內用 + 連接的條件是「且」。
/// <code>
///   .pdf / pdf        副檔名
///   folder            資料夾
///   *                 所有檔案
///   name:報表          檔名包含「報表」（可用 * ? 萬用字元，例如 name:*2024*）
///   newer:7           7 天內修改過
///   older:30          超過 30 天沒修改
///   larger:100        大於 100 MB
///   smaller:1         小於 1 MB
///   pdf+name:發票      副檔名是 pdf 而且檔名包含「發票」
/// </code>
/// </summary>
public static class FenceRules
{
    /// <summary>Windows 檔名不能用的字元（固定寫死，測試在 Linux 上跑結果也一樣）。</summary>
    private static readonly char[] InvalidExtensionChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    public static bool Matches(IEnumerable<string> rules, string path, DateTime? now = null)
    {
        var item = new ItemInfo(path, now ?? DateTime.Now);
        foreach (string rule in rules)
        {
            if (MatchesRule(rule, item))
                return true;
        }

        return false;
    }

    /// <summary>檢查規則語法，回傳看不懂的條件（全部正確時回傳空清單）。</summary>
    public static List<string> FindInvalidConditions(IEnumerable<string> rules)
    {
        var invalid = new List<string>();
        foreach (string rule in rules)
        {
            foreach (string condition in SplitConditions(rule))
            {
                if (!IsValidCondition(condition))
                    invalid.Add(condition);
            }
        }

        return invalid;
    }

    // ───────────── 給圖像化編輯器用：規則字串 ⇄ 條件清單 ─────────────

    /// <summary>把一條規則（例如 "pdf+name:發票"）拆成條件；看不懂的條件會略過。</summary>
    public static List<RuleCondition> ParseRule(string rule)
    {
        var result = new List<RuleCondition>();
        foreach (string condition in SplitConditions(rule))
        {
            if (!IsValidCondition(condition))
                continue;

            (string key, string value) = SplitKey(condition);
            result.Add(key switch
            {
                "" when value == "folder" => new RuleCondition(RuleConditionKind.Folder),
                "" when value == "*" => new RuleCondition(RuleConditionKind.AnyFile),
                "" => new RuleCondition(RuleConditionKind.Extension, value.TrimStart('.')),
                "name" => new RuleCondition(RuleConditionKind.NameContains, value),
                "newer" => new RuleCondition(RuleConditionKind.NewerThanDays, value),
                "older" => new RuleCondition(RuleConditionKind.OlderThanDays, value),
                "larger" => new RuleCondition(RuleConditionKind.LargerThanMb, value),
                _ => new RuleCondition(RuleConditionKind.SmallerThanMb, value),
            });
        }

        return result;
    }

    /// <summary>把條件組回規則字串；值不合法的條件回傳 null（給編輯器顯示錯誤）。</summary>
    public static string? FormatRule(IEnumerable<RuleCondition> conditions)
    {
        var parts = new List<string>();
        foreach (RuleCondition c in conditions)
        {
            string value = c.Value.Trim();
            string part = c.Kind switch
            {
                RuleConditionKind.Folder => "folder",
                RuleConditionKind.AnyFile => "*",
                RuleConditionKind.Extension => value.TrimStart('.').ToLowerInvariant(),
                RuleConditionKind.NameContains => "name:" + value,
                RuleConditionKind.NewerThanDays => "newer:" + value,
                RuleConditionKind.OlderThanDays => "older:" + value,
                RuleConditionKind.LargerThanMb => "larger:" + value,
                _ => "smaller:" + value,
            };

            // + 是「而且」的分隔符號，值裡面不能有
            if (value.Contains('+') || !IsValidCondition(part) || (c.Kind == RuleConditionKind.Extension && value.TrimStart('.').Length == 0))
                return null;
            parts.Add(part);
        }

        return parts.Count == 0 ? null : string.Join("+", parts);
    }

    /// <summary>
    /// 編輯器的一組條件 → 規則字串。副檔名可以一次填好幾個（用空白或逗號分隔，例如 "jpg png"），
    /// 會展開成多條規則（jpg、png 各一條，其他條件照樣「而且」）。有不合法的值時回傳 null。
    /// </summary>
    public static List<string>? ExpandGroup(IReadOnlyList<RuleCondition> group)
    {
        var variants = new List<List<RuleCondition>> { new() };
        foreach (RuleCondition c in group)
        {
            string[] values = c.Kind == RuleConditionKind.Extension
                ? c.Value.Split(new[] { ' ', ',', '，', '、', ';' }, StringSplitOptions.RemoveEmptyEntries)
                : new[] { c.Value };
            if (values.Length == 0)
                return null;

            variants = variants
                .SelectMany(v => values.Select(value => new List<RuleCondition>(v) { c with { Value = value } }))
                .ToList();
        }

        var rules = new List<string>();
        foreach (List<RuleCondition> variant in variants)
        {
            if (FormatRule(variant) is not { } rule)
                return null;
            if (!rules.Contains(rule))
                rules.Add(rule);
        }

        return rules;
    }

    /// <summary>
    /// 規則字串 → 編輯器用的條件組。只有一個副檔名的規則會合併成一組（例如 lnk、url → 「副檔名是 lnk url」），
    /// 其他規則各自一組。
    /// </summary>
    public static List<List<RuleCondition>> GroupForEditing(IEnumerable<string> rules)
    {
        var groups = new List<List<RuleCondition>>();
        var extensions = new List<string>();
        int extensionGroupIndex = -1;
        foreach (string rule in rules)
        {
            List<RuleCondition> conditions = ParseRule(rule);
            if (conditions.Count == 0)
                continue;

            if (conditions is [{ Kind: RuleConditionKind.Extension } only])
            {
                if (extensionGroupIndex < 0)
                {
                    extensionGroupIndex = groups.Count;
                    groups.Add(new List<RuleCondition>());
                }

                if (!extensions.Contains(only.Value))
                    extensions.Add(only.Value);
                continue;
            }

            groups.Add(conditions);
        }

        if (extensionGroupIndex >= 0)
            groups[extensionGroupIndex] = new List<RuleCondition> { new(RuleConditionKind.Extension, string.Join(" ", extensions)) };
        return groups;
    }

    public static bool NeedsValue(RuleConditionKind kind) =>
        kind is not (RuleConditionKind.Folder or RuleConditionKind.AnyFile);

    /// <summary>條件的中文說明，例如「副檔名是 pdf」。</summary>
    public static string Describe(RuleCondition c) => c.Kind switch
    {
        RuleConditionKind.Folder => "是資料夾",
        RuleConditionKind.AnyFile => "任何檔案",
        RuleConditionKind.Extension => $"副檔名是 {c.Value}",
        RuleConditionKind.NameContains => $"檔名包含「{c.Value}」",
        RuleConditionKind.NewerThanDays => $"{c.Value} 天內修改過",
        RuleConditionKind.OlderThanDays => $"超過 {c.Value} 天沒修改",
        RuleConditionKind.LargerThanMb => $"大於 {c.Value} MB",
        _ => $"小於 {c.Value} MB",
    };

    private static bool MatchesRule(string rule, ItemInfo item)
    {
        bool any = false;
        foreach (string condition in SplitConditions(rule))
        {
            any = true;
            if (!MatchesCondition(condition, item))
                return false;
        }

        return any;
    }

    private static IEnumerable<string> SplitConditions(string rule) =>
        rule.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsValidCondition(string condition)
    {
        (string key, string value) = SplitKey(condition);
        return key switch
        {
            // * 在 Windows 是不合法的檔名字元，要先當成特殊規則放行
            "" => value is "*" or "folder" || (value.Length > 0 && value.IndexOfAny(InvalidExtensionChars) < 0),
            "name" => value.Length > 0,
            "newer" or "older" or "larger" or "smaller" => TryParseNumber(value, out _),
            _ => false,
        };
    }

    private static bool MatchesCondition(string condition, ItemInfo item)
    {
        (string key, string value) = SplitKey(condition);
        switch (key)
        {
            case "":
                if (value == "folder")
                    return item.IsDirectory;
                if (item.IsDirectory)
                    return false;
                if (value == "*")
                    return true;
                string ext = value.StartsWith('.') ? value : "." + value;
                return string.Equals(item.Extension, ext, StringComparison.OrdinalIgnoreCase);

            case "name":
                return MatchesName(item.Name, value);

            case "newer":
                return TryParseNumber(value, out double newerDays) && item.Modified is { } m1 && (item.Now - m1).TotalDays <= newerDays;

            case "older":
                return TryParseNumber(value, out double olderDays) && item.Modified is { } m2 && (item.Now - m2).TotalDays > olderDays;

            case "larger":
                return TryParseNumber(value, out double largerMb) && item.SizeBytes is { } s1 && s1 > largerMb * 1024 * 1024;

            case "smaller":
                return TryParseNumber(value, out double smallerMb) && item.SizeBytes is { } s2 && s2 < smallerMb * 1024 * 1024;

            default:
                return false;
        }
    }

    private static (string Key, string Value) SplitKey(string condition)
    {
        string text = condition.Trim();
        int colon = text.IndexOf(':');
        if (colon <= 0)
            return ("", text.ToLowerInvariant());

        return (text[..colon].Trim().ToLowerInvariant(), text[(colon + 1)..].Trim());
    }

    private static bool MatchesName(string name, string pattern)
    {
        if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
            return name.Contains(pattern, StringComparison.CurrentCultureIgnoreCase);

        string regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number >= 0;

    /// <summary>檔案資訊只在用到時才讀，避免每條規則都碰一次磁碟。</summary>
    private sealed class ItemInfo
    {
        private readonly string _path;
        private bool _loaded;
        private DateTime? _modified;
        private long? _size;

        public ItemInfo(string path, DateTime now)
        {
            _path = path;
            Now = now;
            IsDirectory = Directory.Exists(path);
            Extension = Path.GetExtension(path);
            Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        public DateTime Now { get; }
        public bool IsDirectory { get; }
        public string Extension { get; }
        public string Name { get; }

        public DateTime? Modified
        {
            get
            {
                Load();
                return _modified;
            }
        }

        /// <summary>資料夾沒有大小（不會符合 larger / smaller）。</summary>
        public long? SizeBytes
        {
            get
            {
                Load();
                return _size;
            }
        }

        private void Load()
        {
            if (_loaded)
                return;

            _loaded = true;
            try
            {
                if (IsDirectory)
                {
                    _modified = Directory.GetLastWriteTime(_path);
                }
                else if (File.Exists(_path))
                {
                    var info = new FileInfo(_path);
                    _modified = info.LastWriteTime;
                    _size = info.Length;
                }
            }
            catch (Exception)
            {
                // 讀不到就當作不符合
            }
        }
    }
}

public enum RuleConditionKind
{
    Extension,
    Folder,
    AnyFile,
    NameContains,
    NewerThanDays,
    OlderThanDays,
    LargerThanMb,
    SmallerThanMb,
}

/// <summary>規則裡的一個條件（圖像化編輯器的一列）。</summary>
public sealed record RuleCondition(RuleConditionKind Kind, string Value = "");
