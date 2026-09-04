using System.IO;
using System.IO.Compression;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>署名付きJARの署名対象エントリが変更されようとした場合にスローされる例外</summary>
public sealed class SignedJarModificationBlockedException : InvalidOperationException
{
    public string JarPath { get; }
    public IReadOnlyList<string> BlockedEntries { get; }

    public SignedJarModificationBlockedException(string jarPath, IReadOnlyList<string> blockedEntries)
        : base($"署名付きJARの署名対象エントリの変更は安全のためブロックされました: {Path.GetFileName(jarPath)} ({string.Join(", ", blockedEntries)})")
    {
        JarPath = jarPath;
        BlockedEntries = blockedEntries;
    }
}

/// <summary>JARを安全に再構築（Create方式）してlangを反映し、検証後に元ファイルと置換する。</summary>
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
            // 既存JARを読み取り専用で開き、新規一時JAR（Createモード）へ再構築する
            using (var sourceArchive = ZipFile.OpenRead(jarPath))
            {
                // 署名付きJARの場合、署名対象エントリの改変をチェックして保護
                ValidateSignatureProtection(jarPath, sourceArchive, importFiles);

                // 変更対象エントリパス一覧
                var importMap = importFiles.ToDictionary(
                    f => f.ArchivePath,
                    StringComparer.OrdinalIgnoreCase);

                using (var tempStream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    BufferSize,
                    FileOptions.None))
                using (var destinationArchive = new ZipArchive(tempStream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    // 1. 既存エントリのうち、更新対象ではないものをコピー
                    foreach (var sourceEntry in sourceArchive.Entries)
                    {
                        ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                        if (importMap.TryGetValue(sourceEntry.FullName, out var importFile))
                        {
                            // 内容が全く同じかチェック
                            if (IsSameContent(sourceEntry, importFile.SourcePath, cancelPerFile, ct))
                            {
                                unchanged++;
                                CopyExistingEntry(sourceEntry, destinationArchive, cancelPerFile, ct);
                                importMap.Remove(sourceEntry.FullName); // 処理済み
                            }
                            else
                            {
                                // 内容が異なるため、後で新しい内容で書き出す
                                updated++;
                            }
                            continue;
                        }

                        // 変更対象外のエントリをそのままコピー
                        CopyExistingEntry(sourceEntry, destinationArchive, cancelPerFile, ct);
                    }

                    // 2. 新規追加、および内容が更新されたエントリを書き出す
                    foreach (var kvp in importMap)
                    {
                        ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                        var importFile = kvp.Value;
                        var isNew = !sourceArchive.Entries.Any(e => e.FullName.Equals(importFile.ArchivePath, StringComparison.OrdinalIgnoreCase));
                        if (isNew)
                        {
                            added++;
                        }

                        var newEntry = destinationArchive.CreateEntry(importFile.ArchivePath, CompressionLevel.Optimal);
                        newEntry.LastWriteTime = DateTimeOffset.Now;

                        using var sourceStream = File.OpenRead(importFile.SourcePath);
                        using var destStream = newEntry.Open();
                        CopyTo(sourceStream, destStream, cancelPerFile, ct);
                    }
                }
            }

            if (added == 0 && updated == 0)
                return new JarArchiveUpdateResult(added, updated, unchanged);

            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

            // Java ZipInputStream 互換性（STORED + Data Descriptor 検出）の厳密検証
            ValidateZipCompatibility(tempPath);

            // インポートされたファイルの内容一致検証
            ValidateImportedFiles(tempPath, importFiles, cancelPerFile, ct);

            // 元ファイルと安全にアトミック置換
            ReplaceOriginal(tempPath, jarPath);

            return new JarArchiveUpdateResult(added, updated, unchanged);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// 署名付きJARの署名対象ファイルを変更しようとしていないか検証する。
    /// 署名対象（MANIFEST.MFや.SFにダイジェストが記録されているファイル）の改変は
    /// Javaの改ざん検知（SecurityException）を引き起こすため拒否する。
    /// </summary>
    private static void ValidateSignatureProtection(
        string jarPath,
        ZipArchive sourceArchive,
        IReadOnlyList<JarImportFile> importFiles)
    {
        var hasSignature = sourceArchive.Entries.Any(IsJarSignatureEntry);
        if (!hasSignature)
            return;

        // MANIFEST.MF または .SF ファイルから署名対象のエントリ名を収集
        var signedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifestEntry = sourceArchive.Entries.FirstOrDefault(e =>
            e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry != null)
        {
            using var reader = new StreamReader(manifestEntry.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line["Name:".Length..].Trim();
                    if (!string.IsNullOrEmpty(name))
                        signedEntries.Add(name);
                }
            }
        }

        var sfEntries = sourceArchive.Entries.Where(e =>
            e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
            e.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase));
        foreach (var sf in sfEntries)
        {
            using var reader = new StreamReader(sf.Open());
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line["Name:".Length..].Trim();
                    if (!string.IsNullOrEmpty(name))
                        signedEntries.Add(name);
                }
            }
        }

        // 今回変更しようとしているファイルが署名対象に含まれているか検査
        var blocked = importFiles
            .Where(f => signedEntries.Contains(f.ArchivePath))
            .Select(f => f.ArchivePath)
            .ToList();

        if (blocked.Count > 0)
        {
            throw new SignedJarModificationBlockedException(jarPath, blocked);
        }
    }

    /// <summary>
    /// ZIPヘッダーを走査し、JavaのZipInputStreamでZipExceptionを引き起こす
    /// 「STORED (0) なのに bit 3 (Data Descriptor) が立っている」不正エントリが存在しないことを検証する。
    /// </summary>
    public static void ValidateZipCompatibility(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        using var reader = new BinaryReader(fs);

        while (fs.Position < fs.Length - 4)
        {
            var sig = reader.ReadUInt32();
            if (sig == 0x04034b50) // Local File Header: PK\x03\x04
            {
                var versionNeeded = reader.ReadUInt16();
                var generalPurposeFlag = reader.ReadUInt16();
                var compressionMethod = reader.ReadUInt16();
                var lastModTime = reader.ReadUInt16();
                var lastModDate = reader.ReadUInt16();
                var crc32 = reader.ReadUInt32();
                var compressedSize = reader.ReadUInt32();
                var uncompressedSize = reader.ReadUInt32();
                var fileNameLength = reader.ReadUInt16();
                var extraFieldLength = reader.ReadUInt16();

                var fileNameBytes = reader.ReadBytes(fileNameLength);
                var fileName = System.Text.Encoding.UTF8.GetString(fileNameBytes);
                fs.Seek(extraFieldLength, SeekOrigin.Current);

                // Java ZipInputStream のチェック:
                // if (method == STORED && (flag & 8) != 0) throw ZipException
                var hasDataDescriptor = (generalPurposeFlag & 0x0008) != 0;
                if (compressionMethod == 0 && hasDataDescriptor)
                {
                    throw new InvalidDataException(
                        $"Java互換性エラー: エントリ '{fileName}' は非圧縮(STORED)ですが Data Descriptor フラグが付与されています。" +
                        "JavaのZipInputStreamで破損と判定されます。");
                }

                // データ部分をスキップ
                if (!hasDataDescriptor)
                {
                    fs.Seek(compressedSize, SeekOrigin.Current);
                }
                else
                {
                    // Data Descriptor付きの場合（通常Createモードでは発生しないが、安全のためシグネチャ探索）
                }
            }
            else if (sig == 0x02014b50) // Central Directory Header: PK\x01\x02
            {
                // Central Directory に達したらローカルエントリの走査終了
                break;
            }
        }
    }

    private static void CopyExistingEntry(
        ZipArchiveEntry sourceEntry,
        ZipArchive destinationArchive,
        bool cancelPerFile,
        CancellationToken ct)
    {
        var isDir = sourceEntry.FullName.EndsWith('/');
        // ディレクトリエントリは STORED (サイズ0)、通常ファイルは Optimal (Deflate) で作成
        var level = isDir ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        var newEntry = destinationArchive.CreateEntry(sourceEntry.FullName, level);

        newEntry.LastWriteTime = sourceEntry.LastWriteTime;
        newEntry.ExternalAttributes = sourceEntry.ExternalAttributes;

        if (!isDir)
        {
            using var sourceStream = sourceEntry.Open();
            using var destStream = newEntry.Open();
            CopyTo(sourceStream, destStream, cancelPerFile, ct);
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

    private static bool IsJarSignatureEntry(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/');
        if (!normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return fileName.StartsWith("SIG-", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
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
            // 一時退避ファイルの削除失敗は無視
        }
    }
}

