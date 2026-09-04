using System.IO;
using System.IO.Compression;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

public sealed class ResourcePackBuildResult
{
    public int ModCount { get; init; }
    public int FileCount { get; init; }
    public string DestinationPath { get; init; } = string.Empty;
}

/// <summary>
/// 翻訳ファイル群からMinecraft用リソースパック（pack.mcmeta + assets/<mod_id>/lang/*.json）を生成する。
/// MODのJAR自体を一切改変しないため、ZIP破損や署名検証エラーのリスクが原理的にゼロになります。
/// </summary>
public sealed class ResourcePackBuilder
{
    /// <summary>
    /// フォルダ形式のリソースパックを出力する。
    /// </summary>
    public ResourcePackBuildResult BuildFolder(
        JarImportBatchPlan batchPlan,
        string destinationDirectory,
        int packFormat = 15,
        string description = "ModLangOrganizer Translations")
    {
        Directory.CreateDirectory(destinationDirectory);

        // pack.mcmeta の生成 (1.20.1: pack_format 15)
        var mcmetaPath = Path.Combine(destinationDirectory, "pack.mcmeta");
        var mcmetaContent = "{\n  \"pack\": {\n    \"pack_format\": " + packFormat + ",\n    \"description\": \"" + EscapeJsonString(description) + "\"\n  }\n}\n";
        File.WriteAllText(mcmetaPath, mcmetaContent, System.Text.Encoding.UTF8);

        var fileCount = 0;
        var modCount = 0;

        foreach (var plan in batchPlan.JarPlans)
        {
            if (plan.Files.Count == 0)
                continue;

            modCount++;
            foreach (var file in plan.Files)
            {
                var targetPath = Path.Combine(
                    destinationDirectory,
                    file.ArchivePath.Replace('/', Path.DirectorySeparatorChar));

                var parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(file.SourcePath, targetPath, overwrite: true);
                fileCount++;
            }
        }

        return new ResourcePackBuildResult
        {
            ModCount = modCount,
            FileCount = fileCount,
            DestinationPath = destinationDirectory
        };
    }

    /// <summary>
    /// ZIPアーカイブ形式のリソースパックを出力する。
    /// </summary>
    public ResourcePackBuildResult BuildZip(
        JarImportBatchPlan batchPlan,
        string destinationZipPath,
        int packFormat = 15,
        string description = "ModLangOrganizer Translations")
    {
        var tempZip = destinationZipPath + ".tmp";
        if (File.Exists(tempZip))
            File.Delete(tempZip);

        var fileCount = 0;
        var modCount = 0;

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
                if (plan.Files.Count == 0)
                    continue;

                modCount++;
                foreach (var file in plan.Files)
                {
                    var entry = archive.CreateEntry(file.ArchivePath, CompressionLevel.Optimal);
                    entry.LastWriteTime = DateTimeOffset.Now;

                    using var source = File.OpenRead(file.SourcePath);
                    using var dest = entry.Open();
                    source.CopyTo(dest);
                    fileCount++;
                }
            }
        }

        if (File.Exists(destinationZipPath))
            File.Delete(destinationZipPath);
        File.Move(tempZip, destinationZipPath);

        return new ResourcePackBuildResult
        {
            ModCount = modCount,
            FileCount = fileCount,
            DestinationPath = destinationZipPath
        };
    }

    private static string EscapeJsonString(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
}