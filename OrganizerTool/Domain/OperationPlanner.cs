using System.IO;
using OrganizerTool.Models;

namespace OrganizerTool.Domain;

public sealed class OperationPlanner
{
    public ExecutionPlan BuildPlan(ModScanResult scan, AppOptions options, string? backupRootDir)
    {
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
