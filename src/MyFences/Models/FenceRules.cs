using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
            "" => value.Length > 0 && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
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
