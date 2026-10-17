using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyFences.Models;

namespace MyFences.Services;

internal static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ConfigDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyFences");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return AppConfig.CreateDefault();

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? AppConfig.CreateDefault();
        }
        catch (Exception)
        {
            // 設定檔壞掉：備份起來再用預設值，避免直接覆蓋掉使用者的資料
            string backup = ConfigPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(ConfigPath, backup, overwrite: true);
            return AppConfig.CreateDefault();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(ConfigDirectory);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, Options));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
