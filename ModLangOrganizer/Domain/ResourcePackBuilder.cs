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
    /// </summary>
    public static bool IsStandardLangPath(string archivePath)
    {
        var normalized = archivePath.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // assets/<namespace>/lang/... (最低4要素: assets, namespace, lang, filename)
        return parts.Length >= 4
            && parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parts[1])
            && parts[2].Equals("lang", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// フォルダ形式のリソースパックを出力する。
    /// 一時フォルダに完全構築したのちアトミックに置換するため、前回出力された古いファイルが残留しません。
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

                    if (!IsStandardLangPath(file.ArchivePath))
                    {
                        skippedNonStandard.Add(file.ArchivePath);
                        continue;
                    }

                    var targetPath = Path.Combine(
                        tempDirectory,
                        file.ArchivePath.Replace('/', Path.DirectorySeparatorChar));

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

            // 既存フォルダとの置換（アトミック更新）
            if (Directory.Exists(destinationDirectory))
            {
                // 既存ディレクトリを一時バックアップへ移動
                SafeMoveDirectoryWithRetry(destinationDirectory, backupDirectory);
                try
                {
                    SafeMoveDirectoryWithRetry(tempDirectory, destinationDirectory);
                }
                catch
                {
                    // 移動失敗時はバックアップを元に戻す
                    if (!Directory.Exists(destinationDirectory) && Directory.Exists(backupDirectory))
                    {
                        SafeMoveDirectoryWithRetry(backupDirectory, destinationDirectory);
                    }
                    throw;
                }

                // バックアップの削除
                TryDeleteDirectory(backupDirectory);
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
            // 一時フォルダ・バックアップのクリーンアップ
            TryDeleteDirectory(tempDirectory);
            TryDeleteDirectory(backupDirectory);
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

                        if (!IsStandardLangPath(file.ArchivePath))
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