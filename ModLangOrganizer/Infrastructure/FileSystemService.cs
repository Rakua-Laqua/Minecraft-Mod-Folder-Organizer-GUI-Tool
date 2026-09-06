using System.IO;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>ファイルシステム操作（シンボリックリンク・ジャンクション非追従）</summary>
public sealed class FileSystemService
{
    /// <summary>ディレクトリを確実に作成する</summary>
    public void EnsureDir(string path)
    {
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    /// <summary>ファイルをコピーする</summary>
    public void CopyFile(string src, string dst)
    {
        var dir = Path.GetDirectoryName(dst);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.Copy(src, dst, overwrite: false);
    }

    /// <summary>再帰削除（リンク先を辿らない）</summary>
    public void DeleteRecursiveNoFollow(string path)
    {
        if (!Directory.Exists(path)) return;

        var dirInfo = new DirectoryInfo(path);

        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            file.Attributes = FileAttributes.Normal;
            file.Delete();
        }

        foreach (var sub in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(sub.FullName))
            {
                // リンク自体を削除、リンク先は辿らない
                sub.Delete(false);
            }
            else
            {
                DeleteRecursiveNoFollow(sub.FullName);
            }
        }

        dirInfo.Delete(false);
    }

    /// <summary>再解析ポイント（symlink/junction）か判定</summary>
    public bool IsReparsePoint(string path)
    {
        var attr = File.GetAttributes(path);
        return (attr & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>ファイル内容が同一か比較（バイト単位）</summary>
    public bool IsSameContent(string pathA, string pathB)
    {
        var infoA = new FileInfo(pathA);
        var infoB = new FileInfo(pathB);

        if (infoA.Length != infoB.Length) return false;

        const int bufferSize = 8192;
        var bufA = new byte[bufferSize];
        var bufB = new byte[bufferSize];

        using var streamA = infoA.OpenRead();
        using var streamB = infoB.OpenRead();

        while (true)
        {
            var readA = ReadFullChunk(streamA, bufA);
            var readB = ReadFullChunk(streamB, bufB);

            if (readA != readB) return false;
            if (readA == 0) return true;
            if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB)))
                return false;
        }
    }

    private static int ReadFullChunk(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>jarスナップショットを取得</summary>
    public JarSnapshot BuildSnapshot(string jarPath)
    {
        var fi = new FileInfo(jarPath);
        return new JarSnapshot
        {
            FileName = fi.Name,
            FileSize = fi.Length,
            LastWriteTimeUtc = fi.LastWriteTimeUtc
        };
    }

    /// <summary>ディレクトリ直下のファイルを列挙（リンク先非追従）</summary>
    public IEnumerable<string> EnumerateFilesTopLevelNoFollow(string dir, string pattern = "*")
    {
        if (!Directory.Exists(dir)) yield break;
        if (IsReparsePoint(dir)) yield break;

        var dirInfo = new DirectoryInfo(dir);
        foreach (var file in dirInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(file.FullName)) continue;
            yield return file.FullName;
        }
    }

    /// <summary>ディレクトリ配下のファイルを再帰的に列挙（リンク先非追従）</summary>
    public IEnumerable<string> EnumerateFilesNoFollow(string dir, string pattern = "*")
    {
        if (!Directory.Exists(dir)) yield break;
        if (IsReparsePoint(dir)) yield break;

        var dirInfo = new DirectoryInfo(dir);

        foreach (var file in dirInfo.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(file.FullName)) continue;
            yield return file.FullName;
        }

        foreach (var sub in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(sub.FullName)) continue;
            foreach (var file in EnumerateFilesNoFollow(sub.FullName, pattern))
                yield return file;
        }
    }

    /// <summary>ディレクトリ配下のサブディレクトリを再帰的に列挙（リンク先非追従）</summary>
    public IEnumerable<string> EnumerateDirectoriesNoFollow(string dir)
    {
        if (!Directory.Exists(dir)) yield break;

        var dirInfo = new DirectoryInfo(dir);
        foreach (var sub in dirInfo.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(sub.FullName)) continue;
            yield return sub.FullName;
            foreach (var nested in EnumerateDirectoriesNoFollow(sub.FullName))
                yield return nested;
        }
    }
}
