namespace ModLangOrganizer.Models;

/// <summary>アプリ設定（永続化対象）</summary>
public sealed class AppSettings
{
    public string TargetDir { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = string.Empty;
    public bool OutputRootSameAsTarget { get; set; } = true;
    public bool BackupZip { get; set; }
    public CancelGranularity CancelGranularity { get; set; } = CancelGranularity.PerJar;

    public AppSettings Clone()
    {
        return new AppSettings
        {
            TargetDir = TargetDir,
            OutputRoot = OutputRoot,
            OutputRootSameAsTarget = OutputRootSameAsTarget,
            BackupZip = BackupZip,
            CancelGranularity = CancelGranularity
        };
    }
}
