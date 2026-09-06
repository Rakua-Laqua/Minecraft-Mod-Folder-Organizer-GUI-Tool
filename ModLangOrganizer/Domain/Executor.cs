using System.IO;
using System.IO.Compression;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>Execution engine: extract jar, copy lang files, then cleanup temp.</summary>
public sealed class Executor
{
    private readonly ArchiveExtractor _extractor = new();
    private readonly FileSystemService _fs = new();
    private readonly LangFileMerger _langMerger = new();
    private readonly Logger _logger;

    public Executor(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>Execute per scan result and report per-jar progress.</summary>
    public Task<ExecutionResult> ExecuteAsync(
        List<JarScanResult> scanResults,
        string outputRoot,
        Models.Options options,
        IProgress<ExecutionProgress> progress,
        CancellationToken ct,
        WorkspaceMapping? mapping = null,
        IReadOnlyList<JarScanResult>? allScans = null)
    {
        var result = new ExecutionResult();
        int total = scanResults.Count;
        var collisionScans = allScans ?? scanResults;

        for (int index = 0; index < scanResults.Count; index++)
        {
            var scan = scanResults[index];
            var current = index + 1;

            ct.ThrowIfCancellationRequested();
            progress.Report(new ExecutionProgress(
                Index: index,
                Current: current,
                Total: total,
                JarName: scan.JarFileName,
                Stage: ExecutionProgressStage.Started));

            if (scan.Strategy == ProcessingStrategy.NoLang || scan.Integrity == JarIntegrity.Corrupted)
            {
                result.SkipCount++;
                _logger.Info($"スキップ: {scan.JarFileName} ({(scan.Integrity == JarIntegrity.Corrupted ? "破損" : "langなし")})");
                progress.Report(new ExecutionProgress(
                    Index: index,
                    Current: current,
                    Total: total,
                    JarName: scan.JarFileName,
                    Stage: ExecutionProgressStage.Completed,
                    FinalStatus: ModStatus.Skipped));
                continue;
            }

            string? workDir = null;
            bool allCopySuccess = true;
            bool hasWarnings = false;
            var finalStatus = ModStatus.Success;

            try
            {
                // 1. Extract jar
                workDir = _extractor.DetermineWorkDir(scan.JarFilePath);
                _logger.Info($"展開開始: {scan.JarFileName} -> {workDir}");
                _extractor.ExtractSecure(scan.JarFilePath, workDir, ct);

                // 2. Copy / merge lang files
                foreach (var candidate in scan.LangCandidates)
                {
                    if (options.CancelGranularity == CancelGranularity.PerFile)
                        ct.ThrowIfCancellationRequested();

                    var archiveLangPath = candidate.ArchiveLangPath.Replace('/', Path.DirectorySeparatorChar);
                    var srcLang = Path.Combine(workDir, archiveLangPath);
                    var outLang = LangPathResolver.ResolveEditDirectory(outputRoot, scan, candidate, mapping, collisionScans);
                    var logLangPath = LangPathResolver.GetDisplayPath(scan, candidate);

                    if (!Directory.Exists(srcLang))
                    {
                        _logger.Warn($"lang未検出(展開後): {logLangPath} in {scan.JarFileName}");
                        continue;
                    }

                    _fs.EnsureDir(outLang);
                    _logger.Info($"ディレクトリ作成: {outLang}");

                    // 旧mapping先（ルート直下等）から既存翻訳ファイルを安全に引き継ぐ
                    MigrateLegacyMappingEntries(mapping, outputRoot, scan, candidate, outLang);

                    // 旧競合コピーはJAR由来の退避ファイルなので、新方式への移行時に整理する。
                    try
                    {
                        var removedConflictCopies = _langMerger.CleanupLegacyConflictCopies(outLang);
                        if (removedConflictCopies > 0)
                            _logger.Info($"旧競合コピー削除: {logLangPath} ({removedConflictCopies}件)");
                    }
                    catch (Exception ex)
                    {
                        hasWarnings = true;
                        _logger.Warn($"旧競合コピー削除失敗: {logLangPath} - {ex.Message}");
                    }

                    // スキャン時に確定したlang直下の翻訳ファイルだけを処理する。
                    // lang/lang等のネストやlang以外の補助ファイルは対象にしない。
                    foreach (var relativePath in candidate.Files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    {
                        if (options.CancelGranularity == CancelGranularity.PerFile)
                            ct.ThrowIfCancellationRequested();

                        var srcFile = Path.Combine(srcLang, relativePath);
                        var destPath = Path.Combine(outLang, relativePath);

                        try
                        {
                            if (!File.Exists(srcFile))
                                throw new FileNotFoundException("スキャン時に存在したlangファイルが展開後に見つかりません。", srcFile);

                            if (!File.Exists(destPath))
                            {
                                _fs.CopyFile(srcFile, destPath);
                                _logger.Info($"コピー: {logLangPath}/{relativePath}");
                            }
                            else if (_fs.IsSameContent(srcFile, destPath))
                            {
                                _logger.Info($"同一内容のためスキップ: {logLangPath}/{relativePath}");
                            }
                            else if (IsTranslationMergeTarget(relativePath, options.LangFallbackTargetName))
                            {
                                var mergeResult = _langMerger.MergeTargetFromJar(srcFile, destPath);

                                if (mergeResult.UsedFallbackOverwrite)
                                {
                                    hasWarnings = true;
                                    _logger.Warn(
                                        $"翻訳マージ不可のためJAR内容で上書き: {logLangPath}/{relativePath} - " +
                                        mergeResult.Warning);
                                }
                                else if (mergeResult.WasMerged)
                                {
                                    _logger.Info(
                                        $"翻訳マージ: {logLangPath}/{relativePath} " +
                                        $"(既存値維持:{mergeResult.PreservedKeys}, 新規キー:{mergeResult.AddedKeys}, " +
                                        $"削除キー:{mergeResult.RemovedKeys}, JAR行数:{mergeResult.SourceLineCount})");

                                    if (mergeResult.SourceLineCount != mergeResult.OutputLineCount)
                                    {
                                        hasWarnings = true;
                                        _logger.Warn(
                                            $"行構成検証不一致: {logLangPath}/{relativePath} " +
                                            $"(JAR:{mergeResult.SourceLineCount}, 出力:{mergeResult.OutputLineCount})");
                                    }
                                }
                                else
                                {
                                    _logger.Info($"JAR内容で上書き: {logLangPath}/{relativePath}");
                                }
                            }
                            else
                            {
                                // 翻訳ターゲット以外はJAR側を正として上書きする。
                                // これによりMOD更新時の原文・他言語ファイルを古い抽出内容のまま残さない。
                                _langMerger.OverwriteFromJar(srcFile, destPath);
                                _logger.Info($"JAR内容で上書き: {logLangPath}/{relativePath}");
                            }

                            if (mapping != null)
                            {
                                var archivePath = LangPathResolver.BuildArchivePath(candidate, relativePath);
                                RegisterMappingEntry(mapping, outputRoot, destPath, scan.RelativeJarPath, candidate.ModId, archivePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            allCopySuccess = false;
                            _logger.Error($"コピー/マージ失敗: {logLangPath}/{relativePath} - {ex.Message}");
                        }
                    }

                    // Fallback: ターゲットファイルが無ければソースからコピー生成
                    if (options.LangFallbackEnabled &&
                        !string.IsNullOrWhiteSpace(options.LangFallbackSourceName) &&
                        !string.IsNullOrWhiteSpace(options.LangFallbackTargetName))
                    {
                        try
                        {
                            ApplyLangFallback(outLang, options, logLangPath, candidate, scan, outputRoot, mapping);
                        }
                        catch (Exception ex)
                        {
                            allCopySuccess = false;
                            _logger.Error($"フォールバックコピー失敗: {logLangPath} - {ex.Message}");
                        }
                    }

                    // 既存翻訳ファイルの救済登録:
                    // 編集フォルダ直下に既に存在する .json / .lang で mapping 未登録のものを救済登録する（ファイル内容は変更しない）
                    if (mapping != null && Directory.Exists(outLang))
                    {
                        try
                        {
                            var existingFiles = Directory.GetFiles(outLang);
                            foreach (var filePath in existingFiles)
                            {
                                var ext = Path.GetExtension(filePath);
                                if (!ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                                    !ext.Equals(".lang", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var fileName = Path.GetFileName(filePath);
                                var archivePath = LangPathResolver.BuildArchivePath(candidate, fileName);
                                var editRelativePath = Path.GetRelativePath(outputRoot, filePath).Replace('\\', '/');

                                var isMapped = mapping.Entries.Any(e =>
                                    e.EditPath.Equals(editRelativePath, StringComparison.OrdinalIgnoreCase) ||
                                    (e.JarRelativePath.Equals(scan.RelativeJarPath, StringComparison.OrdinalIgnoreCase) &&
                                     e.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase)));

                                if (!isMapped)
                                {
                                    RegisterMappingEntry(mapping, outputRoot, filePath, scan.RelativeJarPath, candidate.ModId, archivePath);
                                    _logger.Info($"未登録既存翻訳ファイルをmappingに追加: {editRelativePath} → {archivePath} (ModId: {candidate.ModId})");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn($"既存翻訳ファイル救済スキャン失敗: {outLang} - {ex.Message}");
                        }
                    }
                }

                if (allCopySuccess && !hasWarnings)
                {
                    result.SuccessCount++;
                    _logger.Info($"成功: {scan.JarFileName}");
                }
                else
                {
                    result.WarningCount++;
                    _logger.Warn($"警告あり: {scan.JarFileName}");
                    finalStatus = ModStatus.Warning;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"キャンセル: {scan.JarFileName}");
                throw;
            }
            catch (Exception ex)
            {
                result.FailCount++;
                result.Errors.Add($"{scan.JarFileName}: {ex.Message}");
                _logger.Error($"失敗: {scan.JarFileName} - {ex.Message}");
                finalStatus = ModStatus.Failed;
            }
            finally
            {
                if (workDir != null && Directory.Exists(workDir))
                {
                    try
                    {
                        _fs.DeleteRecursiveNoFollow(workDir);
                        _logger.Info($"クリーンアップ完了: {scan.JarFileName}");
                    }
                    catch (Exception ex)
                    {
                        result.CleanupFailCount++;
                        _logger.Warn($"クリーンアップ失敗: {scan.JarFileName} - {ex.Message}");
                    }
                }
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

    /// <summary>Create zip backup before execution.</summary>
    public async Task CreateBackupAsync(string targetDir, CancellationToken ct)
    {
        var normalized = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = Path.GetDirectoryName(normalized) ?? normalized;
        var dirName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(dirName))
            dirName = "backup";

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var unique = Guid.NewGuid().ToString("N");
        var zipName = $"{dirName}_backup_{timestamp}_{unique}.zip";
        var zipPath = Path.Combine(parentDir, zipName);

        if (JarPathPolicy.IsSameOrUnder(zipPath, normalized))
            zipPath = Path.Combine(Path.GetTempPath(), zipName);

        _logger.Info($"バックアップ作成開始: {zipPath}");

        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(normalized, zipPath);
        }, ct);

        _logger.Info($"バックアップ完了: {zipPath}");
    }

    /// <summary>
    /// 抽出前に TargetDir と存在する OutputRoot を包含関係に応じてバックアップする。
    /// すべての必要バックアップが完了してから戻る。
    /// </summary>
    public async Task CreateExtractionBackupAsync(string targetDir, string outputRoot, CancellationToken ct)
    {
        foreach (var root in ResolveExtractionBackupRoots(targetDir, outputRoot))
            await CreateBackupAsync(root, ct);
    }

    private static List<string> ResolveExtractionBackupRoots(string targetDir, string outputRoot)
    {
        string? NormalizeExisting(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return null;

            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var target = NormalizeExisting(targetDir);
        var output = NormalizeExisting(outputRoot);

        if (target is null && output is null)
            return [];
        if (target is null)
            return [output!];
        if (output is null)
            return [target];

        if (JarPathPolicy.IsSameOrUnder(output, target))
            return [target];
        if (JarPathPolicy.IsSameOrUnder(target, output))
            return [output];

        return [target, output];
    }

    /// <summary>
    /// 出力lang内にターゲットファイルが存在しなければソースファイルからコピーして生成する。
    /// 例: ja_jp.json が無い場合、en_us.json → ja_jp.json としてコピー。
    /// </summary>
    private void ApplyLangFallback(
        string outLangDir,
        Models.Options options,
        string langLogPath,
        LangCandidate candidate,
        JarScanResult scan,
        string outputRoot,
        WorkspaceMapping? mapping)
    {
        if (!Directory.Exists(outLangDir))
            return;

        var sourceName = options.LangFallbackSourceName.Trim();
        var targetName = options.LangFallbackTargetName.Trim();

        // outLangDir 直下のファイルについてチェック（再帰なし：lang直下のみ対象）
        var allFiles = Directory.GetFiles(outLangDir);

        // ソースファイルを拡張子込みで探す（例: en_us.json, en_us.lang）
        var sourceFiles = allFiles
            .Where(f => Path.GetFileNameWithoutExtension(f)
                .Equals(sourceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sourceFiles.Count == 0)
        {
            _logger.Info($"フォールバック: ソース '{sourceName}' が見つかりません: {langLogPath}");
            return;
        }

        foreach (var srcFile in sourceFiles)
        {
            var ext = Path.GetExtension(srcFile); // 例: .json
            var targetFileName = targetName + ext;
            var targetPath = Path.Combine(outLangDir, targetFileName);

            if (File.Exists(targetPath))
            {
                _logger.Info($"フォールバック不要: {langLogPath}/{targetFileName} は既に存在します");

                if (mapping != null)
                {
                    var archivePath = LangPathResolver.BuildArchivePath(candidate, targetFileName);
                    RegisterMappingEntry(mapping, outputRoot, targetPath, scan.RelativeJarPath, candidate.ModId, archivePath);
                }
                continue;
            }

            _fs.CopyFile(srcFile, targetPath);
            _logger.Info($"フォールバックコピー: {langLogPath}/{Path.GetFileName(srcFile)} → {targetFileName}");

            if (mapping != null)
            {
                var archivePath = LangPathResolver.BuildArchivePath(candidate, targetFileName);
                RegisterMappingEntry(mapping, outputRoot, targetPath, scan.RelativeJarPath, candidate.ModId, archivePath);
            }
        }
    }

    /// <summary>
    /// 旧抽出フォルダ（ルート直下等）に存在する既存mappingの翻訳ファイルを、
    /// 新しい正規のJAR単位フォルダへ安全に引き継ぎ（1ファイルずつコピー）、mappingのEditPathを新配置へ更新する。
    /// 旧ディレクトリ内のファイル全コピーではなく、対象 candidate の mapping entry だけを根拠とする。
    /// </summary>
    private void MigrateLegacyMappingEntries(
        WorkspaceMapping? mapping,
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate,
        string newOutLangDir)
    {
        if (mapping == null)
            return;

        var fullOutputRoot = Path.GetFullPath(outputRoot);
        var normalizedNewOutLang = Path.GetFullPath(newOutLangDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var archivePrefix = candidate.ArchiveLangPath.TrimEnd('/') + "/";

        var matchedEntries = mapping.Entries.Where(e =>
            e.JarRelativePath.Equals(scan.RelativeJarPath, StringComparison.OrdinalIgnoreCase) &&
            e.ModId.Equals(candidate.ModId, StringComparison.OrdinalIgnoreCase) &&
            e.ArchivePath.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(e.EditPath)).ToList();

        foreach (var entry in matchedEntries)
        {
            var oldEditFullPath = Path.GetFullPath(
                Path.Combine(fullOutputRoot, entry.EditPath.Replace('/', Path.DirectorySeparatorChar)));
            var fileName = Path.GetFileName(oldEditFullPath);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var newEditFullPath = Path.GetFullPath(Path.Combine(normalizedNewOutLang, fileName));

            if (oldEditFullPath.Equals(newEditFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(oldEditFullPath))
                continue;

            if (File.Exists(newEditFullPath))
            {
                _logger.Warn($"新配置に既にファイルが存在するため旧ファイルの引き継ぎをスキップ: {entry.EditPath} (新配置: {newEditFullPath})");
                var existingEditRelative = Path.GetRelativePath(fullOutputRoot, newEditFullPath).Replace('\\', '/');
                entry.EditPath = existingEditRelative;
                entry.LastUpdated = DateTimeOffset.Now;
                continue;
            }

            try
            {
                _fs.CopyFile(oldEditFullPath, newEditFullPath);

                if (File.Exists(newEditFullPath))
                {
                    var newEditRelativePath = Path.GetRelativePath(fullOutputRoot, newEditFullPath).Replace('\\', '/');
                    _logger.Info($"旧mapping先から既存翻訳を引き継ぎ: {entry.EditPath} → {newEditRelativePath}");
                    entry.EditPath = newEditRelativePath;
                    entry.LastUpdated = DateTimeOffset.Now;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"既存翻訳の引き継ぎ失敗: {entry.EditPath} → {newEditFullPath}: {ex.Message}");
            }
        }
    }

    private static void RegisterMappingEntry(
        WorkspaceMapping mapping,
        string outputRoot,
        string fullFilePath,
        string jarRelativePath,
        string modId,
        string archivePath)
    {
        var editRelativePath = Path.GetRelativePath(outputRoot, fullFilePath).Replace('\\', '/');
        var byEditPath = mapping.Entries.FirstOrDefault(e =>
            e.EditPath.Equals(editRelativePath, StringComparison.OrdinalIgnoreCase));

        if (byEditPath != null)
        {
            var sameJar = byEditPath.JarRelativePath.Equals(jarRelativePath, StringComparison.OrdinalIgnoreCase);
            var sameArchive = byEditPath.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase);
            if (!sameJar || !sameArchive)
            {
                throw new InvalidDataException(
                    $"mappingのEditPathは別のJAR/アーカイブに割り当て済みのため付け替えできません: {editRelativePath}");
            }
        }

        var existing = byEditPath ?? mapping.Entries.FirstOrDefault(e =>
            e.JarRelativePath.Equals(jarRelativePath, StringComparison.OrdinalIgnoreCase) &&
            e.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.EditPath = editRelativePath;
            existing.JarRelativePath = jarRelativePath;
            existing.ModId = modId;
            existing.ArchivePath = archivePath;
            existing.LastUpdated = DateTimeOffset.Now;
        }
        else
        {
            mapping.Entries.Add(new TranslationMappingEntry
            {
                EditPath = editRelativePath,
                JarRelativePath = jarRelativePath,
                ModId = modId,
                ArchivePath = archivePath,
                LastUpdated = DateTimeOffset.Now
            });
        }
    }

    private static bool IsTranslationMergeTarget(string relativePath, string configuredTargetName)
    {
        if (string.IsNullOrWhiteSpace(configuredTargetName))
            return false;

        var extension = Path.GetExtension(relativePath);
        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".lang", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetFileNameWithoutExtension(relativePath)
            .Equals(configuredTargetName.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
