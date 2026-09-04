using System.IO;
using System.IO.Compression;
using System.Text;
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
                // 1. 各反映対象ファイルについて、既存エントリと内容を比較
                var filesToUpdate = new List<JarImportFile>();
                var filesToAdd = new List<JarImportFile>();
                var unchangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var importFile in importFiles)
                {
                    var existingEntry = sourceArchive.Entries.FirstOrDefault(e =>
                        e.FullName.Equals(importFile.ArchivePath, StringComparison.OrdinalIgnoreCase));

                    if (existingEntry == null)
                    {
                        filesToAdd.Add(importFile);
                    }
                    else if (IsSameContent(existingEntry, importFile.SourcePath, cancelPerFile, ct))
                    {
                        unchangedPaths.Add(existingEntry.FullName);
                        unchanged++;
                    }
                    else
                    {
                        filesToUpdate.Add(importFile);
                        updated++;
                    }
                }

                // 2. 実際に内容が変更される既存エントリ（filesToUpdate）についてのみ署名保護をチェック
                // （内容が同一のファイルや新規追加ファイルは署名ダイジェストを破壊しないためブロックしない）
                if (filesToUpdate.Count > 0)
                {
                    ValidateSignatureProtection(jarPath, sourceArchive, filesToUpdate);
                }

                // 変更対象が0件（すべて同一内容）の場合は書き換え不要
                if (filesToAdd.Count == 0 && filesToUpdate.Count == 0)
                {
                    return new JarArchiveUpdateResult(0, 0, unchanged);
                }

                added = filesToAdd.Count;

                // 3. Createモードで新しい一時アーカイブを構築
                var updateMap = filesToUpdate.ToDictionary(
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
                    // 既存エントリのコピー（変更対象以外、および同一内容のもの）
                    foreach (var sourceEntry in sourceArchive.Entries)
                    {
                        ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                        if (updateMap.ContainsKey(sourceEntry.FullName))
                        {
                            // 内容が更新されるエントリは後で新規データとして書き出す
                            continue;
                        }

                        // 変更対象外、または同一内容のエントリをそのままコピー
                        CopyExistingEntry(sourceEntry, destinationArchive, cancelPerFile, ct);
                    }

                    // 更新エントリの書き出し
                    foreach (var importFile in filesToUpdate)
                    {
                        ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                        var newEntry = destinationArchive.CreateEntry(importFile.ArchivePath, CompressionLevel.Optimal);
                        newEntry.LastWriteTime = DateTimeOffset.Now;

                        using var sourceStream = File.OpenRead(importFile.SourcePath);
                        using var destStream = newEntry.Open();
                        CopyTo(sourceStream, destStream, cancelPerFile, ct);
                    }

                    // 新規追加エントリの書き出し
                    foreach (var importFile in filesToAdd)
                    {
                        ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

                        var newEntry = destinationArchive.CreateEntry(importFile.ArchivePath, CompressionLevel.Optimal);
                        newEntry.LastWriteTime = DateTimeOffset.Now;

                        using var sourceStream = File.OpenRead(importFile.SourcePath);
                        using var destStream = newEntry.Open();
                        CopyTo(sourceStream, destStream, cancelPerFile, ct);
                    }
                }
            }

            ThrowIfPerFileCancellationRequested(cancelPerFile, ct);

            // 4. Java ZipInputStream 互換性（中央ディレクトリ走査）の厳密検証
            ValidateZipCompatibility(tempPath);

            // 5. インポートされたファイルの内容一致検証
            ValidateImportedFiles(tempPath, importFiles, cancelPerFile, ct);

            // 6. 元ファイルと安全にアトミック置換
            ReplaceOriginal(tempPath, jarPath);

            return new JarArchiveUpdateResult(added, updated, unchanged);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// 署名付きJARにおいて、署名ダイジェストが存在する既存エントリが改変されようとしていないか検証する。
    /// MANIFEST.MF および *.SF のセクションを継続行（72バイト折り返し）込みでパースし、
    /// ダイジェスト属性を持つエントリのみを保護対象とする。
    /// </summary>
    private static void ValidateSignatureProtection(
        string jarPath,
        ZipArchive sourceArchive,
        IReadOnlyList<JarImportFile> modifiedFiles)
    {
        var hasSignature = sourceArchive.Entries.Any(IsJarSignatureEntry);
        if (!hasSignature)
            return;

        var signedEntries = ParseSignedEntries(sourceArchive);
        if (signedEntries.Count == 0)
            return;

        // 今回内容が変更されるファイルが署名ダイジェスト対象に含まれているか検査
        var blocked = modifiedFiles
            .Where(f => signedEntries.Contains(f.ArchivePath))
            .Select(f => f.ArchivePath)
            .ToList();

        if (blocked.Count > 0)
        {
            throw new SignedJarModificationBlockedException(jarPath, blocked);
        }
    }

    /// <summary>
    /// MANIFEST.MF および *.SF から、署名ダイジェスト属性（*-Digest:）を持つエントリパスを収集する。
    /// 仕様通りの継続行（先頭スペース/タブ）連結とセクション分割を行う。
    /// </summary>
    public static HashSet<string> ParseSignedEntries(ZipArchive archive)
    {
        var signedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var manifestFiles = archive.Entries.Where(e =>
            e.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase) ||
            (e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)));

        foreach (var entry in manifestFiles)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            // 1. 継続行（先頭スペースまたはタブ）を連結した論理行リストを構築
            var logicalLines = new List<string>();
            string? rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                if (rawLine.StartsWith(' ') || rawLine.StartsWith('\t'))
                {
                    if (logicalLines.Count > 0)
                    {
                        // 前の行の末尾に連結（先頭の空白文字1つを除く）
                        logicalLines[^1] += rawLine[1..];
                    }
                }
                else
                {
                    logicalLines.Add(rawLine);
                }
            }

            // 2. 空行区切りでセクションをパース
            string? currentName = null;
            var hasDigest = false;

            foreach (var line in logicalLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    // セクションの終端
                    if (!string.IsNullOrEmpty(currentName) && hasDigest)
                    {
                        signedEntries.Add(currentName);
                    }
                    currentName = null;
                    hasDigest = false;
                    continue;
                }

                if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                {
                    currentName = line["Name:".Length..].Trim();
                }
                else
                {
                    var colonIdx = line.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var headerName = line[..colonIdx].Trim();
                        if (headerName.EndsWith("-Digest", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDigest = true;
                        }
                    }
                }
            }

            // 最終セクション
            if (!string.IsNullOrEmpty(currentName) && hasDigest)
            {
                signedEntries.Add(currentName);
            }
        }

        return signedEntries;
    }

    /// <summary>
    /// ZIPの中央ディレクトリ（Central Directory）を正確に走査し、
    /// JavaのZipInputStreamでZipExceptionを引き起こす
    /// 「STORED (0) なのに bit 3 (Data Descriptor) が立っている」不正エントリが存在しないことを検証する。
    /// 中央ディレクトリおよびローカルヘッダーの双方をピンポイントで検査するため、
    /// 圧縮データストリームの誤判定を起こさず確実に検証できる。
    /// </summary>
    public static void ValidateZipCompatibility(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        using var reader = new BinaryReader(fs);

        if (fs.Length < 22)
            throw new InvalidDataException("ZIPファイルのサイズが不正です。");

        // 1. アーカイブ末尾から End of Central Directory Record (EOCD: 0x06054b50) を探索
        var maxSearchLength = (int)Math.Min(fs.Length, 65557); // 最大コメント長 65535 + EOCD 22
        fs.Seek(-maxSearchLength, SeekOrigin.End);
        var searchBuffer = reader.ReadBytes(maxSearchLength);

        var eocdOffsetInSearch = -1;
        for (var i = searchBuffer.Length - 22; i >= 0; i--)
        {
            if (searchBuffer[i] == 0x50 &&
                searchBuffer[i + 1] == 0x4b &&
                searchBuffer[i + 2] == 0x05 &&
                searchBuffer[i + 3] == 0x06)
            {
                eocdOffsetInSearch = i;
                break;
            }
        }

        if (eocdOffsetInSearch == -1)
            throw new InvalidDataException("End of Central Directory Record (EOCD) が見つかりません。");

        var eocdPosition = fs.Length - maxSearchLength + eocdOffsetInSearch;
        fs.Seek(eocdPosition + 8, SeekOrigin.Begin);
        var entriesOnDisk = reader.ReadUInt16();
        var totalEntries = reader.ReadUInt16();
        var cdSize = reader.ReadUInt32();
        var cdOffset = reader.ReadUInt32();

        // 2. 中央ディレクトリの全エントリを走査
        fs.Seek(cdOffset, SeekOrigin.Begin);
        for (var i = 0; i < totalEntries; i++)
        {
            var cdSig = reader.ReadUInt32();
            if (cdSig != 0x02014b50) // PK\x01\x02
                throw new InvalidDataException($"Central Directory Record のシグネチャが不正です (エントリ {i + 1}/{totalEntries})。");

            var versionMadeBy = reader.ReadUInt16();
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
            var fileCommentLength = reader.ReadUInt16();
            var diskNumberStart = reader.ReadUInt16();
            var internalAttributes = reader.ReadUInt16();
            var externalAttributes = reader.ReadUInt32();
            var localHeaderOffset = reader.ReadUInt32();

            var fileNameBytes = reader.ReadBytes(fileNameLength);
            var fileName = Encoding.UTF8.GetString(fileNameBytes);
            fs.Seek(extraFieldLength + fileCommentLength, SeekOrigin.Current);

            // 中央ディレクトリヘッダーにおけるチェック
            var hasDataDescriptor = (generalPurposeFlag & 0x0008) != 0;
            if (compressionMethod == 0 && hasDataDescriptor)
            {
                throw new InvalidDataException(
                    $"Java互換性エラー: エントリ '{fileName}' は非圧縮(STORED)ですが Data Descriptor フラグが付与されています (CD)。" +
                    "JavaのZipInputStreamでZipExceptionとなります。");
            }

            // 3. Local File Header 側も直接検査
            var currentPos = fs.Position;
            fs.Seek(localHeaderOffset, SeekOrigin.Begin);
            var localSig = reader.ReadUInt32();
            if (localSig != 0x04034b50) // PK\x03\x04
                throw new InvalidDataException($"Local File Header のシグネチャが不正です: '{fileName}'");

            fs.Seek(2, SeekOrigin.Current); // versionNeeded
            var localFlag = reader.ReadUInt16();
            var localMethod = reader.ReadUInt16();

            var localHasDataDescriptor = (localFlag & 0x0008) != 0;
            if (localMethod == 0 && localHasDataDescriptor)
            {
                throw new InvalidDataException(
                    $"Java互換性エラー: エントリ '{fileName}' は非圧縮(STORED)ですが Local Header に Data Descriptor フラグが付与されています。" +
                    "JavaのZipInputStreamでZipExceptionとなります。");
            }

            fs.Seek(currentPos, SeekOrigin.Begin);
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

