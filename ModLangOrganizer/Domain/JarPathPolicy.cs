using System.IO;

namespace ModLangOrganizer.Domain;

/// <summary>再帰JAR探索とlang入出力のパス方針。</summary>
public static class JarPathPolicy
{
    public const string DefaultOutputFolderName = "_lang_output";

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "_backup",
        DefaultOutputFolderName
    };

    /// <summary>親フォルダ配下の既定lang入出力ルート。</summary>
    public static string GetDefaultOutputRoot(string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            return string.Empty;

        return Path.Combine(Path.GetFullPath(targetRoot), DefaultOutputFolderName);
    }

    /// <summary>対象ルートからJARまでの安全な相対パスを取得する。</summary>
    public static string GetRelativeJarPath(string targetRoot, string jarPath)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            throw new ArgumentException("対象ルートが指定されていません。", nameof(targetRoot));
        if (string.IsNullOrWhiteSpace(jarPath))
            throw new ArgumentException("JARパスが指定されていません。", nameof(jarPath));

        var fullRoot = Path.GetFullPath(targetRoot);
        var fullJar = Path.GetFullPath(jarPath);
        if (!IsSameOrUnder(fullJar, fullRoot) ||
            fullJar.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"対象ルート外のJARです: {jarPath}");
        }

        var relative = Path.GetRelativePath(fullRoot, fullJar);
        if (Path.IsPathRooted(relative) || IsParentTraversal(relative))
            throw new InvalidDataException($"無効なJAR相対パスです: {relative}");

        return relative;
    }

    /// <summary>再帰探索時に潜らないディレクトリか判定する。</summary>
    public static bool ShouldSkipDirectory(
        string directoryPath,
        string targetRoot,
        string? outputRoot)
    {
        var fullDirectory = Path.GetFullPath(directoryPath);
        var fullTarget = Path.GetFullPath(targetRoot);
        if (!IsSameOrUnder(fullDirectory, fullTarget))
            return true;

        var directoryName = Path.GetFileName(
            fullDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (ExcludedDirectoryNames.Contains(directoryName))
            return true;

        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            var fullOutput = Path.GetFullPath(outputRoot);
            if (!fullOutput.Equals(fullTarget, StringComparison.OrdinalIgnoreCase) &&
                IsSameOrUnder(fullOutput, fullTarget) &&
                IsSameOrUnder(fullDirectory, fullOutput))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>FileSystemWatcherで無視する生成物・バックアップ配下か判定する。</summary>
    public static bool ShouldIgnoreWatchPath(
        string fullPath,
        string targetRoot,
        string? outputRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(targetRoot))
            return false;

        var fullTarget = Path.GetFullPath(targetRoot);
        var normalizedPath = Path.GetFullPath(fullPath);
        if (!IsSameOrUnder(normalizedPath, fullTarget))
            return true;

        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            var fullOutput = Path.GetFullPath(outputRoot);
            if (!fullOutput.Equals(fullTarget, StringComparison.OrdinalIgnoreCase) &&
                IsSameOrUnder(fullOutput, fullTarget) &&
                IsSameOrUnder(normalizedPath, fullOutput))
            {
                return true;
            }
        }

        var relative = Path.GetRelativePath(fullTarget, normalizedPath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment => ExcludedDirectoryNames.Contains(segment));
    }

    public static string ToDisplayPath(string relativePath) =>
        relativePath.Replace('\\', '/');

    public static bool IsSameOrUnder(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return fullPath.StartsWith(
            fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsParentTraversal(string relativePath)
    {
        if (relativePath.Equals("..", StringComparison.Ordinal))
            return true;

        return relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
               relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
