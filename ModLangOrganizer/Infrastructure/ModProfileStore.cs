using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>MOD構成プロファイルをユーザー領域へ永続化する。</summary>
public sealed class ModProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string StoreDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModLangOrganizer");

    private static string StorePath => Path.Combine(StoreDirectory, "deploy-profiles.json");

    public IReadOnlyList<ModProfileModel> Load()
    {
        if (!File.Exists(StorePath))
            return Array.Empty<ModProfileModel>();

        try
        {
            var json = File.ReadAllText(StorePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, StoredProfile>>(json, JsonOptions)
                       ?? new Dictionary<string, StoredProfile>();

            return data
                .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => new ModProfileModel
                {
                    Name = pair.Key,
                    SelectedRelativePaths = pair.Value.SelectedRelPaths ?? new List<string>(),
                    Description = pair.Value.Description ?? string.Empty,
                    UpdatedAt = pair.Value.UpdatedAt ?? string.Empty
                })
                .ToList();
        }
        catch (JsonException)
        {
            BackupBrokenFile();
            return Array.Empty<ModProfileModel>();
        }
        catch (IOException)
        {
            return Array.Empty<ModProfileModel>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<ModProfileModel>();
        }
    }

    public void SaveAll(IEnumerable<ModProfileModel> profiles)
    {
        Directory.CreateDirectory(StoreDirectory);

        var data = profiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Name))
            .GroupBy(profile => profile.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.First().Name.Trim(),
                group =>
                {
                    var profile = group.Last();
                    return new StoredProfile
                    {
                        SelectedRelPaths = profile.SelectedRelativePaths
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                        Description = profile.Description,
                        UpdatedAt = profile.UpdatedAt
                    };
                },
                StringComparer.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        WriteAtomic(StorePath, json);
    }

    private static void WriteAtomic(string path, string content)
    {
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
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
            File.Move(StorePath, Path.Combine(StoreDirectory, $"deploy-profiles.broken.{timestamp}.json"), overwrite: true);
        }
        catch
        {
            // 壊れたプロファイルを退避できなくても、空状態で継続する。
        }
    }

    private sealed class StoredProfile
    {
        public List<string>? SelectedRelPaths { get; set; }
        public string? Description { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
