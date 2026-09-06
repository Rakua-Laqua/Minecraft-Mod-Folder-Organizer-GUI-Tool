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
        }

        // mapping が無い・空の場合は推測反映を行わず安全に終了する（mappingを唯一の正解とする）
        if (mapping == null || mapping.Entries.Count == 0)
        {
            _logger.Warn("有効な言語マッピング (mapping.json) が存在しないため、JAR反映計画の生成をスキップしました。先に［langを抽出］を実行してください。");
            return batchPlan;
        }

        var normalizedOutputRoot = Path.GetFullPath(outputRoot);

        foreach (var entry in mapping.Entries)
        {
            var normalizedJarPath = entry.JarRelativePath.Replace('\\', '/');
            // 相対JARパスの完全一致のみ受け付ける（同名JAR誤爆防止）
            if (!jarPlanMap.TryGetValue(normalizedJarPath, out var jarPlan))
            {
                continue;
            }

            var scan = jarPlan.ScanResult;
            if (scan.Strategy == ProcessingStrategy.NoLang ||
                scan.Integrity == JarIntegrity.Corrupted)
            {
                continue;
            }

            // 1. EditPath のパストラバーサル検証
            var combinedPath = Path.Combine(normalizedOutputRoot, entry.EditPath.Replace('/', Path.DirectorySeparatorChar));
            var fullSourcePath = Path.GetFullPath(combinedPath);
            if (!fullSourcePath.StartsWith(normalizedOutputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !fullSourcePath.Equals(normalizedOutputRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn($"マッピングのEditPathが出力ルート外を指しているため除外: {entry.EditPath}");
                continue;
            }

            if (!File.Exists(fullSourcePath))
            {
                continue;
            }

            // 2. 拡張子検証 (.json / .lang)
            if (!IsSupportedLangFile(fullSourcePath))
            {
                _logger.Warn($"非対応の拡張子のためJAR反映から除外: {entry.EditPath}");
                continue;
            }

            // 3. 競合コピーの除外
            if (IsConflictCopy(fullSourcePath))
            {
                jarPlan.IgnoredConflictFiles.Add(fullSourcePath);
                continue;
            }

            // 4. ArchivePath の危険パス拒否（相対 .. / . 、絶対、ドライブ/ADS、バックスラッシュ）
            if (!TryGetSafeArchivePath(entry.ArchivePath, out var normalizedArchivePath))
            {
                _logger.Warn($"マッピングのArchivePathが不正または危険なため除外: {entry.ArchivePath}");
                continue;
            }

            // 5. ArchivePath の厳格検証（対象JARで検出済みの LangCandidate かつ ModId が一致し、その ArchiveLangPath 配下に収まっているか）
            var isValidArchiveLang = scan.LangCandidates.Any(candidate =>
            {
                if (!string.IsNullOrWhiteSpace(entry.ModId) &&
                    !candidate.ModId.Equals(entry.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var candidateLangRoot = candidate.ArchiveLangPath.TrimStart('/').TrimEnd('/') + "/";
                return normalizedArchivePath.StartsWith(candidateLangRoot, StringComparison.OrdinalIgnoreCase);
            });

            if (!isValidArchiveLang)
            {
                _logger.Warn($"マッピングのArchivePathがJAR内の検出langパスと一致しないため除外: {scan.RelativeJarPath} -> {entry.ArchivePath}");
                continue;
            }

            // 6. 反映リストに追加（重複排除）
            if (!jarPlan.Files.Any(f => f.ArchivePath.Equals(normalizedArchivePath, StringComparison.OrdinalIgnoreCase)))
            {
                jarPlan.Files.Add(new JarImportFile(fullSourcePath, normalizedArchivePath));
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

    private static bool TryGetSafeArchivePath(string archivePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(archivePath))
            return false;

        if (archivePath.Contains('\\') ||
            archivePath.Contains(':') ||
            archivePath.Contains('\0') ||
            archivePath.StartsWith('/') ||
            Path.IsPathRooted(archivePath))
        {
            return false;
        }

        var parts = archivePath.Split('/', StringSplitOptions.None);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or "..")
                return false;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
        }

        normalized = string.Join('/', parts);
        return true;
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
