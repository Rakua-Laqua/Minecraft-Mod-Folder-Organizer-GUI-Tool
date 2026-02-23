using System.IO;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>スナップショット検証</summary>
public sealed class SnapshotValidator
{
    private readonly FileSystemService _fs = new();

    /// <summary>スキャン時スナップショットと現在の状態を比較</summary>
    /// <returns>差分のあるjarファイル名リスト（空なら合格）</returns>
    public List<string> Validate(IEnumerable<JarScanResult> scanResults)
    {
        var staleJars = new List<string>();

        foreach (var result in scanResults)
        {
            if (result.Snapshot == null) continue;
            if (!File.Exists(result.JarFilePath))
            {
                staleJars.Add(result.JarFileName);
                continue;
            }

            var current = _fs.BuildSnapshot(result.JarFilePath);
            if (!result.Snapshot.Matches(current))
            {
                staleJars.Add(result.JarFileName);
            }
        }

        return staleJars;
    }
}
