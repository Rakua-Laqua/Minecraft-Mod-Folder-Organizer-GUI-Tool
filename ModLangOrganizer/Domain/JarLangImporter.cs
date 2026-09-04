using System.IO;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>外部編集したlangファイルを対応するJARへ追加・更新する。</summary>
public sealed class JarLangImporter
{
    private readonly FileSystemService _fs = new();
    private readonly JarArchiveUpdater _updater = new();
    private readonly Logger _logger;

    public JarLangImporter(Logger logger)
    {
        _logger = logger;
    }

    public JarImportBatchPlan CreatePlan(
        IReadOnlyList<JarScanResult> scanResults,
        string outputRoot,
        WorkspaceMapping? mapping = null)
    {
        var batchPlan = new JarImportBatchPlan();
        var jarPlanMap = new Dictionary<string, JarImportPlan>(StringComparer.OrdinalIgnoreCase);

        foreach (var scan in scanResults)
        {
            var jarPlan = new JarImportPlan { ScanResult = scan };
            batchPlan.JarPlans.Add(jarPlan);
            jarPlanMap[scan.RelativeJarPath.Replace('\\', '/')] = jarPlan;
            jarPlanMap.TryAdd(scan.JarFileName, jarPlan);
        }

        // mapping がある場合は mapping を唯一の正解として計画を構築
        if (mapping != null && mapping.Entries.Count > 0)
        {
            foreach (var entry in mapping.Entries)
            {
                var normalizedJarPath = entry.JarRelativePath.Replace('\\', '/');
                if (!jarPlanMap.TryGetValue(normalizedJarPath, out var jarPlan) &&
                    !jarPlanMap.TryGetValue(Path.GetFileName(normalizedJarPath), out jarPlan))
                {
                    continue;
                }

                var scan = jarPlan.ScanResult;
                if (scan.Strategy == ProcessingStrategy.NoLang ||
                    scan.Integrity == JarIntegrity.Corrupted)
                {
                    continue;
                }

                var sourcePath = Path.GetFullPath(Path.Combine(outputRoot, entry.EditPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                if (IsConflictCopy(sourcePath))
                {
                    jarPlan.IgnoredConflictFiles.Add(sourcePath);
                    continue;
                }

                if (!IsSupportedLangFile(sourcePath))
                {
                    continue;
                }

                if (!jarPlan.Files.Any(f => f.ArchivePath.Equals(entry.ArchivePath, StringComparison.OrdinalIgnoreCase)))
                {
                    jarPlan.Files.Add(new JarImportFile(sourcePath, entry.ArchivePath));
                }
            }

            return batchPlan;
        }

        // フォールバック: mapping がない場合は従来のフォルダ探索
        foreach (var scan in scanResults)
        {
            var jarPlan = jarPlanMap[scan.RelativeJarPath.Replace('\\', '/')];
            if (scan.Strategy == ProcessingStrategy.NoLang ||
                scan.Integrity == JarIntegrity.Corrupted)
            {
                continue;
            }

            foreach (var candidate in scan.LangCandidates)
            {
                try
                {
                    var sourceDirectory = LangPathResolver.GetExternalLangDirectory(
                        outputRoot,
                        scan,
                        candidate);

                    if (!Directory.Exists(sourceDirectory))
                    {
                        jarPlan.MissingSourceDirectories.Add(sourceDirectory);
                        continue;
                    }

                    // lang直下の翻訳ファイルだけを反映する。
                    // サブディレクトリを再帰すると、旧出力の lang/ を assets/.../lang/lang/ として再投入してしまう。
                    foreach (var sourcePath in _fs.EnumerateFilesTopLevelNoFollow(sourceDirectory)
                                 .Where(IsSupportedLangFile)
                                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (IsConflictCopy(sourcePath))
                        {
                            jarPlan.IgnoredConflictFiles.Add(sourcePath);
                            continue;
                        }

                        var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                        var archivePath = LangPathResolver.BuildArchivePath(candidate, relativePath);
                        if (!jarPlan.Files.Any(f => f.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            jarPlan.Files.Add(new JarImportFile(sourcePath, archivePath));
                        }
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    jarPlan.PlanningErrors.Add($"{candidate.ModId}: {ex.Message}");
                }
            }
        }

        return batchPlan;
    }

    public Task<ExecutionResult> ImportAsync(
        JarImportBatchPlan batchPlan,
        Models.Options options,
        IProgress<ExecutionProgress> progress,
        CancellationToken ct)
    {
        var result = new ExecutionResult();
        var total = batchPlan.JarPlans.Count;

        for (var index = 0; index < total; index++)
        {
            var plan = batchPlan.JarPlans[index];
            var scan = plan.ScanResult;
            var current = index + 1;

            ct.ThrowIfCancellationRequested();
            progress.Report(new ExecutionProgress(
                Index: index,
                Current: current,
                Total: total,
                JarName: scan.JarFileName,
                Stage: ExecutionProgressStage.Started));

            if (scan.Strategy == ProcessingStrategy.NoLang ||
                scan.Integrity == JarIntegrity.Corrupted ||
                plan.Files.Count == 0)
            {
                result.SkipCount++;
                LogSkippedPlan(plan);
                progress.Report(new ExecutionProgress(
                    Index: index,
                    Current: current,
                    Total: total,
                    JarName: scan.JarFileName,
                    Stage: ExecutionProgressStage.Completed,
                    FinalStatus: ModStatus.Skipped));
                continue;
            }

            var hasWarning = false;
            var finalStatus = ModStatus.Success;

            try
            {
                foreach (var missingDirectory in plan.MissingSourceDirectories)
                {
                    hasWarning = true;
                    _logger.Warn($"反映元フォルダなし: {missingDirectory}");
                }

                foreach (var planningError in plan.PlanningErrors)
                {
                    hasWarning = true;
                    _logger.Warn($"反映計画エラー: {scan.JarFileName} - {planningError}");
                }

                if (plan.IgnoredConflictFiles.Count > 0)
                {
                    _logger.Info(
                        $"競合コピーをJAR反映から除外: {scan.JarFileName} " +
                        $"({plan.IgnoredConflictFiles.Count}ファイル)");
                }

                _logger.Info($"JAR反映開始: {scan.JarFileName} ({plan.Files.Count}ファイル)");
                var update = _updater.Update(
                    scan.JarFilePath,
                    plan.Files,
                    options.CancelGranularity == CancelGranularity.PerFile,
                    ct);

                result.AddedFileCount += update.AddedCount;
                result.UpdatedFileCount += update.UpdatedCount;
                result.UnchangedFileCount += update.UnchangedCount;

                if (scan.HasSignature && (update.AddedCount > 0 || update.UpdatedCount > 0))
                {
                    hasWarning = true;
                    _logger.Warn($"署名付きJARを更新しました。署名検証に影響する可能性があります: {scan.JarFileName}");
                }

                if (update.AddedCount == 0 && update.UpdatedCount == 0)
                {
                    result.UnchangedJarCount++;
                    _logger.Info($"JAR変更なし: {scan.JarFileName} (同一内容 {update.UnchangedCount})");
                }
                else
                {
                    _logger.Info(
                        $"JAR反映完了: {scan.JarFileName} " +
                        $"(追加 {update.AddedCount}, 更新 {update.UpdatedCount}, 同一 {update.UnchangedCount})");
                }

                if (hasWarning)
                {
                    result.WarningCount++;
                    finalStatus = ModStatus.Warning;
                }
                else
                {
                    result.SuccessCount++;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"JAR反映キャンセル: {scan.JarFileName}");
                throw;
            }
            catch (SignedJarModificationBlockedException ex)
            {
                result.FailCount++;
                result.Errors.Add($"{scan.JarFileName}: 署名保護によりスキップ ({string.Join(", ", ex.BlockedEntries)})");
                _logger.Warn($"[署名保護] {ex.Message} → 改ざん検知によるゲームクラッシュを防ぐためスキップしました。リソースパック機能のご利用を推奨します。");
                finalStatus = ModStatus.Warning;
            }
            catch (Exception ex)
            {
                result.FailCount++;
                result.Errors.Add($"{scan.JarFileName}: {ex.Message}");
                _logger.Error($"JAR反映失敗: {scan.JarFileName} - {ex.Message}");
                finalStatus = ModStatus.Failed;
            }

            progress.Report(new ExecutionProgress(
                Index: index,
                Current: current,
                Total: total,
                JarName: scan.JarFileName,
                Stage: ExecutionProgressStage.Completed,
                FinalStatus: finalStatus));

            if (options.CancelGranularity == CancelGranularity.PerJar)
                ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult(result);
    }

    private void LogSkippedPlan(JarImportPlan plan)
    {
        var scan = plan.ScanResult;
        if (scan.Integrity == JarIntegrity.Corrupted)
        {
            _logger.Warn($"JAR反映スキップ: {scan.JarFileName} (破損)");
            return;
        }

        if (scan.Strategy == ProcessingStrategy.NoLang)
        {
            _logger.Info($"JAR反映スキップ: {scan.JarFileName} (langなし)");
            return;
        }

        foreach (var missingDirectory in plan.MissingSourceDirectories)
            _logger.Warn($"反映元フォルダなし: {missingDirectory}");
        foreach (var planningError in plan.PlanningErrors)
            _logger.Warn($"反映計画エラー: {scan.JarFileName} - {planningError}");
        if (plan.IgnoredConflictFiles.Count > 0)
            _logger.Info($"競合コピーをJAR反映から除外: {scan.JarFileName} ({plan.IgnoredConflictFiles.Count}ファイル)");

        _logger.Info($"JAR反映スキップ: {scan.JarFileName} (反映するファイルなし)");
    }

    private static bool IsConflictCopy(string path)
    {
        return Path.GetFileNameWithoutExtension(path)
            .Contains(".conflict.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedLangFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase);
}
