using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>Copy Manager画面の設定と最後の選択状態を永続化する。</summary>
public sealed class ModDeploySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string StoreDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModLangOrganizer");

    private static string StorePath => Path.Combine(StoreDirectory, "deploy-settings.json");

    public DeploySettingsModel Load()
    {
        if (!File.Exists(StorePath))
            return new DeploySettingsModel();

        try
        {
            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<DeploySettingsModel>(json, JsonOptions) ?? new DeploySettingsModel();
        }
        catch (JsonException)
        {
            BackupBrokenFile();
            return new DeploySettingsModel();
        }
        catch (IOException)
        {
            return new DeploySettingsModel();
        }
        catch (UnauthorizedAccessException)
        {
            return new DeploySettingsModel();
        }
    }

    public void Save(DeploySettingsModel settings)
    {
        Directory.CreateDirectory(StoreDirectory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = StorePath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(StorePath))
                File.Replace(tempPath, StorePath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, StorePath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static void BackupBrokenFile()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;

            Directory.CreateDirectory(StoreDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.Move(StorePath, Path.Combine(StoreDirectory, $"deploy-settings.broken.{timestamp}.json"), overwrite: true);
        }
        catch
        {
            // 設定退避に失敗しても既定値で継続する。
        }
    }
}
