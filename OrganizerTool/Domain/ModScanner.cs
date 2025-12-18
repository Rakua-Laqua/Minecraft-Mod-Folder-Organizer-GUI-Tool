using System.IO;
using System.IO.Compression;
using OrganizerTool.Models;

namespace OrganizerTool.Domain;

public sealed class ModScanner
{
    public IReadOnlyList<ModScanResult> Scan(string targetDir, bool includeJarFiles, Func<string, bool> isCancelled)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return Array.Empty<ModScanResult>();
        }

        if (!Directory.Exists(targetDir))
        {
            return Array.Empty<ModScanResult>();
        }

        var modDirs = Directory.EnumerateDirectories(targetDir, "*", SearchOption.TopDirectoryOnly)
            .ToList();

        var jarFiles = includeJarFiles
            ? Directory.EnumerateFiles(targetDir, "*.jar", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();

        var allItems = modDirs
            .Concat(jarFiles)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ModScanResult>(allItems.Count);

        foreach (var path in allItems)
        {
            if (isCancelled(path))
            {
                break;
            }

            if (Directory.Exists(path))
            {
                var modName = Path.GetFileName(path);
                var assetsPath = Path.Combine(path, "assets");
                var assetsExists = Directory.Exists(assetsPath);

                var candidates = assetsExists ? FindLangCandidates(assetsPath).ToList() : new List<string>();

                results.Add(new ModScanResult
                {
                    SourceType = ModSourceType.Directory,
                    ModName = modName,
                    ModPath = path,
                    AssetsExists = assetsExists,
                    LangCandidates = candidates,
                });

                continue;
            }

            if (File.Exists(path) && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                var modName = Path.GetFileName(path);

                var (assetsExists, candidates) = FindLangCandidatesInJar(path);

                results.Add(new ModScanResult
                {
                    SourceType = ModSourceType.Jar,
                    ModName = modName,
                    ModPath = path,
                    AssetsExists = assetsExists,
                    LangCandidates = candidates,
                });
            }
        }

        return results;
    }

    private static (bool assetsExists, IReadOnlyList<string> langCandidates) FindLangCandidatesInJar(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);

            var assetsExists = false;
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Zipは '/' 区切り
                if (name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    assetsExists = true;
                }

                // assets/<modid>/lang/... の存在確認
                if (!name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                if (!string.Equals(parts[2], "lang", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // langディレクトリそのものを候補にする（末尾スラッシュ無し）
                candidates.Add($"assets/{parts[1]}/lang");
            }

            return (assetsExists, candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch
        {
            return (false, Array.Empty<string>());
        }
    }

    private static IEnumerable<string> FindLangCandidates(string assetsPath)
    {
        // 仕様: assets 配下を再帰検索し、.../assets/[^/]+/lang に一致するディレクトリを候補にする
        // 実装: assets 配下の 'lang' ディレクトリを列挙し、親=modid、祖父=assets のものだけ採用
        IEnumerable<string> langDirs;
        try
        {
            langDirs = Directory.EnumerateDirectories(assetsPath, "lang", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var langDir in langDirs)
        {
            var parent = Directory.GetParent(langDir);
            if (parent is null)
            {
                continue;
            }

            var grandParent = parent.Parent;
            if (grandParent is null)
            {
                continue;
            }

            if (!string.Equals(grandParent.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    assetsPath.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return langDir;
        }
    }
}
