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
        WorkspaceMapping? mapping = null)
    {
        var result = new ExecutionResult();
        int total = scanResults.Count;

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
                    var outLang = LangPathResolver.ResolveEditDirectory(outputRoot, scan, candidate, mapping, scanResults);
                    var logLangPath = LangPathResolver.GetDisplayPath(scan, candidate);

                    if (!Directory.Exists(srcLang))
                    {
                        _logger.Warn($"lang未検出(展開後): {logLangPath} in {scan.JarFileName}");
                        continue;
                    }

                    _fs.EnsureDir(outLang);
                    _logger.Info($"ディレクトリ作成: {outLang}");

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
        var parentDir = Path.GetDirectoryName(targetDir) ?? targetDir;
        var dirName = Path.GetFileName(targetDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(parentDir, $"{dirName}_backup_{timestamp}.zip");

        _logger.Info($"バックアップ作成開始: {zipPath}");

        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(targetDir, zipPath);
        }, ct);

        _logger.Info($"バックアップ完了: {zipPath}");
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

    private static void RegisterMappingEntry(
        WorkspaceMapping mapping,
        string outputRoot,
        string fullFilePath,
        string jarRelativePath,
        string modId,
        string archivePath)
    {
        var editRelativePath = Path.GetRelativePath(outputRoot, fullFilePath).Replace('\\', '/');
        var existing = mapping.Entries.FirstOrDefault(e =>
            e.EditPath.Equals(editRelativePath, StringComparison.OrdinalIgnoreCase) ||
            (e.JarRelativePath.Equals(jarRelativePath, StringComparison.OrdinalIgnoreCase) &&
             e.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase)));

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
