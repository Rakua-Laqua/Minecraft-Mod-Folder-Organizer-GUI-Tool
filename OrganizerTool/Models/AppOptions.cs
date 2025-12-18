namespace OrganizerTool.Models;

public sealed class AppOptions
{
    public bool DryRun { get; set; }
    public MultiLangMode MultiLangMode { get; set; } = MultiLangMode.FirstOnly;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Permanent;
    public bool BackupZip { get; set; }
}
