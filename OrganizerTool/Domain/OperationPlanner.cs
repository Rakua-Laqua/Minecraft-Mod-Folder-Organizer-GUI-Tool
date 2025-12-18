using System.IO;
using System.IO.Compression;
using OrganizerTool.Models;

namespace OrganizerTool.Domain;

public sealed class OperationPlanner
{
    public ExecutionPlan BuildPlan(ModScanResult scan, AppOptions options, string? backupRootDir)
    {
        if (scan.SourceType == ModSourceType.Jar)
        {
            return BuildJarPlan(scan, options);
        }

        var operations = new List<IOperation>();

        var modRoot = scan.ModPath;
        var dstLangDir = Path.Combine(modRoot, "lang");

        var candidates = scan.LangCandidates;
        var chosenLangDirs = ChooseLangSources(candidates, options.MultiLangMode);

        var plannedMoves = 0;
        var plannedDeletes = 0;

        // Backup (実行時のみ意味があるが、planとしては常に組み立てておく)
        if (options.BackupZip && !string.IsNullOrWhiteSpace(backupRootDir))
        {
            var zipPath = Path.Combine(backupRootDir, $"{scan.ModName}.zip");
            operations.Add(new EnsureDirectoryOperation(backupRootDir));
            operations.Add(new BackupZipOperation(modRoot, zipPath));
        }

        if (chosenLangDirs.Count > 0)
        {
            // A) langあり
            operations.Add(new EnsureDirectoryOperation(dstLangDir));

            foreach (var srcLangDir in chosenLangDirs)
            {
                var entries = SafeEnumerateFileSystemEntries(srcLangDir);
                foreach (var entry in entries)
                {
                    plannedMoves++;

                    var name = Path.GetFileName(entry);
                    var destPath = Path.Combine(dstLangDir, name);

                    if (File.Exists(destPath) || Directory.Exists(destPath))
                    {
                        plannedDeletes++;
                    }

                    operations.Add(new MoveWithOverwriteOperation(entry, destPath));
                }
            }

            // Mod直下の lang 以外を全削除
            var modChildren = SafeEnumerateFileSystemEntries(modRoot);
            foreach (var child in modChildren)
            {
                var name = Path.GetFileName(child);
                if (string.Equals(name, "lang", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                plannedDeletes++;
                operations.Add(new DeletePathOperation(child));
            }

            return new ExecutionPlan
            {
                ModName = scan.ModName,
                ModPath = scan.ModPath,
                LangCandidates = candidates,
                PolicyLabel = "A (langあり)",
                Operations = operations,
                PlannedMoves = plannedMoves,
                PlannedDeletes = plannedDeletes,
            };
        }

        // B) langなし → Modフォルダ内を空にする
        var children = SafeEnumerateFileSystemEntries(modRoot);
        foreach (var child in children)
        {
            plannedDeletes++;
            operations.Add(new DeletePathOperation(child));
        }

        return new ExecutionPlan
        {
            ModName = scan.ModName,
            ModPath = scan.ModPath,
            LangCandidates = candidates,
            PolicyLabel = "B (langなし: 中身全削除)",
            Operations = operations,
            PlannedMoves = 0,
            PlannedDeletes = plannedDeletes,
        };
    }

    private static ExecutionPlan BuildJarPlan(ModScanResult scan, AppOptions options)
    {
        var operations = new List<IOperation>();

        var jarPath = scan.ModPath;
        var jarName = Path.GetFileNameWithoutExtension(jarPath);
        var parent = Path.GetDirectoryName(jarPath) ?? "";

        // 安全のため、jarは改変せず、親フォルダ配下の専用出力へ抽出する
        // 例: <TargetDir>/_jar_lang/<jarName>/lang/*.json
        var dstLangDir = Path.Combine(parent, "_jar_lang", jarName, "lang");

        var candidates = scan.LangCandidates;
        var chosenLangDirs = ChooseLangSources(candidates, options.MultiLangMode);

        var plannedExtracts = 0;

        if (chosenLangDirs.Count > 0)
        {
            operations.Add(new EnsureDirectoryOperation(dstLangDir));

            foreach (var langDir in chosenLangDirs)
            {
                foreach (var entryPath in EnumerateLangFilesInJar(jarPath, langDir))
                {
                    plannedExtracts++;

                    var fileName = Path.GetFileName(entryPath.Replace('/', Path.DirectorySeparatorChar));
                    var destPath = Path.Combine(dstLangDir, fileName);
                    operations.Add(new ExtractZipEntryOperation(jarPath, entryPath, destPath));
                }
            }
        }

        return new ExecutionPlan
        {
            ModName = scan.ModName,
            ModPath = scan.ModPath,
            LangCandidates = candidates,
            PolicyLabel = chosenLangDirs.Count > 0 ? "JAR (lang抽出のみ)" : "JAR (langなし)",
            Operations = operations,
            PlannedMoves = plannedExtracts,
            PlannedDeletes = 0,
        };
    }

    private static IReadOnlyList<string> EnumerateLangFilesInJar(string jarPath, string langDir)
    {
        // langDir: assets/<modid>/lang
        var prefix = langDir.TrimEnd('/') + "/";

        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var results = new List<string>();

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

                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 既存のフォルダモードに合わせ、lang直下のファイルのみ対象にする
                var relative = name.Substring(prefix.Length);
                if (relative.Contains('/'))
                {
                    continue;
                }

                if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith(".lang", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(name);
                }
            }

            return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ChooseLangSources(IReadOnlyList<string> candidates, MultiLangMode mode)
    {
        if (candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        return mode switch
        {
            MultiLangMode.MergeAll => candidates,
            _ => new[] { candidates[0] },
        };
    }

    private static IReadOnlyList<string> SafeEnumerateFileSystemEntries(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
