using System.Collections.ObjectModel;
using System.Text;
using OrganizerTool.Models;

namespace OrganizerTool.Infrastructure;

public sealed class Logger
{
    private readonly ObservableCollection<LogEntry> _entries;

    public Logger(ObservableCollection<LogEntry> entries)
    {
        _entries = entries;
    }

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Warn(string message) => Add(LogLevel.Warn, message);
    public void Error(string message) => Add(LogLevel.Error, message);

    public void Add(LogLevel level, string message)
    {
        _entries.Add(new LogEntry(DateTimeOffset.Now, level, message));
    }

    public string ExportText()
    {
        var sb = new StringBuilder();
        foreach (var e in _entries)
        {
            sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append('\t');
            sb.Append(e.Level.ToString().ToUpperInvariant());
            sb.Append('\t');
            sb.AppendLine(e.Message);
        }

        return sb.ToString();
    }
}
