using System.IO;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>スナップショット検証</summary>
public sealed class SnapshotValidator
{
    private readonly FileSystemService _fs = new();

    /// <summary>スキャン時スナップショットと現在の状態を比較</summary>
    /// <returns>差分のあるjar相対パス一覧（空なら合格）</returns>
    public List<string> Validate(IEnumerable<JarScanResult> scanResults)
    {
        var staleJars = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in scanResults)
        {
            if (result.Snapshot == null) continue;
            var relativePath = result.RelativeJarPath.Replace('\\', '/');
            if (!File.Exists(result.JarFilePath))
            {
                if (seen.Add(relativePath))
                    staleJars.Add(relativePath);
                continue;
            }

            var current = _fs.BuildSnapshot(result.JarFilePath);
            if (!result.Snapshot.Matches(current) && seen.Add(relativePath))
            {
                staleJars.Add(relativePath);
            }
        }

        return staleJars;
    }
}
