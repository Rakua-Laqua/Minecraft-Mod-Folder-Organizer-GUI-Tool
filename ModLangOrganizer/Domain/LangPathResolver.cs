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
        var outputBase = fullOutputRoot;

        var relativeDirectory = Path.GetDirectoryName(scan.RelativeJarPath);
        if (!string.IsNullOrWhiteSpace(relativeDirectory) && relativeDirectory != ".")
        {
            var segments = relativeDirectory.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
                throw new InvalidDataException($"無効なJAR相対フォルダです: {relativeDirectory}");

            foreach (var segment in segments)
            {
                ValidatePathSegment(segment, "JAR相対フォルダ名");
                outputBase = Path.Combine(outputBase, segment);
            }

            outputBase = Path.GetFullPath(outputBase);
            EnsureContained(outputBase, fullOutputRoot);
        }

        var jarOutputRoot = Path.GetFullPath(Path.Combine(outputBase, jarRootName));
        EnsureContained(jarOutputRoot, fullOutputRoot);
        return jarOutputRoot;
    }

    /// <summary>
    /// 外部編集用ディレクトリを決定する。
    /// 1. 既存の mapping に該当エントリがあればその親ディレクトリ（過去の抽出パス）を維持
    /// 2. 既存のディスク上に旧形式フォルダ（<outputRoot>/<jarRootName>...）が存在すれば互換性のためそれを維持
    /// 3. 新規抽出の場合は、基本形 `<outputRoot>/<modId>` とする
    /// 4. 同じ ModId を持つ候補が他のJARにも存在する場合は、衝突回避のため `<outputRoot>/<modId>__<jarRootName>` とする
    /// </summary>
    public static string ResolveEditDirectory(
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate,
        WorkspaceMapping? existingMapping = null,
        IReadOnlyList<JarScanResult>? allScans = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
            throw new ArgumentException("出力ルートが指定されていません。", nameof(outputRoot));

        var fullOutputRoot = Path.GetFullPath(outputRoot);

        // 1. 既存 mapping の確認
        if (existingMapping != null)
        {
            var matchedEntry = existingMapping.Entries.FirstOrDefault(e =>
                e.JarRelativePath.Equals(scan.RelativeJarPath, StringComparison.OrdinalIgnoreCase) &&
                e.ModId.Equals(candidate.ModId, StringComparison.OrdinalIgnoreCase));

            if (matchedEntry != null && !string.IsNullOrEmpty(matchedEntry.EditPath))
            {
                var fullEditPath = Path.Combine(fullOutputRoot, matchedEntry.EditPath.Replace('/', Path.DirectorySeparatorChar));
                var dirFromMapping = Path.GetDirectoryName(fullEditPath);
                if (!string.IsNullOrEmpty(dirFromMapping))
                {
                    EnsureContained(dirFromMapping, fullOutputRoot);
                    return dirFromMapping;
                }
            }
        }

        // 2. 旧形式（<outputRoot>/<jarRootName>...）のフォルダが既にディスク上に存在するか確認
        var legacyDir = GetLegacyExternalLangDirectory(outputRoot, scan, candidate);
        if (Directory.Exists(legacyDir))
        {
            return legacyDir;
        }

        // 3. 新規抽出: <outputRoot>/<modId>（ModId重複時は衝突回避）
        var modIdSegment = SanitizeDirectoryName(candidate.ModId);
        var isDuplicateModId = allScans != null && allScans
            .Where(s => !s.RelativeJarPath.Equals(scan.RelativeJarPath, StringComparison.OrdinalIgnoreCase))
            .Any(s => s.LangCandidates.Any(c => c.ModId.Equals(candidate.ModId, StringComparison.OrdinalIgnoreCase)));

        string targetFolderName;
        if (isDuplicateModId)
        {
            var jarRootName = SanitizeDirectoryName(Path.GetFileNameWithoutExtension(scan.JarFileName));
            targetFolderName = $"{modIdSegment}__{jarRootName}";
        }
        else
        {
            targetFolderName = modIdSegment;
        }

        var resultDir = Path.GetFullPath(Path.Combine(fullOutputRoot, targetFolderName));
        EnsureContained(resultDir, fullOutputRoot);
        return resultDir;
    }

    public static string GetExternalLangDirectory(
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate)
    {
        return ResolveEditDirectory(outputRoot, scan, candidate);
    }

    public static string GetLegacyExternalLangDirectory(
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
        var relativeDirectory = Path.GetDirectoryName(scan.RelativeJarPath);
        var jarDisplayPath = string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "."
            ? jarRootName
            : $"{relativeDirectory.Replace('\\', '/')}/{jarRootName}";

        return scan.LangCandidates.Count > 1
            ? $"{jarDisplayPath}/{candidate.ModId}"
            : jarDisplayPath;
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
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return;

        if (!normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"出力ルート外のパスは使用できません: {path}");
        }
    }

    public static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalidChars.Contains(ch) || ch is '/' or '\\' ? '_' : ch);
        }
        var sanitized = sb.ToString().Trim('.', ' ', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "mod" : sanitized;
    }
}
