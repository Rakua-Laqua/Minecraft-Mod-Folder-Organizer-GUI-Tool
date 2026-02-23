using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ModLangOrganizer.Infrastructure;

/// <summary>jar(zip)展開（Zip Slip対策付き）</summary>
public sealed class ArchiveExtractor
{
    private const string TempRootName = "mod-organizer";

    /// <summary>作業展開フォルダのルートパスを取得</summary>
    public static string GetTempRoot() =>
        Path.Combine(Path.GetTempPath(), TempRootName);

    /// <summary>jarの作業展開ディレクトリを決定</summary>
    public string DetermineWorkDir(string jarPath)
    {
        var jarName = Path.GetFileNameWithoutExtension(jarPath);
        var hash = ComputeShortHash(jarPath);
        return Path.Combine(GetTempRoot(), $"{jarName}_{hash}");
    }

    /// <summary>安全にjarを展開する（Zip Slip対策）</summary>
    /// <returns>展開先ディレクトリ</returns>
    public string ExtractSecure(string jarPath, string destDir, CancellationToken ct = default)
    {
        var fullDest = Path.GetFullPath(destDir);
        if (!Directory.Exists(fullDest))
            Directory.CreateDirectory(fullDest);

        using var archive = ZipFile.OpenRead(jarPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // ディレクトリエントリはスキップ
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var entryPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));

            // Zip Slip対策: 展開先が許可ルート配下か検証
            if (!entryPath.StartsWith(fullDest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !entryPath.Equals(fullDest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Zip Slip detected: entry '{entry.FullName}' escapes destination '{fullDest}'");
            }

            // ..を含むパスも拒否（二重チェック）
            if (entry.FullName.Contains(".."))
            {
                throw new InvalidOperationException(
                    $"Zip Slip detected: entry '{entry.FullName}' contains '..'");
            }

            var entryDir = Path.GetDirectoryName(entryPath);
            if (entryDir != null && !Directory.Exists(entryDir))
                Directory.CreateDirectory(entryDir);

            entry.ExtractToFile(entryPath, overwrite: true);
        }

        return fullDest;
    }

    /// <summary>アーカイブ内のエントリ一覧を取得（展開せずに読み取り）</summary>
    public List<string> ListEntries(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    /// <summary>jarパスから短いハッシュを生成</summary>
    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input + DateTime.UtcNow.Ticks));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
