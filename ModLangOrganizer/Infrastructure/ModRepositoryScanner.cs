using System.IO;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>Minecraft MOD倉庫を再帰走査して、JARだけの階層ツリーを構築する。</summary>
public sealed class ModRepositoryScanner
{
    private static readonly string[] DisabledKeywords =
    [
        "_backup",
        "有効化しないmod",
        "無効化",
        "disabled",
        "bak"
    ];

    public IReadOnlyList<ModTreeNodeModel> Scan(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("管理元フォルダを指定してください。", nameof(rootPath));

        var expanded = Environment.ExpandEnvironmentVariables(rootPath.Trim());
        var root = Path.GetFullPath(expanded);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"管理元フォルダが存在しません: {root}");

        var result = new List<ModTreeNodeModel>();
        foreach (var entry in EnumerateEntries(root))
        {
            if (Directory.Exists(entry))
            {
                var node = ScanDirectory(entry, root, inheritedDisabled: false);
                if (node is not null && node.ModCount > 0)
                    result.Add(node);
            }
            else if (IsJarFile(entry))
            {
                var node = CreateFileNode(entry, root, isDisabled: false);
                if (node is not null)
                    result.Add(node);
            }
        }

        return result;
    }

    private ModTreeNodeModel? ScanDirectory(string directoryPath, string rootPath, bool inheritedDisabled)
    {
        var name = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var isDisabled = inheritedDisabled || IsDisabledFolderName(name);
        var folder = new ModTreeNodeModel
        {
            Name = name,
            RelativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, directoryPath)),
            FullPath = directoryPath,
            IsFolder = true,
            IsDisabledFolder = isDisabled,
            IsChecked = false,
            IsExpanded = true
        };

        long totalBytes = 0;
        var totalMods = 0;

        foreach (var entry in EnumerateEntries(directoryPath))
        {
            if (Directory.Exists(entry))
            {
                var childFolder = ScanDirectory(entry, rootPath, isDisabled);
                if (childFolder is null || childFolder.ModCount == 0)
                    continue;

                folder.Children.Add(childFolder);
                totalBytes += childFolder.SizeBytes;
                totalMods += childFolder.ModCount;
            }
            else if (IsJarFile(entry))
            {
                var childFile = CreateFileNode(entry, rootPath, isDisabled);
                if (childFile is null)
                    continue;

                folder.Children.Add(childFile);
                totalBytes += childFile.SizeBytes;
                totalMods++;
            }
        }

        if (totalMods == 0)
            return null;

        folder.SizeBytes = totalBytes;
        folder.FormattedSize = FormatBytes(totalBytes);
        folder.ModCount = totalMods;
        return folder;
    }

    private static ModTreeNodeModel? CreateFileNode(string filePath, string rootPath, bool isDisabled)
    {
        try
        {
            var info = new FileInfo(filePath);
            return new ModTreeNodeModel
            {
                Name = info.Name,
                RelativePath = NormalizeRelativePath(Path.GetRelativePath(rootPath, filePath)),
                FullPath = info.FullName,
                IsFolder = false,
                IsDisabledFolder = isDisabled,
                SizeBytes = info.Exists ? info.Length : 0,
                FormattedSize = FormatBytes(info.Exists ? info.Length : 0),
                ModCount = 0,
                IsChecked = false,
                IsExpanded = false
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> EnumerateEntries(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath)
                .Where(path => !IsReparsePoint(path))
                .OrderBy(path => Directory.Exists(path) ? 0 : 1)
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsJarFile(string path) =>
        File.Exists(path) && string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase);

    public static bool IsDisabledFolderName(string name) =>
        DisabledKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):F1} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:F1} KB";
        return $"{bytes} B";
    }
}
