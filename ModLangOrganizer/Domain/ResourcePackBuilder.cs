using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

public sealed class ResourcePackBuildResult
{
    public int ModCount { get; init; }
    public int FileCount { get; init; }
    public string DestinationPath { get; init; } = string.Empty;
    public IReadOnlyList<string> SkippedNonStandardPaths { get; init; } = [];
}

/// <summary>
/// 翻訳ファイル群からMinecraft用リソースパック（pack.mcmeta + assets/<namespace>/lang/*.json 等）を生成する。
/// MODのJAR自体を一切改変しないため、ZIP破損や署名検証エラーのリスクが原理的にゼロになります。
/// </summary>
public sealed class ResourcePackBuilder
{
    /// <summary>
    /// Minecraft標準のリソースパック構造 (assets/<namespace>/lang/...) に準拠しているかを判定する。
    /// すべてのパスセグメントを検証し、危険な相対・絶対・ADS・バックスラッシュは拒否する。
    /// </summary>
    public static bool IsStandardLangPath(string archivePath)
    {
        if (!TryGetSafeArchiveSegments(archivePath, out var parts))
            return false;

        return parts.Length >= 4
            && parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parts[1])
            && parts[2].Equals("lang", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSafeArchiveSegments(string archivePath, out string[] parts)
    {
        parts = [];
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

        parts = archivePath.Split('/', StringSplitOptions.None);
        if (parts.Length == 0)
            return false;

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or "..")
                return false;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;
        }

