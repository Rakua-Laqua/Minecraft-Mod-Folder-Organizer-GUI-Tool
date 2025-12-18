namespace OrganizerTool.Models;

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Message);
