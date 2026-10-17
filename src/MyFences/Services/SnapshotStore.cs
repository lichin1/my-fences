using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyFences.Models;

namespace MyFences.Services;

public enum SnapshotKind
{
    /// <summary>使用者手動建立，不會自動刪除。</summary>
    Manual,

    /// <summary>每天自動建立。</summary>
    Daily,

    /// <summary>整理桌面、還原快照之前自動備份。</summary>
    Backup,
}

public sealed class Snapshot
{
    public string Name { get; set; } = "";
    public SnapshotKind Kind { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<FenceConfig> Fences { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; set; } = "";
}

/// <summary>快照：整份分區設定（位置、項目、規則、外觀）的備份，存在設定資料夾的 snapshots 底下。</summary>
internal static class SnapshotStore
{
    private const int KeepAutoSnapshotDays = 14;

    public static string SnapshotDirectory { get; } = Path.Combine(ConfigStore.ConfigDirectory, "snapshots");

    public static Snapshot Create(AppConfig config, string name, SnapshotKind kind)
    {
        // 用 JSON 來回轉一次當作深層複製，之後改設定不會影響快照
        var fences = JsonSerializer.Deserialize<List<FenceConfig>>(
            JsonSerializer.Serialize(config.Fences, ConfigStore.JsonOptions), ConfigStore.JsonOptions) ?? new();

        var snapshot = new Snapshot { Name = name, Kind = kind, CreatedAt = DateTime.Now, Fences = fences };
        Directory.CreateDirectory(SnapshotDirectory);
        snapshot.FilePath = Path.Combine(SnapshotDirectory, snapshot.CreatedAt.ToString("yyyyMMdd-HHmmss-fff") + ".json");
        File.WriteAllText(snapshot.FilePath, JsonSerializer.Serialize(snapshot, ConfigStore.JsonOptions));
        Prune();
        return snapshot;
    }

    /// <summary>新的在前。讀不懂的檔案會略過。</summary>
    public static List<Snapshot> List()
    {
        var result = new List<Snapshot>();
        if (!Directory.Exists(SnapshotDirectory))
            return result;

        foreach (string file in Directory.EnumerateFiles(SnapshotDirectory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), ConfigStore.JsonOptions) is { } snapshot)
                {
                    snapshot.FilePath = file;
                    result.Add(snapshot);
                }
            }
            catch (Exception)
            {
            }
        }

        return result.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <summary>今天還沒有每日快照就建立一份。</summary>
    public static void EnsureDailySnapshot(AppConfig config)
    {
        if (List().Any(s => s.Kind == SnapshotKind.Daily && s.CreatedAt.Date == DateTime.Today))
            return;

        Create(config, "每日自動快照", SnapshotKind.Daily);
    }

    public static void Delete(Snapshot snapshot)
    {
        if (File.Exists(snapshot.FilePath))
            File.Delete(snapshot.FilePath);
    }

    /// <summary>自動建立的快照只保留 14 天；手動的永遠保留。</summary>
    private static void Prune()
    {
        DateTime cutoff = DateTime.Now.AddDays(-KeepAutoSnapshotDays);
        foreach (Snapshot snapshot in List().Where(s => s.Kind != SnapshotKind.Manual && s.CreatedAt < cutoff))
        {
            try
            {
                Delete(snapshot);
            }
            catch (Exception)
            {
            }
        }
    }
}
