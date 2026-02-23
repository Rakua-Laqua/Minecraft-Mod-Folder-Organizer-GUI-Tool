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
}
