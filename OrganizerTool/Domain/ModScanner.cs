using System.IO;
using System.IO.Compression;
using System.Linq;
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

                var candidates = FindLangCandidatesInDirectoryMod(path).ToList();

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

                // ディレクトリエントリ除外
                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                // Zipは '/' 区切り
                if (name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    assetsExists = true;
                }

                // lang候補: 「.../lang/<file>」形式のファイルが存在するディレクトリ
                // assets配下以外にも対応する
                if (!(name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                      name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                // 直下が lang のときだけ（既存のフォルダモードに合わせる）
                if (!string.Equals(parts[^2], "lang", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // langディレクトリそのものを候補にする（末尾スラッシュ無し）
                var langDir = string.Join('/', parts.Take(parts.Length - 1));
                candidates.Add(langDir);
            }

            return (assetsExists, candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch
        {
            return (false, Array.Empty<string>());
        }
    }

    private static IEnumerable<string> FindLangCandidatesInDirectoryMod(string modRoot)
    {
        var assetsPath = Path.Combine(modRoot, "assets");
        var dstLangDir = Path.Combine(modRoot, "lang");

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(assetsPath))
        {
            foreach (var c in FindLangCandidates(assetsPath))
            {
                candidates.Add(c);
            }
        }

        // assets 配下以外の lang も探索
        IEnumerable<string> langDirs;
        try
        {
            langDirs = Directory.EnumerateDirectories(modRoot, "lang", SearchOption.AllDirectories);
        }
        catch
        {
            yield break;
        }

        foreach (var langDir in langDirs)
        {
            // 出力先 (Mod/lang) は候補にしない（自己参照で MOVE が壊れる）
            if (string.Equals(
                    langDir.TrimEnd(Path.DirectorySeparatorChar),
                    dstLangDir.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // assets配下は既に FindLangCandidates で拾っているので除外
            if (IsUnderDirectory(langDir, assetsPath))
            {
                continue;
            }

            if (!ContainsLangFiles(langDir))
            {
                continue;
            }

            candidates.Add(langDir);
        }

        foreach (var c in candidates.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            yield return c;
        }
    }

    private static bool IsUnderDirectory(string path, string parentDir)
    {
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            return false;
        }

        var parentFull = Path.GetFullPath(parentDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return full.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsLangFiles(string langDir)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(langDir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
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
