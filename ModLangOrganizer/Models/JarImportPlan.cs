namespace ModLangOrganizer.Models;

/// <summary>全JARに対するlang反映計画</summary>
public sealed class JarImportBatchPlan
{
    public List<JarImportPlan> JarPlans { get; } = [];

    public int ImportableJarCount => JarPlans.Count(p => p.Files.Count > 0);
    public int SourceFileCount => JarPlans.Sum(p => p.Files.Count);
    public int SignedJarCount => JarPlans.Count(p => p.Files.Count > 0 && p.ScanResult.HasSignature);
    public int IgnoredConflictFileCount => JarPlans.Sum(p => p.IgnoredConflictFiles.Count);
}

/// <summary>1つのJARに対するlang反映計画</summary>
public sealed class JarImportPlan
{
    public required JarScanResult ScanResult { get; init; }
    public List<JarImportFile> Files { get; } = [];
    public List<string> MissingSourceDirectories { get; } = [];
    public List<string> PlanningErrors { get; } = [];
    public List<string> IgnoredConflictFiles { get; } = [];
}

/// <summary>外部ファイルとJAR内エントリの対応</summary>
public sealed record JarImportFile(string SourcePath, string ArchivePath);

/// <summary>1つのJARの更新結果</summary>
public sealed record JarArchiveUpdateResult(
    int AddedCount,
    int UpdatedCount,
    int UnchangedCount);
