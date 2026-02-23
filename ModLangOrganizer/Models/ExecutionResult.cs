namespace ModLangOrganizer.Models;

/// <summary>実行結果サマリ</summary>
public sealed class ExecutionResult
{
    public int SuccessCount { get; set; }
    public int WarningCount { get; set; }
    public int SkipCount { get; set; }
    public int FailCount { get; set; }
    public int CleanupFailCount { get; set; }
    public List<string> Errors { get; set; } = [];
}
