namespace ModLangOrganizer.Models;

/// <summary>処理方針</summary>
public enum ProcessingStrategy
{
    /// <summary>A: langあり → 抽出・コピー</summary>
    LangFound,
    /// <summary>B: langなし → スキップ</summary>
    NoLang
}

/// <summary>予定操作の種別</summary>
public enum PlannedOperationType
{
    Extract,
    CreateDir,
    Copy,
    ConflictCopy,
    FallbackCopy,
    Cleanup,
    Skip
}

/// <summary>jar健全性</summary>
public enum JarIntegrity
{
    Unknown,
    OK,
    Corrupted
}

/// <summary>スナップショット状態</summary>
public enum SnapshotState
{
    /// <summary>最新（スキャン結果と一致）</summary>
    Current,
    /// <summary>要再スキャン</summary>
    Stale
}

/// <summary>Mod処理ステータス</summary>
public enum ModStatus
{
    Pending,
    Scanning,
    Scanned,
    Processing,
    Success,
    Warning,
    Skipped,
    Failed
}

/// <summary>ログレベル</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>キャンセル粒度</summary>
public enum CancelGranularity
{
    /// <summary>jar単位（既定）</summary>
    PerJar,
    /// <summary>ファイル単位</summary>
    PerFile
}

/// <summary>Mod一覧の絞り込みタブ</summary>
public enum ModFilterTab
{
    All,
    Extractable,
    Fallback,
    Errors
}
