using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>
/// 選択したMODをMinecraftのmodsフォルダへ安全に同期/追加する。
/// 同期時の削除対象は、このツールが .modmanager.json に記録したファイルだけに限定する。
/// </summary>
public sealed class ModDeploymentService
{
    public const string ManifestFileName = ".modmanager.json";
    public const string BackupDirectoryName = ".modmanager_backup";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public IReadOnlyList<DeployConflictModel> DetectConflicts(IEnumerable<ModTreeNodeModel> selectedMods)
    {
        return selectedMods
            .Where(mod => mod.IsFile)
            .GroupBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DeployConflictModel
            {
                FileName = group.First().Name,
                Paths = group.Select(mod => mod.FullPath).ToList()
            })
            .ToList();
    }

    public DeployPlanModel CalculateDiff(
        IReadOnlyCollection<ModTreeNodeModel> selectedMods,
        string modsDirectory,
        DeployMode mode)
    {
        var conflicts = DetectConflicts(selectedMods);
        if (conflicts.Count > 0)
            throw new InvalidOperationException("同名のMODが複数選択されています。衝突を解消してください。");

        var plan = new DeployPlanModel();
        var targetDirectory = NormalizeDirectory(modsDirectory);

        if (!Directory.Exists(targetDirectory))
        {
            plan.ToAdd.AddRange(selectedMods);
            return plan;
        }

        var managedFileNames = LoadManifest(targetDirectory);
        var selectedByName = selectedMods.ToDictionary(mod => mod.Name, StringComparer.OrdinalIgnoreCase);
        var existingFiles = EnumerateJarFiles(targetDirectory)
            .ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in selectedMods)
        {
            if (!existingFiles.TryGetValue(mod.Name, out var target))
            {
                plan.ToAdd.Add(mod);
                continue;
            }

            if (FilesAppearIdentical(mod.FullPath, target.FullPath))
                plan.ToKeep.Add(mod);
            else
                plan.ToUpdate.Add(mod);
        }

        if (mode == DeployMode.Sync)
        {
            foreach (var managedName in managedFileNames)
            {
                if (!selectedByName.ContainsKey(managedName) && existingFiles.TryGetValue(managedName, out var target))
                    plan.ToDelete.Add(target);
            }
        }

        foreach (var target in existingFiles.Values)
        {
            if (!managedFileNames.Contains(target.FileName) && !selectedByName.ContainsKey(target.FileName))
                plan.UnmanagedFiles.Add(target);
        }

        plan.ToDelete.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName));
        plan.UnmanagedFiles.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName));
        return plan;
    }

    public Task<DeployExecutionResult> ExecuteAsync(
        IReadOnlyCollection<ModTreeNodeModel> selectedMods,
        string modsDirectory,
        DeployMode mode,
        bool createBackup,
        IProgress<DeployProgressModel>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => Execute(selectedMods, modsDirectory, mode, createBackup, progress, cancellationToken),
            cancellationToken);
    }

    private DeployExecutionResult Execute(
        IReadOnlyCollection<ModTreeNodeModel> selectedMods,
        string modsDirectory,
        DeployMode mode,
        bool createBackup,
        IProgress<DeployProgressModel>? progress,
        CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        var addedCount = 0;
        var updatedCount = 0;
        var deletedCount = 0;
        string? backupPath = null;

        void Log(string message) => logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var mod in selectedMods)
            {
                if (string.IsNullOrWhiteSpace(mod.FullPath) || !File.Exists(mod.FullPath))
                    throw new FileNotFoundException($"選択されたMODが見つかりません: {mod.Name}", mod.FullPath);
            }

            var targetDirectory = NormalizeDirectory(modsDirectory);
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
                Log($"modsフォルダを作成しました: {targetDirectory}");
            }

            var plan = CalculateDiff(selectedMods, targetDirectory, mode);
            var totalOperations = Math.Max(1, plan.TotalChanges);
            var currentOperation = 0;

            void Report(string message)
            {
                progress?.Report(new DeployProgressModel
                {
                    Current = currentOperation,
                    Total = totalOperations,
                    Message = message
                });
            }

            Report("デプロイ準備中...");

            if (createBackup && (plan.ToDelete.Count > 0 || plan.ToUpdate.Count > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backupDirectory = Path.Combine(
                    targetDirectory,
                    BackupDirectoryName,
                    DateTime.Now.ToString("yyyyMMdd_HHmmss"));

                try
                {
                    Directory.CreateDirectory(backupDirectory);

                    foreach (var target in plan.ToDelete)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (File.Exists(target.FullPath))
                            File.Copy(target.FullPath, Path.Combine(backupDirectory, target.FileName), overwrite: true);
                    }

                    foreach (var mod in plan.ToUpdate)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var currentTarget = Path.Combine(targetDirectory, mod.Name);
                        if (File.Exists(currentTarget))
                            File.Copy(currentTarget, Path.Combine(backupDirectory, mod.Name), overwrite: true);
                    }

                    backupPath = backupDirectory;
                    Log($"バックアップを作成しました: {backupDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"バックアップの作成に失敗したため、反映を中止しました: {ex.Message}", ex);
                }
            }

            if (mode == DeployMode.Sync)
            {
                foreach (var target in plan.ToDelete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    currentOperation++;
                    Report($"削除中: {target.FileName}");
                    File.Delete(target.FullPath);
                    deletedCount++;
                    Log($"削除 (未選択・管理対象のみ): {target.FileName}");
                }
            }

            foreach (var mod in plan.ToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentOperation++;
                Report($"新規コピー: {mod.Name}");
                File.Copy(mod.FullPath, Path.Combine(targetDirectory, mod.Name), overwrite: true);
                addedCount++;
                Log($"新規コピー: {mod.Name}");
            }

            foreach (var mod in plan.ToUpdate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentOperation++;
                Report($"更新コピー: {mod.Name}");
                File.Copy(mod.FullPath, Path.Combine(targetDirectory, mod.Name), overwrite: true);
                updatedCount++;
                Log($"更新コピー: {mod.Name}");
            }

            if (plan.ToKeep.Count > 0)
                Log($"変更なし (スキップ): {plan.ToKeep.Count} 個のMOD");

            var currentManaged = LoadManifest(targetDirectory);
            HashSet<string> nextManaged;
            if (mode == DeployMode.Sync)
            {
                nextManaged = new HashSet<string>(selectedMods.Select(mod => mod.Name), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                nextManaged = new HashSet<string>(currentManaged, StringComparer.OrdinalIgnoreCase);
                nextManaged.UnionWith(selectedMods.Select(mod => mod.Name));
            }

            SaveManifest(targetDirectory, nextManaged);
            Log("マニフェスト (.modmanager.json) を更新しました");

            currentOperation = totalOperations;
            Report("デプロイ完了");
            Log($"完了: 追加 {addedCount} / 更新 {updatedCount} / 削除 {deletedCount} / 維持 {plan.ToKeep.Count}");

            return new DeployExecutionResult
            {
                Success = true,
                AddedCount = addedCount,
                UpdatedCount = updatedCount,
                DeletedCount = deletedCount,
                KeptCount = plan.ToKeep.Count,
                BackupPath = backupPath,
                Logs = logs
            };
        }
        catch (OperationCanceledException)
        {
            Log("デプロイをキャンセルしました。");
            return new DeployExecutionResult
            {
                Success = false,
                AddedCount = addedCount,
                UpdatedCount = updatedCount,
                DeletedCount = deletedCount,
                BackupPath = backupPath,
                ErrorMessage = "デプロイがキャンセルされました。",
                Logs = logs
            };
        }
        catch (Exception ex)
        {
            Log($"エラー: {ex.Message}");
            return new DeployExecutionResult
            {
                Success = false,
                AddedCount = addedCount,
                UpdatedCount = updatedCount,
                DeletedCount = deletedCount,
                BackupPath = backupPath,
                ErrorMessage = ex.Message,
                Logs = logs
            };
        }
    }

    public HashSet<string> LoadManifest(string modsDirectory)
    {
        var targetDirectory = NormalizeDirectory(modsDirectory);
        var manifestPath = Path.Combine(targetDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<ManifestModel>(json, ManifestJsonOptions);
            return new HashSet<string>(manifest?.ManagedFiles ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // 壊れたmanifestは「管理対象なし」として扱う。これにより手動MODを誤削除しない。
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveManifest(string modsDirectory, IEnumerable<string> managedFileNames)
    {
        var targetDirectory = NormalizeDirectory(modsDirectory);
        Directory.CreateDirectory(targetDirectory);

        var manifestPath = Path.Combine(targetDirectory, ManifestFileName);
        var manifest = new ManifestModel
        {
            Version = "1.0",
            LastSynced = DateTime.Now.ToString("O"),
            ManagedFiles = managedFileNames
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        var tempPath = manifestPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));
            if (File.Exists(manifestPath))
                File.Replace(tempPath, manifestPath, null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, manifestPath);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }
    }

    private static IReadOnlyList<ExistingModFileModel> EnumerateJarFiles(string targetDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(targetDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new ExistingModFileModel
                    {
                        FileName = info.Name,
                        FullPath = info.FullName,
                        SizeBytes = info.Exists ? info.Length : 0
                    };
                })
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<ExistingModFileModel>();
        }
    }

    private static bool FilesAppearIdentical(string sourcePath, string targetPath)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            var target = new FileInfo(targetPath);
            if (!source.Exists || !target.Exists)
                return false;

            var timeDifference = (source.LastWriteTimeUtc - target.LastWriteTimeUtc).Duration();
            return source.Length == target.Length && timeDifference < TimeSpan.FromSeconds(1);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("適用先modsフォルダを指定してください。", nameof(path));

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }

    private sealed class ManifestModel
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0";

        [JsonPropertyName("last_synced")]
        public string LastSynced { get; set; } = string.Empty;

        [JsonPropertyName("managed_files")]
        public List<string> ManagedFiles { get; set; } = new();
    }
}