        return true;
    }

    private static bool TryGetPathUnderWorkRoot(string workRoot, string archivePath, out string fullTargetPath)
    {
        fullTargetPath = string.Empty;
        if (!TryGetSafeArchiveSegments(archivePath, out var parts))
            return false;

        var combined = workRoot;
        foreach (var part in parts)
            combined = Path.Combine(combined, part);

        var fullTarget = Path.GetFullPath(combined);
        var fullRoot = Path.GetFullPath(workRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedTarget = fullTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmedTarget.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!trimmedTarget.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullTargetPath = fullTarget;
        return true;
    }

    /// <summary>
    /// フォルダ形式のリソースパックを出力する。
    /// 一時フォルダに完全構築したのち安全に置換するため、前回出力された古いファイルが残留しません。
    /// </summary>
    public ResourcePackBuildResult BuildFolder(
        JarImportBatchPlan batchPlan,
        string destinationDirectory,
        int packFormat = 15,
        string description = "ModLangOrganizer Translations",
        CancellationToken ct = default)
    {
        var parentDir = Path.GetDirectoryName(destinationDirectory)
            ?? throw new InvalidOperationException("出力先フォルダの親ディレクトリを取得できません。");

        Directory.CreateDirectory(parentDir);

        var folderName = Path.GetFileName(destinationDirectory);
        var tempDirectory = Path.Combine(parentDir, $".{folderName}.{Guid.NewGuid():N}.modlang.tmp");
        var backupDirectory = Path.Combine(parentDir, $".{folderName}.{Guid.NewGuid():N}.modlang.bak");

        var fileCount = 0;
        var modCount = 0;
        var skippedNonStandard = new List<string>();

        try
        {
            Directory.CreateDirectory(tempDirectory);

            // pack.mcmeta の生成
            var mcmetaPath = Path.Combine(tempDirectory, "pack.mcmeta");
            var mcmetaContent = "{\n  \"pack\": {\n    \"pack_format\": " + packFormat + ",\n    \"description\": \"" + EscapeJsonString(description) + "\"\n  }\n}\n";
            File.WriteAllText(mcmetaPath, mcmetaContent, System.Text.Encoding.UTF8);

            foreach (var plan in batchPlan.JarPlans)
            {
                ct.ThrowIfCancellationRequested();

                if (plan.Files.Count == 0)
                    continue;

                var modHadValidFiles = false;
                foreach (var file in plan.Files)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!IsStandardLangPath(file.ArchivePath) ||
                        !TryGetPathUnderWorkRoot(tempDirectory, file.ArchivePath, out var targetPath))
                    {
                        skippedNonStandard.Add(file.ArchivePath);
                        continue;
                    }

                    var targetParent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetParent))
                        Directory.CreateDirectory(targetParent);

                    File.Copy(file.SourcePath, targetPath, overwrite: true);
                    fileCount++;
                    modHadValidFiles = true;
                }

                if (modHadValidFiles)
                {
                    modCount++;
                }
            }

            ct.ThrowIfCancellationRequested();

            // 既存フォルダとの置換（安全な置換）
            if (Directory.Exists(destinationDirectory))
            {
                // 既存ディレクトリを一時バックアップへ移動
                SafeMoveDirectoryWithRetry(destinationDirectory, backupDirectory);
                try
                {
                    SafeMoveDirectoryWithRetry(tempDirectory, destinationDirectory);
                }
                catch (Exception replaceEx)
                {
                    // 移動失敗時はバックアップを元に戻す
                    if (!Directory.Exists(destinationDirectory) && Directory.Exists(backupDirectory))
                    {
                        try
                        {
                            SafeMoveDirectoryWithRetry(backupDirectory, destinationDirectory);
                        }
                        catch (Exception rollbackEx)
                        {
                            // ロールバックにも失敗した場合は、旧データを保護するためバックアップを絶対に残す
                            throw new IOException(
                                $"リソースパックの置換に失敗し、バックアップからの復元にも失敗しました。" +
                                $"旧データはバックアップフォルダに残されています: '{backupDirectory}' " +
                                $"(置換エラー: {replaceEx.Message}, ロールバックエラー: {rollbackEx.Message})",
                                replaceEx);
                        }
                    }
                    throw;
                }

                // 新しい destinationDirectory が正常に確立されたことを確認して初めてバックアップを削除
                if (Directory.Exists(destinationDirectory))
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
            else
            {
                SafeMoveDirectoryWithRetry(tempDirectory, destinationDirectory);
            }

            return new ResourcePackBuildResult
            {
                ModCount = modCount,
                FileCount = fileCount,
                DestinationPath = destinationDirectory,
                SkippedNonStandardPaths = skippedNonStandard
            };
        }
        finally
        {
            // 一時作業フォルダのクリーンアップ
            // （backupDirectory は置換成功時のみ削除され、ロールバック失敗時はユーザーの旧データを保護するため削除しない）
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// ZIPアーカイブ形式のリソースパックを出力する。
    /// </summary>
    public ResourcePackBuildResult BuildZip(
        JarImportBatchPlan batchPlan,
        string destinationZipPath,
        int packFormat = 15,
        string description = "ModLangOrganizer Translations",
        CancellationToken ct = default)
    {
        var parentDir = Path.GetDirectoryName(destinationZipPath)
            ?? throw new InvalidOperationException("出力先ZIPの親ディレクトリを取得できません。");

        Directory.CreateDirectory(parentDir);

        var tempZip = destinationZipPath + "." + Guid.NewGuid().ToString("N") + ".modlang.tmp";
        if (File.Exists(tempZip))
            File.Delete(tempZip);

        var fileCount = 0;
        var modCount = 0;
        var skippedNonStandard = new List<string>();

        try
        {
            using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                // pack.mcmeta の書き込み
                var mcmetaEntry = archive.CreateEntry("pack.mcmeta", CompressionLevel.Optimal);
                var mcmetaContent = "{\n  \"pack\": {\n    \"pack_format\": " + packFormat + ",\n    \"description\": \"" + EscapeJsonString(description) + "\"\n  }\n}\n";
                using (var writer = new StreamWriter(mcmetaEntry.Open(), System.Text.Encoding.UTF8))
                {
                    writer.Write(mcmetaContent);
                }

                foreach (var plan in batchPlan.JarPlans)
                {
                    ct.ThrowIfCancellationRequested();

                    if (plan.Files.Count == 0)
                        continue;

                    var modHadValidFiles = false;
                    foreach (var file in plan.Files)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!IsStandardLangPath(file.ArchivePath) ||
                            !TryGetSafeArchiveSegments(file.ArchivePath, out _))
                        {
                            skippedNonStandard.Add(file.ArchivePath);
                            continue;
                        }

                        var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = DateTimeOffset.Now;

                        using var source = File.OpenRead(file.SourcePath);
                        using var dest = entry.Open();
                        source.CopyTo(dest);
                        fileCount++;
                        modHadValidFiles = true;
                    }

                    if (modHadValidFiles)
                    {
                        modCount++;
                    }
                }
            }

            ct.ThrowIfCancellationRequested();

            if (File.Exists(destinationZipPath))
                File.Delete(destinationZipPath);
            File.Move(tempZip, destinationZipPath);

            return new ResourcePackBuildResult
            {
                ModCount = modCount,
                FileCount = fileCount,
                DestinationPath = destinationZipPath,
                SkippedNonStandardPaths = skippedNonStandard
            };
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
    }

    private static void SafeMoveDirectoryWithRetry(string sourceDir, string destDir, int maxRetries = 10)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                Directory.Move(sourceDir, destDir);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i == maxRetries - 1)
                {
                    // 最終フォールバック: ファイル単位で移動して旧フォルダを削除
                    FallbackMoveDirectory(sourceDir, destDir);
                    return;
                }
                Thread.Sleep(50 * (i + 1));
            }
        }
    }

    private static void FallbackMoveDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destFile = Path.Combine(destDir, relative);
            var destFolder = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destFolder))
                Directory.CreateDirectory(destFolder);

            File.Move(file, destFile, overwrite: true);
        }
        TryDeleteDirectory(sourceDir);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ロック等で即時削除できない場合は無視
        }
    }

    private static string EscapeJsonString(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
}