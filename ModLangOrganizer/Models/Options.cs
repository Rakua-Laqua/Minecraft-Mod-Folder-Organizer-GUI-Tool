namespace ModLangOrganizer.Models;

/// <summary>ユーザーオプション</summary>
public sealed class Options
{
    /// <summary>実行前バックアップ（Zip）</summary>
    public bool BackupZip { get; set; } = false;

    /// <summary>キャンセル粒度</summary>
    public CancelGranularity CancelGranularity { get; set; } = CancelGranularity.PerJar;

    /// <summary>FileSystemWatcher使用</summary>
    public bool UseWatcher { get; set; } = true;

    /// <summary>langフォールバック有効（ターゲットファイルが無ければソースからコピー生成）</summary>
    public bool LangFallbackEnabled { get; set; } = false;

    /// <summary>フォールバック元のファイル名（拡張子なし、例: en_us）</summary>
    public string LangFallbackSourceName { get; set; } = "en_us";

    /// <summary>フォールバック先のファイル名（拡張子なし、例: ja_jp）</summary>
    public string LangFallbackTargetName { get; set; } = "ja_jp";
}
