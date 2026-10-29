using System.Text.Json;
using AITranslator.Models;

namespace AITranslator.Services;

/// <summary>以 JSON 讀寫設定檔；檔案不存在或內容損壞一律落回預設值。</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }
}