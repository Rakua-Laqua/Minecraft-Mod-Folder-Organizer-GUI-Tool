namespace ModLangOrganizer.Models;

/// <summary>ログエントリ</summary>
public sealed class LogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required LogLevel Level { get; init; }
    public required string Message { get; init; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
}
