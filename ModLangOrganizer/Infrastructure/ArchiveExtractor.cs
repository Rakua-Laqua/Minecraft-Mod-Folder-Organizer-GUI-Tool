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

    /// <summary>
    /// JAR内の全エントリを安全検証しつつ、lang直下の .json / .lang だけを一時抽出する。
    /// JAR全体を展開しないことで大量の一時ファイル作成・削除を避ける。
    /// </summary>
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

            // Zip Slip対策: 抽出対象外も含め、全ファイルエントリを従来通り検証する。
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

            // スキャン処理と同じ対象範囲に絞り、lang以外のclass・texture等はディスクへ展開しない。
            if (!IsSupportedLangEntry(entry.FullName))
                continue;

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

    private static bool IsSupportedLangEntry(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            return false;

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.EndsWith('/') || normalized.StartsWith('/'))
            return false;

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(p => p is "." or ".."))
            return false;

        if (!parts[^2].Equals("lang", StringComparison.OrdinalIgnoreCase))
            return false;

        var extension = Path.GetExtension(parts[^1]);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lang", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>jarパスから短いハッシュを生成</summary>
    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input + DateTime.UtcNow.Ticks));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
