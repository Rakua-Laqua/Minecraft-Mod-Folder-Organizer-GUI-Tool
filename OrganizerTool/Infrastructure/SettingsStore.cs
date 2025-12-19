using System;
using System.IO;
using System.Text.Json;
using OrganizerTool.Models;

namespace OrganizerTool.Infrastructure;

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore(string appDirName = "MinecraftModFolderOrganizer")
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _settingsPath = Path.Combine(baseDir, appDirName, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public PersistedSettings LoadOrDefault()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return PersistedSettings.Default();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<PersistedSettings>(json);
            return settings ?? PersistedSettings.Default();
        }
        catch
        {
            return PersistedSettings.Default();
        }
    }

    public bool TrySave(PersistedSettings settings)
    {
        try
        {
            var parent = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            File.WriteAllText(_settingsPath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
