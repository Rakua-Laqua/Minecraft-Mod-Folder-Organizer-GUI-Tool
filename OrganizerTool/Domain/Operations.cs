namespace OrganizerTool.Domain;

public enum OperationKind
{
    EnsureDirectory = 0,
    MoveWithOverwrite = 1,
    DeletePath = 2,
    BackupZip = 3,
    ExtractZipEntry = 4,
}

public interface IOperation
{
    OperationKind Kind { get; }
    string Describe();
}

public sealed record EnsureDirectoryOperation(string Path) : IOperation
{
    public OperationKind Kind => OperationKind.EnsureDirectory;
    public string Describe() => $"MKDIR  {Path}";
}

public sealed record MoveWithOverwriteOperation(string SourcePath, string DestinationPath) : IOperation
{
    public OperationKind Kind => OperationKind.MoveWithOverwrite;
    public string Describe() => $"MOVE   {SourcePath} -> {DestinationPath} (overwrite)";
}

public sealed record DeletePathOperation(string Path) : IOperation
{
    public OperationKind Kind => OperationKind.DeletePath;
    public string Describe() => $"DELETE {Path}";
}

public sealed record BackupZipOperation(string SourceDirectory, string ZipPath) : IOperation
{
    public OperationKind Kind => OperationKind.BackupZip;
    public string Describe() => $"ZIP    {SourceDirectory} -> {ZipPath}";
}

public sealed record ExtractZipEntryOperation(string ZipPath, string EntryPath, string DestinationPath) : IOperation
{
    public OperationKind Kind => OperationKind.ExtractZipEntry;
    public string Describe() => $"EXTRACT {ZipPath}!{EntryPath} -> {DestinationPath}";
}

public sealed class ExecutionPlan
{
    public required string ModName { get; init; }
    public required string ModPath { get; init; }
    public required IReadOnlyList<string> LangCandidates { get; init; }

    public required string PolicyLabel { get; init; }

    public required IReadOnlyList<IOperation> Operations { get; init; }

    public int PlannedMoves { get; init; }
    public int PlannedDeletes { get; init; }
}
