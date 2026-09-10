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

    private readonly Action<string, string> replaceManifest;

    public ModDeploymentService()
        : this((source, target) => File.Replace(source, target, null, ignoreMetadataErrors: true))
    {
    }

    internal ModDeploymentService(Action<string, string> replaceManifest)
    {
        this.replaceManifest = replaceManifest ?? throw new ArgumentNullException(nameof(replaceManifest));
    }

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

            if (FilesHaveSameContent(mod.FullPath, target.FullPath))
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
        string? transactionDirectory = null;
        var originals = new List<(string Target, string Saved)>();
        var installedFiles = new List<string>();
        (string Target, string Saved)? manifestToRestore = null;
        var preserveTransaction = false;

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
            Directory.CreateDirectory(targetDirectory);
            var plan = CalculateDiff(selectedMods, targetDirectory, mode);
            var nextManaged = mode == DeployMode.Sync
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : LoadManifest(targetDirectory);
            nextManaged.UnionWith(selectedMods.Select(mod => mod.Name));
            var totalOperations = Math.Max(1, plan.TotalChanges);
            var currentOperation = 0;

            void Report(string message)
            {
                // A progress observer must not turn a committed deployment into a failure.
                try
                {
                    progress?.Report(new DeployProgressModel
                    {
                        Current = currentOperation,
                        Total = totalOperations,
                        Message = message
                    });
                }
                catch (Exception ex)
                {
                    Log($"進捗通知に失敗しました: {ex.Message}");
                }
            }

            Report("デプロイ準備中...");
            // Keep staging on the target volume so applying and restoring use renames.
            transactionDirectory = Path.Combine(targetDirectory, $".modmanager-transaction-{Guid.NewGuid():N}");
            var stagedDirectory = Path.Combine(transactionDirectory, "staged");
            var originalDirectory = Path.Combine(transactionDirectory, "originals");
            Directory.CreateDirectory(stagedDirectory);
            Directory.CreateDirectory(originalDirectory);
            foreach (var mod in plan.ToAdd.Concat(plan.ToUpdate))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report($"コピー準備: {mod.Name}");
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(mod.FullPath, Path.Combine(stagedDirectory, mod.Name));
            }
            SaveManifest(transactionDirectory, nextManaged);

            if (createBackup && (plan.ToDelete.Count > 0 || plan.ToUpdate.Count > 0))
            {
                backupPath = Path.Combine(targetDirectory, BackupDirectoryName,
                    $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}");
                Directory.CreateDirectory(backupPath);
                foreach (var path in plan.ToDelete.Select(item => item.FullPath)
                    .Concat(plan.ToUpdate.Select(mod => Path.Combine(targetDirectory, mod.Name))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(path, Path.Combine(backupPath, Path.GetFileName(path)));
                }
                Log($"バックアップを作成しました: {backupPath}");
            }

            void SaveOriginal(string target)
            {
                var saved = Path.Combine(originalDirectory, Path.GetFileName(target));
                File.Move(target, saved);
                originals.Add((target, saved));
            }

            void Install(ModTreeNodeModel mod)
            {
                var target = Path.Combine(targetDirectory, mod.Name);
                File.Move(Path.Combine(stagedDirectory, mod.Name), target);
                installedFiles.Add(target);
            }

            foreach (var target in plan.ToDelete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentOperation++;
                Report($"削除中: {target.FileName}");
                cancellationToken.ThrowIfCancellationRequested();
                SaveOriginal(target.FullPath);
                deletedCount++;
                Log($"削除 (未選択・管理対象のみ): {target.FileName}");
            }
            foreach (var mod in plan.ToAdd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentOperation++;
                Report($"新規コピー: {mod.Name}");
                cancellationToken.ThrowIfCancellationRequested();
                Install(mod);
                addedCount++;
                Log($"新規コピー: {mod.Name}");
            }
            foreach (var mod in plan.ToUpdate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentOperation++;
                Report($"更新コピー: {mod.Name}");
                cancellationToken.ThrowIfCancellationRequested();
                SaveOriginal(Path.Combine(targetDirectory, mod.Name));
                Install(mod);
                updatedCount++;
                Log($"更新コピー: {mod.Name}");
            }

            // The manifest is the final commit point. ReplaceFileW can remove the old
            // destination even on failure, so keep an independent copy for rollback.
            Report("管理情報を保存中...");
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(targetDirectory, ManifestFileName);
            var stagedManifest = Path.Combine(transactionDirectory, ManifestFileName);
            if (File.Exists(manifestPath))
            {
                var savedManifest = Path.Combine(originalDirectory, ManifestFileName);
                File.Copy(manifestPath, savedManifest);
                manifestToRestore = (manifestPath, savedManifest);
                replaceManifest(stagedManifest, manifestPath);
            }
            else
                File.Move(stagedManifest, manifestPath);

            Log($"管理情報を更新しました。変更なし: {plan.ToKeep.Count} 個のMOD");
            currentOperation = totalOperations;
            Report("デプロイ完了");
            Log($"完了: 追加 {addedCount} / 更新 {updatedCount} / 削除 {deletedCount}");
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
        catch (Exception ex)
        {
            // Rollback ignores cancellation and attempts every restoration even if one fails.
            var rollbackErrors = new List<string>();
            foreach (var path in installedFiles.AsEnumerable().Reverse())
            {
                try { File.Delete(path); }
                catch (Exception rollbackEx) { rollbackErrors.Add($"{path}: {rollbackEx.Message}"); }
            }
            foreach (var (target, saved) in originals.AsEnumerable().Reverse())
            {
                try { File.Move(saved, target); }
                catch (Exception rollbackEx) { rollbackErrors.Add($"{target}: {rollbackEx.Message}"); }
            }

            if (manifestToRestore is { } manifest)
            {
                // Copy rather than consume the backup: failed restoration must leave
                // the exact original bytes available for manual recovery.
                try { File.Copy(manifest.Saved, manifest.Target, overwrite: true); }
                catch (Exception rollbackEx) { rollbackErrors.Add($"{manifest.Target}: {rollbackEx.Message}"); }
            }

            var error = ex is OperationCanceledException
                ? "デプロイがキャンセルされました。"
                : ex.Message;
            if (rollbackErrors.Count == 0)
            {
                addedCount = updatedCount = deletedCount = 0;
                Log("配置先と管理情報は変更前の状態です。");
            }
            else
            {
                preserveTransaction = true;
                backupPath = transactionDirectory;
                error += $" 自動復元に失敗しました。復旧用データを保持しています: {transactionDirectory}";
                foreach (var rollbackError in rollbackErrors)
                    Log($"復元失敗: {rollbackError}");
            }
            Log(error);
            return new DeployExecutionResult
            {
                Success = false,
                AddedCount = addedCount,
                UpdatedCount = updatedCount,
                DeletedCount = deletedCount,
                BackupPath = backupPath,
                ErrorMessage = error,
                Logs = logs
            };
        }
        finally
        {
            if (!preserveTransaction && transactionDirectory != null)
            {
                try
                {
                    if (Directory.Exists(transactionDirectory))
                        Directory.Delete(transactionDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    Log($"作業フォルダを削除できませんでした: {transactionDirectory} ({ex.Message})");
                }
            }
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

    private static bool FilesHaveSameContent(string sourcePath, string targetPath)
    {
        try
        {
            return new FileSystemService().IsSameContent(sourcePath, targetPath);
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
