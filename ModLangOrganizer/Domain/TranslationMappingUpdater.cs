using System.IO;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>
/// MODのバージョンアップ等によりJAR名が変更された際、mapping.jsonの参照先JARを自動追従・更新する。
/// </summary>
public sealed class TranslationMappingUpdater
{
    private readonly Logger _logger;

    public TranslationMappingUpdater(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// スキャン結果と既存マッピングを照合し、削除された旧JARの参照を同一ModIdを持つ新JARへ自動更新する。
    /// </summary>
    /// <returns>更新されたエントリの件数</returns>
    public int UpdateJarReferences(WorkspaceMapping mapping, IReadOnlyList<JarScanResult> scanResults)
    {
        if (mapping.Entries.Count == 0 || scanResults.Count == 0)
            return 0;

        // 現在存在するJARの相対パス一覧
        var currentJarPaths = new HashSet<string>(
            scanResults.Select(s => s.RelativeJarPath.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        // ModId ごとのスキャン結果マップ（同一ModIdを持つJAR一覧）
        var jarsByModId = new Dictionary<string, List<JarScanResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var scan in scanResults)
        {
            if (scan.Strategy == ProcessingStrategy.NoLang || scan.Integrity == JarIntegrity.Corrupted)
                continue;

            foreach (var candidate in scan.LangCandidates)
            {
                if (!jarsByModId.TryGetValue(candidate.ModId, out var list))
                {
                    list = new List<JarScanResult>();
                    jarsByModId[candidate.ModId] = list;
                }
                if (!list.Contains(scan))
                {
                    list.Add(scan);
                }
            }
        }

        var updatedCount = 0;

        // 旧JARごとにグルーピングして更新判定
        var entriesByJar = mapping.Entries
            .GroupBy(e => e.JarRelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in entriesByJar)
        {
            var oldJarPath = group.Key;
            if (currentJarPaths.Contains(oldJarPath))
            {
                // 旧JARはまだ存在するので更新不要
                continue;
            }

            foreach (var entry in group)
            {
                if (!jarsByModId.TryGetValue(entry.ModId, out var candidateJars) || candidateJars.Count == 0)
                {
                    continue;
                }

                var normalizedArchivePath = entry.ArchivePath.TrimStart('/').Replace('\\', '/');

                // 候補新JARの中で、ModIdが一致し entry.ArchivePath に対応する LangCandidate を持っているJARを絞り込む
                var matchedJars = candidateJars.Where(j =>
                    j.LangCandidates.Any(c =>
                    {
                        if (!c.ModId.Equals(entry.ModId, StringComparison.OrdinalIgnoreCase))
                            return false;
                        var candidateRoot = c.ArchiveLangPath.TrimStart('/').TrimEnd('/') + "/";
                        return normalizedArchivePath.StartsWith(candidateRoot, StringComparison.OrdinalIgnoreCase);
                    })).ToList();

                if (matchedJars.Count == 1)
                {
                    var matchedJar = matchedJars[0];
                    var newJarPath = matchedJar.RelativeJarPath.Replace('\\', '/');
                    if (!newJarPath.Equals(oldJarPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Info($"MOD更新を検出しマッピングを自動更新: {oldJarPath} → {newJarPath} (ModId: {entry.ModId}, File: {entry.EditPath})");
                        entry.JarRelativePath = newJarPath;
                        entry.LastUpdated = DateTimeOffset.Now;
                        updatedCount++;
                    }
                }
                else if (matchedJars.Count > 1)
                {
                    _logger.Warn($"MOD更新候補の新JARが複数存在するため自動更新をスキップしました: ModId={entry.ModId}, OldJar={oldJarPath}, 候補=[{string.Join(", ", matchedJars.Select(m => m.RelativeJarPath))}]");
                }
            }
        }

        return updatedCount;
    }
}
