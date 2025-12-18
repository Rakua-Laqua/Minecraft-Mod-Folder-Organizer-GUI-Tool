using OrganizerTool.Models;

namespace OrganizerTool.Domain;

public sealed class ModScanResult
{
    public ModSourceType SourceType { get; init; } = ModSourceType.Directory;

    public required string ModName { get; init; }
    public required string ModPath { get; init; }

    public bool AssetsExists { get; init; }

    /// <summary>
    /// 検出した lang ディレクトリ候補（フルパス）。
    /// assets/<modid>/lang に一致するもののみ。
    /// </summary>
    public required IReadOnlyList<string> LangCandidates { get; init; }

    public int PlannedMoves { get; init; }
    public int PlannedDeletes { get; init; }

    public string PolicyLabel { get; init; } = "";
}
