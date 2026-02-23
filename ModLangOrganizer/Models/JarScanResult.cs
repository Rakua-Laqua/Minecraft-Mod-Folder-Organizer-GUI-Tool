namespace ModLangOrganizer.Models;

/// <summary>1つのjarに対するスキャン結果</summary>
public sealed class JarScanResult
{
    public required string JarFileName { get; init; }
    public required string JarFilePath { get; init; }
    public JarIntegrity Integrity { get; set; } = JarIntegrity.Unknown;
    public ProcessingStrategy Strategy { get; set; } = ProcessingStrategy.NoLang;

    /// <summary>検出されたlang候補 (assets/modid/lang)</summary>
    public List<LangCandidate> LangCandidates { get; set; } = [];

    /// <summary>予定操作</summary>
    public List<PlannedOperation> PlannedOperations { get; set; } = [];

    /// <summary>スナップショット</summary>
    public JarSnapshot? Snapshot { get; set; }

    public string? ErrorMessage { get; set; }
}

/// <summary>lang候補情報</summary>
public sealed class LangCandidate
{
    /// <summary>modid (assets直下のフォルダ名)</summary>
    public required string ModId { get; init; }

    /// <summary>アーカイブ内のlangフォルダパス</summary>
    public required string ArchiveLangPath { get; init; }

    /// <summary>lang配下のファイル一覧（相対パス）</summary>
    public List<string> Files { get; set; } = [];
}

/// <summary>予定操作1件</summary>
public sealed class PlannedOperation
{
    public required PlannedOperationType Type { get; init; }
    public required string Description { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
}
