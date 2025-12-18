namespace OrganizerTool.Models;

public sealed class AppOptions
{
    public bool DryRun { get; set; }

    /// <summary>
    /// true の場合、親フォルダ直下の .jar を対象に含め、jarを展開せずに assets/&lt;modid&gt;/lang を抽出する。
    /// jar自体は削除・改変しない。
    /// </summary>
    public bool JarMode { get; set; }

    public MultiLangMode MultiLangMode { get; set; } = MultiLangMode.FirstOnly;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Permanent;
    public bool BackupZip { get; set; }
}
