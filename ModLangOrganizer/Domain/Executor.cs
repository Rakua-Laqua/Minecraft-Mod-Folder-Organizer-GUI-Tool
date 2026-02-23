using System.IO;
using System.IO.Compression;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>実行エンジン（jar展開 + lang配置 + クリーンアップ）</summary>
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

    /// <summary>実行計画に基づいてlang抽出を実行する</summary>
    public Task<ExecutionResult> ExecuteAsync(
        List<JarScanResult> scanResults,
        string outputRoot,
        Models.Options options,
        IProgress<(int current, int total, string jarName)> progress,
        CancellationToken ct)
    {
        var result = new ExecutionResult();
        int processed = 0;
        int total = scanResults.Count;

        foreach (var scan in scanResults)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((processed, total, scan.JarFileName));

            if (scan.Strategy == ProcessingStrategy.NoLang || scan.Integrity == JarIntegrity.Corrupted)
            {
                result.SkipCount++;
                _logger.Info($"スキップ: {scan.JarFileName} ({(scan.Integrity == JarIntegrity.Corrupted ? "破損" : "langなし")})");
                processed++;
                continue;
            }

            string? workDir = null;
            bool allCopySuccess = true;

            try
            {
                // 1. jar展開
                workDir = _extractor.DetermineWorkDir(scan.JarFilePath);
                _logger.Info($"展開開始: {scan.JarFileName} → {workDir}");
                _extractor.ExtractSecure(scan.JarFilePath, workDir, ct);

                // 2. 各lang候補を処理
                foreach (var candidate in scan.LangCandidates)
                {
                    if (options.CancelGranularity == CancelGranularity.PerFile)
                        ct.ThrowIfCancellationRequested();

                    var srcLang = Path.Combine(workDir, "assets", candidate.ModId, "lang");
                    var outLang = Path.Combine(outputRoot, candidate.ModId, "lang");

                    if (!Directory.Exists(srcLang))
                    {
                        _logger.Warn($"lang未検出（展開後）: {candidate.ModId} in {scan.JarFileName}");
                        continue;
                    }

                    _fs.EnsureDir(outLang);
                    _logger.Info($"ディレクトリ作成: {outLang}");

                    // lang配下のファイルを再帰処理
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
                                // 既存ファイル → 比較
                                if (_fs.IsSameContent(srcFile, destPath))
                                {
                                    _logger.Info($"同一内容スキップ: {candidate.ModId}/lang/{relativePath}");
                                }
                                else
                                {
                                    // 競合コピー
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
            }
            finally
            {
                // クリーンアップ
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

            processed++;
            progress.Report((processed, total, scan.JarFileName));

            // jar単位キャンセルチェック
            if (options.CancelGranularity == CancelGranularity.PerJar)
                ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult(result);
    }

    /// <summary>実行前バックアップ（Zip）を作成</summary>
    public async Task CreateBackupAsync(string targetDir, CancellationToken ct)
    {
        var parentDir = Path.GetDirectoryName(targetDir) ?? targetDir;
        var dirName = Path.GetFileName(targetDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var zipPath = Path.Combine(parentDir, $"{dirName}_backup_{timestamp}.zip");

        _logger.Info($"バックアップ作成開始: {zipPath}");

        await Task.Run(() =>
        {
            System.IO.Compression.ZipFile.CreateFromDirectory(targetDir, zipPath);
        }, ct);

        _logger.Info($"バックアップ完了: {zipPath}");
    }
}
