using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Threading;
using OrganizerTool.Models;

namespace OrganizerTool.Infrastructure;

public sealed class Logger
{
    private readonly ObservableCollection<LogEntry> _entries;
    private readonly Dispatcher? _dispatcher;

    public Logger(ObservableCollection<LogEntry> entries)
    {
        _entries = entries;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
    }

    public void Info(string message) => Add(LogLevel.Info, message);
    public void Warn(string message) => Add(LogLevel.Warn, message);
    public void Error(string message) => Add(LogLevel.Error, message);

    public void Add(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        // バックグラウンドスレッドから呼ばれても安全にUIへ反映する
        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => _entries.Add(entry));
            return;
        }

        _entries.Add(entry);
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
