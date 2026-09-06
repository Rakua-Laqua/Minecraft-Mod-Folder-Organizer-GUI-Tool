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
    /// JARの相対カテゴリ構造を維持し、JAR名（拡張子なし）のフォルダへlangファイルを配置する。
    /// 単一lang候補はJARフォルダ直下、複数lang候補は衝突回避のためその下にModIdフォルダを作る。
    /// mapping はJAR反映時の対応情報としてのみ使用し、抽出先の物理配置は変更しない。
    /// </summary>
    public static string ResolveEditDirectory(
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate,
        WorkspaceMapping? existingMapping = null,
        IReadOnlyList<JarScanResult>? allScans = null)
    {
        // 既存呼び出しとの互換性のため引数は維持する。
        // 出力先は常にJAR基準で決定し、過去のmodId直下mappingには追従しない。
        _ = existingMapping;
        _ = allScans;

        return GetExternalLangDirectory(outputRoot, scan, candidate);
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

    /// <summary>
    /// 旧呼び出し互換用。現在の標準出力形式もJAR単位のため GetExternalLangDirectory と同じ結果を返す。
    /// </summary>
    public static string GetLegacyExternalLangDirectory(
        string outputRoot,
        JarScanResult scan,
        LangCandidate candidate)
    {
        return GetExternalLangDirectory(outputRoot, scan, candidate);
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
