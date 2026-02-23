using ModLangOrganizer.Helpers;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.ViewModels;

/// <summary>スキャン結果テーブルの1行分</summary>
public sealed class ModItemViewModel : ObservableObject
{
    private ModStatus _status = ModStatus.Pending;
    private SnapshotState _snapshotState = SnapshotState.Current;

    public required string JarFileName { get; init; }
    public required JarIntegrity Integrity { get; init; }
    public required int LangCount { get; init; }
    public required ProcessingStrategy Strategy { get; init; }
    public required int ExtractCount { get; init; }
    public required int CreateDirCount { get; init; }
    public required int CopyCount { get; init; }
    public required int ConflictCopyCount { get; init; }
    public required int CleanupCount { get; init; }
    public required int SkipCount { get; init; }

    public ModStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public SnapshotState SnapshotState
    {
        get => _snapshotState;
        set => SetProperty(ref _snapshotState, value);
    }

    // 表示用プロパティ
    public string IntegrityDisplay => Integrity switch
    {
        JarIntegrity.OK => "OK",
        JarIntegrity.Corrupted => "破損",
        _ => "—"
    };

    public string LangCountDisplay => LangCount switch
    {
        0 => "なし",
        1 => "1",
        _ => $"{LangCount} (複数)"
    };

    public string StrategyDisplay => Strategy switch
    {
        ProcessingStrategy.LangFound => "A (抽出)",
        ProcessingStrategy.NoLang => "B (スキップ)",
        _ => "—"
    };

    public string OperationSummary =>
        $"E:{ExtractCount} D:{CreateDirCount} C:{CopyCount} CF:{ConflictCopyCount} CL:{CleanupCount} S:{SkipCount}";

    public string SnapshotStateDisplay => SnapshotState switch
    {
        SnapshotState.Current => "最新",
        SnapshotState.Stale => "要再スキャン",
        _ => "—"
    };

    public string StatusDisplay => Status switch
    {
        ModStatus.Pending => "未処理",
        ModStatus.Scanning => "スキャン中",
        ModStatus.Scanned => "スキャン済",
        ModStatus.Processing => "処理中",
        ModStatus.Success => "成功",
        ModStatus.Warning => "警告",
        ModStatus.Skipped => "スキップ",
        ModStatus.Failed => "失敗",
        _ => "—"
    };

    /// <summary>JarScanResultからViewModelを生成</summary>
    public static ModItemViewModel FromScanResult(JarScanResult scan)
    {
        var ops = scan.PlannedOperations;
        return new ModItemViewModel
        {
            JarFileName = scan.JarFileName,
            Integrity = scan.Integrity,
            LangCount = scan.LangCandidates.Count,
            Strategy = scan.Strategy,
            ExtractCount = ops.Count(o => o.Type == PlannedOperationType.Extract),
            CreateDirCount = ops.Count(o => o.Type == PlannedOperationType.CreateDir),
            CopyCount = ops.Count(o => o.Type == PlannedOperationType.Copy),
            ConflictCopyCount = ops.Count(o => o.Type == PlannedOperationType.ConflictCopy),
            CleanupCount = ops.Count(o => o.Type == PlannedOperationType.Cleanup),
            SkipCount = ops.Count(o => o.Type == PlannedOperationType.Skip),
            Status = ModStatus.Scanned
        };
    }
}
