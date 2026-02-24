using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>Persistent store for application settings.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModLangOrganizer");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            BackupBrokenSettingsFile();
            return new AppSettings();
        }
        catch (NotSupportedException)
        {
            BackupBrokenSettingsFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = SettingsPath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));

            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, SettingsPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static void BackupBrokenSettingsFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            Directory.CreateDirectory(SettingsDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var brokenPath = Path.Combine(SettingsDir, $"settings.broken.{timestamp}.json");
            File.Move(SettingsPath, brokenPath, overwrite: true);
        }
        catch
        {
            // Ignore backup failures and continue with defaults.
        }
    }
}