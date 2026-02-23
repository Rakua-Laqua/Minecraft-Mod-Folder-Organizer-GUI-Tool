namespace ModLangOrganizer.Models;

/// <summary>全jarを含む実行計画</summary>
public sealed class ExecutionPlan
{
    public List<JarExecutionPlan> JarPlans { get; set; } = [];
    public int TotalExtract => JarPlans.Sum(p => p.ExtractCount);
    public int TotalCreateDir => JarPlans.Sum(p => p.CreateDirCount);
    public int TotalCopy => JarPlans.Sum(p => p.CopyCount);
    public int TotalConflictCopy => JarPlans.Sum(p => p.ConflictCopyCount);
    public int TotalCleanup => JarPlans.Sum(p => p.CleanupCount);
    public int TotalSkip => JarPlans.Sum(p => p.SkipCount);
}

/// <summary>1つのjarの実行計画</summary>
public sealed class JarExecutionPlan
{
    public required JarScanResult ScanResult { get; init; }
    public List<PlannedOperation> Operations { get; set; } = [];

    public int ExtractCount => Operations.Count(o => o.Type == PlannedOperationType.Extract);
    public int CreateDirCount => Operations.Count(o => o.Type == PlannedOperationType.CreateDir);
    public int CopyCount => Operations.Count(o => o.Type == PlannedOperationType.Copy);
    public int ConflictCopyCount => Operations.Count(o => o.Type == PlannedOperationType.ConflictCopy);
    public int CleanupCount => Operations.Count(o => o.Type == PlannedOperationType.Cleanup);
    public int SkipCount => Operations.Count(o => o.Type == PlannedOperationType.Skip);
}
