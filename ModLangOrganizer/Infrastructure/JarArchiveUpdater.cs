using System.IO;
using System.IO.Compression;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>JARの一時コピーへlangを反映し、検証後に元ファイルと置換する。</summary>
public sealed class JarArchiveUpdater
{
    private const int BufferSize = 81920;

    public JarArchiveUpdateResult Update(
        string jarPath,
        IReadOnlyList<JarImportFile> importFiles,
        bool cancelPerFile,
        CancellationToken ct)
    {
        if (!File.Exists(jarPath))
            throw new FileNotFoundException("更新対象のJARが見つかりません。", jarPath);

        foreach (var importFile in importFiles)
        {
            ValidateArchivePath(importFile.ArchivePath);
            if (!File.Exists(importFile.SourcePath))
                throw new FileNotFoundException("反映元ファイルが見つかりません。", importFile.SourcePath);
        }

        var duplicatePath = importFiles
            .GroupBy(f => f.ArchivePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicatePath != null)
            throw new InvalidDataException($"同じJARパスへ複数ファイルを反映できません: {duplicatePath.Key}");

        var directory = Path.GetDirectoryName(jarPath)
            ?? throw new InvalidOperationException("JARの親フォルダを取得できません。");
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(jarPath)}.{Guid.NewGuid():N}.modlang.tmp");

        var added = 0;
        var updated = 0;
        var unchanged = 0;

        try
        {
            File.Copy(jarPath, tempPath, overwrite: false);

            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Update))
            {
                foreach (var importFile in importFiles)
                {
                    ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                    var matches = archive.Entries
                        .Where(e => e.FullName.Equals(
                            importFile.ArchivePath,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matches.Count == 1 && IsSameContent(matches[0], importFile.SourcePath, cancelPerFile, ct))
                    {
                        unchanged++;
                        continue;
                    }

                    foreach (var match in matches)
                        match.Delete();

                    var entry = archive.CreateEntry(importFile.ArchivePath, CompressionLevel.Optimal);
                    using var source = File.OpenRead(importFile.SourcePath);
                    using var destination = entry.Open();
                    CopyTo(source, destination, cancelPerFile, ct);

                    if (matches.Count == 0)
                        added++;
                    else
                        updated++;
                }
            }

            if (added == 0 && updated == 0)
                return new JarArchiveUpdateResult(added, updated, unchanged);

            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);
            ValidateImportedFiles(tempPath, importFiles, cancelPerFile, ct);
            ReplaceOriginal(tempPath, jarPath);

            return new JarArchiveUpdateResult(added, updated, unchanged);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static bool IsSameContent(
        ZipArchiveEntry entry,
        string sourcePath,
        bool cancelPerFile,
        CancellationToken ct)
    {
        var sourceInfo = new FileInfo(sourcePath);
        if (entry.Length != sourceInfo.Length)
            return false;

        using var archiveStream = entry.Open();
        using var sourceStream = sourceInfo.OpenRead();
        return StreamsEqual(archiveStream, sourceStream, cancelPerFile, ct);
    }

    private static void ValidateImportedFiles(
        string jarPath,
        IReadOnlyList<JarImportFile> importFiles,
        bool cancelPerFile,
        CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        foreach (var importFile in importFiles)
        {
            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

            var matches = archive.Entries
                .Where(e => e.FullName.Equals(
                    importFile.ArchivePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count != 1 ||
                !IsSameContent(matches[0], importFile.SourcePath, cancelPerFile, ct))
            {
                throw new InvalidDataException(
                    $"反映後JARの検証に失敗しました: {importFile.ArchivePath}");
            }
        }
    }

    private static bool StreamsEqual(
        Stream left,
        Stream right,
        bool cancelPerFile,
        CancellationToken ct)
    {
        var leftBuffer = new byte[BufferSize];
        var rightBuffer = new byte[BufferSize];

        while (true)
        {
            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);
            var leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);

            if (leftRead != rightRead)
                return false;
            if (leftRead == 0)
                return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                return false;
        }
    }

    private static void CopyTo(
        Stream source,
        Stream destination,
        bool cancelPerFile,
        CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);
            destination.Write(buffer, 0, read);
        }
    }

    private static void ReplaceOriginal(string tempPath, string jarPath)
    {
        var rollbackPath = Path.Combine(
            Path.GetDirectoryName(jarPath)!,
            $".{Path.GetFileName(jarPath)}.{Guid.NewGuid():N}.modlang.rollback");

        try
        {
            File.Replace(tempPath, jarPath, rollbackPath, ignoreMetadataErrors: true);
            TryDelete(rollbackPath);
        }
        catch (PlatformNotSupportedException)
        {
            ReplaceOriginalWithMove(tempPath, jarPath, rollbackPath);
        }
    }

    private static void ReplaceOriginalWithMove(string tempPath, string jarPath, string rollbackPath)
    {
        File.Move(jarPath, rollbackPath);
        try
        {
            File.Move(tempPath, jarPath);
            TryDelete(rollbackPath);
        }
        catch
        {
            if (!File.Exists(jarPath) && File.Exists(rollbackPath))
                File.Move(rollbackPath, jarPath);
            throw;
        }
    }

    private static void ThrowIfPerFileCancellationRequested(bool cancelPerFile, CancellationToken ct)
    {
        if (cancelPerFile)
            ct.ThrowIfCancellationRequested();
    }

    private static void ValidateArchivePath(string archivePath)
    {
        var segments = archivePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(archivePath) ||
            archivePath.StartsWith('/') ||
            archivePath.Contains('\\') ||
            segments.Length == 0 ||
            segments.Any(s => s is "." or ".."))
        {
            throw new InvalidDataException($"無効なJAR内パスです: {archivePath}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 更新済みJARまたは元JARを優先し、一時退避ファイルの削除失敗は無視する。
        }
    }
}
