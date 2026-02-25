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
    private readonly ConflictResolver _conflict = new();
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
        CancellationToken ct)
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
            var finalStatus = ModStatus.Success;

            try
            {
                // 1. Extract jar
                workDir = _extractor.DetermineWorkDir(scan.JarFilePath);
                _logger.Info($"展開開始: {scan.JarFileName} -> {workDir}");
                _extractor.ExtractSecure(scan.JarFilePath, workDir, ct);

                // 2. Copy lang files
                foreach (var candidate in scan.LangCandidates)
                {
                    if (options.CancelGranularity == CancelGranularity.PerFile)
                        ct.ThrowIfCancellationRequested();

                    var srcLang = Path.Combine(workDir, "assets", candidate.ModId, "lang");
                    var outLang = Path.Combine(outputRoot, candidate.ModId, "lang");

                    if (!Directory.Exists(srcLang))
                    {
                        _logger.Warn($"lang未検出(展開後): {candidate.ModId} in {scan.JarFileName}");
                        continue;
                    }

                    _fs.EnsureDir(outLang);
                    _logger.Info($"ディレクトリ作成: {outLang}");

                    var files = _fs.EnumerateFilesNoFollow(srcLang).ToList();
                    foreach (var srcFile in files)
                    {
                        if (options.CancelGranularity == CancelGranularity.PerFile)
                            ct.ThrowIfCancellationRequested();

                        var relativePath = Path.GetRelativePath(srcLang, srcFile);
                        var destPath = Path.Combine(outLang, relativePath);

                        try
                        {
                            if (File.Exists(destPath))
                            {
                                if (_fs.IsSameContent(srcFile, destPath))
                                {
                                    _logger.Info($"同一内容のためスキップ: {candidate.ModId}/lang/{relativePath}");
                                }
                                else
                                {
                                    var sourceTag = Path.GetFileNameWithoutExtension(scan.JarFileName);
                                    var conflictName = _conflict.BuildConflictName(
                                        Path.GetFileName(destPath), sourceTag, Path.GetDirectoryName(destPath)!);
                                    var conflictPath = Path.Combine(Path.GetDirectoryName(destPath)!, conflictName);
                                    _fs.CopyFile(srcFile, conflictPath);
                                    _logger.Warn($"競合コピー: {candidate.ModId}/lang/{conflictName}");
                                }
                            }
                            else
                            {
                                _fs.CopyFile(srcFile, destPath);
                                _logger.Info($"コピー: {candidate.ModId}/lang/{relativePath}");
                            }
                        }
                        catch (Exception ex)
                        {
                            allCopySuccess = false;
                            _logger.Error($"コピー失敗: {candidate.ModId}/lang/{relativePath} - {ex.Message}");
                        }
                    }

                    // Fallback: ターゲットファイルが無ければソースからコピー生成
                    if (options.LangFallbackEnabled &&
                        !string.IsNullOrWhiteSpace(options.LangFallbackSourceName) &&
                        !string.IsNullOrWhiteSpace(options.LangFallbackTargetName))
                    {
                        try
                        {
                            ApplyLangFallback(outLang, options, candidate.ModId);
                        }
                        catch (Exception ex)
                        {
                            allCopySuccess = false;
                            _logger.Error($"フォールバックコピー失敗: {candidate.ModId}/lang - {ex.Message}");
                        }
                    }
                }

                if (allCopySuccess)
                {
                    result.SuccessCount++;
                    _logger.Info($"成功: {scan.JarFileName}");
                }
                else
                {
                    result.WarningCount++;
                    _logger.Warn($"一部失敗あり: {scan.JarFileName}");
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
    private void ApplyLangFallback(string outLangDir, Models.Options options, string modId)
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
            _logger.Info($"フォールバック: ソース '{sourceName}' が見つかりません: {modId}/lang");
            return;
        }

        foreach (var srcFile in sourceFiles)
        {
            var ext = Path.GetExtension(srcFile); // 例: .json
            var targetFileName = targetName + ext;
            var targetPath = Path.Combine(outLangDir, targetFileName);

            if (File.Exists(targetPath))
            {
                _logger.Info($"フォールバック不要: {modId}/lang/{targetFileName} は既に存在します");
                continue;
            }

            _fs.CopyFile(srcFile, targetPath);
            _logger.Info($"フォールバックコピー: {modId}/lang/{Path.GetFileName(srcFile)} → {targetFileName}");
        }
    }
}
