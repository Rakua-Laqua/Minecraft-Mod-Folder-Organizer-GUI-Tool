namespace ModLangOrganizer.Models;

/// <summary>jarファイルのスナップショット（鮮度管理用）</summary>
public sealed class JarSnapshot
{
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }

    public bool Matches(JarSnapshot other) =>
        FileName == other.FileName &&
        FileSize == other.FileSize &&
        LastWriteTimeUtc == other.LastWriteTimeUtc;
}
