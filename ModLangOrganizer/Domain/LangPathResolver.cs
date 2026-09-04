using System.IO;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>JAR内langパスと外部編集フォルダの対応を一元管理する。</summary>
public static class LangPathResolver
{
    public static string GetJarOutputRoot(string outputRoot, JarScanResult scan)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("出力ルートが指定されていません。", nameof(outputRoot));

        var jarRootName = Path.GetFileNameWithoutExtension(scan.JarFileName);
        ValidatePathSegment(jarRootName, "jar名");

        var fullOutputRoot = Path.GetFullPath(outputRoot);
        var jarOutputRoot = Path.GetFullPath(Path.Combine(fullOutputRoot, jarRootName));
        EnsureContained(jarOutputRoot, fullOutputRoot);
        return jarOutputRoot;
    }

    public static string GetExternalLangDirectory(
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate)
    {
        var jarOutputRoot = GetJarOutputRoot(outputRoot, scan);
        if (scan.LangCandidates.Count <= 1)
            return jarOutputRoot;

        ValidatePathSegment(candidate.ModId, "lang候補キー");
        var candidateRoot = Path.GetFullPath(Path.Combine(jarOutputRoot, candidate.ModId));
        EnsureContained(candidateRoot, jarOutputRoot);
        return candidateRoot;
    }

    public static string GetDisplayPath(JarScanResult scan, LangCandidate candidate)
    {
        var jarRootName = Path.GetFileNameWithoutExtension(scan.JarFileName);
        return scan.LangCandidates.Count > 1
            ? $"{jarRootName}/{candidate.ModId}"
            : jarRootName;
    }

    public static string BuildArchivePath(LangCandidate candidate, string relativePath)
    {
        var archiveLangPath = NormalizeArchiveLangPath(candidate.ArchiveLangPath);

        var normalizedRelative = relativePath.Replace('\\', '/').TrimStart('/');
        var segments = normalizedRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new InvalidDataException($"無効なlang相対パスです: {relativePath}");

        return $"{archiveLangPath}/{string.Join('/', segments)}";
    }

    private static string NormalizeArchiveLangPath(string archiveLangPath)
    {
        if (string.IsNullOrWhiteSpace(archiveLangPath) ||
            archiveLangPath.StartsWith('/') ||
            archiveLangPath.Contains('\\'))
        {
            throw new InvalidDataException($"無効なJAR内langパスです: {archiveLangPath}");
        }

        var segments = archiveLangPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(s => s is "." or "..") ||
            !segments[^1].Equals("lang", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"無効なJAR内langパスです: {archiveLangPath}");
        }

        return string.Join('/', segments);
    }

    private static void ValidatePathSegment(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') ||
            value.Contains('\\'))
        {
            throw new InvalidDataException($"無効な{label}です: {value}");
        }
    }

    private static void EnsureContained(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"出力ルート外のパスは使用できません: {path}");
    }
}
