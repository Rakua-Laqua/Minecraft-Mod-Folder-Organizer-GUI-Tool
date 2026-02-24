namespace ModLangOrganizer.Models;

/// <summary>実行進捗イベントの種類</summary>
public enum ExecutionProgressStage
{
    Started,
    Completed
}

/// <summary>jar単位の実行進捗</summary>
public sealed record ExecutionProgress(
    int Index,
    int Current,
    int Total,
    string JarName,
    ExecutionProgressStage Stage,
    ModStatus? FinalStatus = null);
