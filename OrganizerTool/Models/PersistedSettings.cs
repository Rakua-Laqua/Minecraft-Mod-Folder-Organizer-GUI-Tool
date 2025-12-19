using System;

namespace OrganizerTool.Models;

public sealed class PersistedSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string TargetDir { get; set; } = "";

    public bool DryRun { get; set; }
    public bool BackupZip { get; set; }
    public bool JarMode { get; set; }

    public MultiLangMode MultiLangMode { get; set; } = MultiLangMode.FirstOnly;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Permanent;

    public static PersistedSettings Default() => new();
}
