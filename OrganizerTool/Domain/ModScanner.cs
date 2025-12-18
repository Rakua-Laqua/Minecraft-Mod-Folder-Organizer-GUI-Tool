using System.IO;

namespace OrganizerTool.Domain;

public sealed class ModScanner
{
    public IReadOnlyList<ModScanResult> Scan(string targetDir, Func<string, bool> isCancelled)
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
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ModScanResult>(modDirs.Count);

        foreach (var modPath in modDirs)
        {
            if (isCancelled(modPath))
            {
                break;
            }

            var modName = Path.GetFileName(modPath);
            var assetsPath = Path.Combine(modPath, "assets");
            var assetsExists = Directory.Exists(assetsPath);

            var candidates = assetsExists ? FindLangCandidates(assetsPath).ToList() : new List<string>();

            results.Add(new ModScanResult
            {
                ModName = modName,
                ModPath = modPath,
                AssetsExists = assetsExists,
                LangCandidates = candidates,
            });
        }

        return results;
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
