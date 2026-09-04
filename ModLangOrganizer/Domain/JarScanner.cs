using System.IO;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>jarスキャナ: jarファイルを解析してlang候補を検出する</summary>
public sealed class JarScanner
{
    private readonly ArchiveExtractor _extractor = new();
    private readonly FileSystemService _fs = new();

    /// <summary>指定ディレクトリ直下のjar一覧を取得</summary>
    public List<string> EnumerateJars(string targetDir)
    {
        return Directory.GetFiles(targetDir, "*.jar", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>1つのjarをスキャンしてlang候補を検出する</summary>
    public JarScanResult ScanJar(string jarPath, string outputRoot)
    {
        var result = new JarScanResult
        {
            JarFileName = Path.GetFileName(jarPath),
            JarFilePath = jarPath
        };

        try
        {
            // スナップショット取得
            result.Snapshot = _fs.BuildSnapshot(jarPath);

            // アーカイブ内エントリ一覧を取得（展開せずに）
            var entries = _extractor.ListEntries(jarPath);
            var normalizedEntries = entries
                .Select(NormalizeArchivePath)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();

            result.Integrity = JarIntegrity.OK;
            result.HasSignature = entries.Any(IsJarSignatureEntry);

            // lang候補を検出。
            // assets/<modid>/lang に限定せず、JARルートや任意階層の .../lang/*.json|*.lang も対象にする。
            // lang/lang のように既存候補の配下へネストした候補は、誤反映で生成された重複として除外する。
            var langRoots = DetectLangRoots(normalizedEntries);

            if (langRoots.Count == 0)
            {
                result.Strategy = ProcessingStrategy.NoLang;
                result.PlannedOperations.Add(new PlannedOperation
                {
                    Type = PlannedOperationType.Skip,
                    Description = $"{result.JarFileName}: langなし → スキップ"
                });
                return result;
            }

            var usedOutputKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var archiveLangPath in langRoots)
            {
                var modId = TryGetAssetsModId(archiveLangPath);
                var outputKey = MakeUniqueOutputKey(
                    BuildOutputKey(archiveLangPath, modId),
                    usedOutputKeys);

                var prefix = archiveLangPath.TrimEnd('/') + "/";
                var files = normalizedEntries
                    .Where(e => IsDirectLangFile(e, prefix))
                    .Select(e => e[prefix.Length..])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count == 0)
                    continue;

                result.LangCandidates.Add(new LangCandidate
                {
                    ModId = outputKey,
                    ArchiveLangPath = archiveLangPath,
                    Files = files
                });
            }

            if (result.LangCandidates.Count == 0)
            {
                result.Strategy = ProcessingStrategy.NoLang;
                result.PlannedOperations.Add(new PlannedOperation
                {
                    Type = PlannedOperationType.Skip,
                    Description = $"{result.JarFileName}: langなし → スキップ"
                });
                return result;
            }

            result.Strategy = ProcessingStrategy.LangFound;

            // 予定操作を構築
            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Extract,
                Description = $"{result.JarFileName}: 一時展開"
            });

            foreach (var candidate in result.LangCandidates)
            {
                var outLang = LangPathResolver.GetExternalLangDirectory(outputRoot, result, candidate);
                var logLangPath = LangPathResolver.GetDisplayPath(result, candidate);

                result.PlannedOperations.Add(new PlannedOperation
                {
                    Type = PlannedOperationType.CreateDir,
                    Description = $"ディレクトリ作成: {logLangPath}",
                    DestinationPath = outLang
                });

                foreach (var file in candidate.Files)
                {
                    var destPath = Path.Combine(outLang, file);
                    if (File.Exists(destPath))
                    {
                        // 既存ファイルがある → 実行時に比較（スキャン時点ではConflictCopy候補）
                        result.PlannedOperations.Add(new PlannedOperation
                        {
                            Type = PlannedOperationType.ConflictCopy,
                            Description = $"競合可能性: {logLangPath}/{file}",
                            DestinationPath = destPath
                        });
                    }
                    else
                    {
                        result.PlannedOperations.Add(new PlannedOperation
                        {
                            Type = PlannedOperationType.Copy,
                            Description = $"コピー: {logLangPath}/{file}",
                            DestinationPath = destPath
                        });
                    }
                }
            }

            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Cleanup,
                Description = $"{result.JarFileName}: 作業展開フォルダ削除"
            });
        }
        catch (Exception ex)
        {
            result.Integrity = JarIntegrity.Corrupted;
            result.Strategy = ProcessingStrategy.NoLang;
            result.ErrorMessage = ex.Message;
            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Skip,
                Description = $"{result.JarFileName}: 読み取りエラー → スキップ"
            });
        }

        return result;
    }

    private static List<string> DetectLangRoots(IEnumerable<string> entries)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var root = TryGetLangRoot(entry);
            if (root != null)
                roots.Add(root);
        }

        // 浅い候補を優先し、その配下にある lang/lang などのネスト候補を除外する。
        var ordered = roots
            .OrderBy(GetArchivePathDepth)
            .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var accepted = new List<string>();
        foreach (var root in ordered)
        {
            if (accepted.Any(parent => IsDescendantArchivePath(root, parent)))
                continue;

            accepted.Add(root);
        }

        return accepted
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? TryGetLangRoot(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) ||
            entry.EndsWith('/') ||
            entry.StartsWith('/') ||
            !IsSupportedLangFile(entry))
        {
            return null;
        }

        var parts = entry.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Any(p => p is "." or ".."))
            return null;

        if (!parts[^2].Equals("lang", StringComparison.OrdinalIgnoreCase))
            return null;

        return string.Join('/', parts.Take(parts.Length - 1));
    }

    private static bool IsDirectLangFile(string entry, string prefix)
    {
        if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var relative = entry[prefix.Length..];
        return relative.Length > 0 &&
               !relative.Contains('/') &&
               IsSupportedLangFile(relative);
    }

    private static bool IsSupportedLangFile(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".lang", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetAssetsModId(string archiveLangPath)
    {
        var parts = archiveLangPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            parts[0].Equals("assets", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("lang", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        return null;
    }

    private static string BuildOutputKey(string archiveLangPath, string? modId)
    {
        if (!string.IsNullOrWhiteSpace(modId))
            return SanitizeOutputKey(modId);

        var parts = archiveLangPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rawKey = parts.Length >= 2 ? parts[^2] : "root";
        return SanitizeOutputKey(rawKey);
    }

    private static string SanitizeOutputKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "lang";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = key
            .Select(ch => invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch)
            .ToArray();

        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "lang" : sanitized;
    }

    private static string MakeUniqueOutputKey(string baseKey, HashSet<string> used)
    {
        if (used.Add(baseKey))
            return baseKey;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseKey}-{suffix}";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static int GetArchivePathDepth(string path) =>
        path.Count(ch => ch == '/') + 1;

    private static bool IsDescendantArchivePath(string path, string parent)
    {
        var prefix = parent.TrimEnd('/') + "/";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeArchivePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized;
    }

    private static bool IsJarSignatureEntry(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/');
        if (!normalized.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        return fileName.StartsWith("SIG-", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".SF", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
    }
}
