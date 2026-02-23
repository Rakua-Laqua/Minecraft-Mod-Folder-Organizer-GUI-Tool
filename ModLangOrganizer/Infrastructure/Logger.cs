using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>ロガー（画面表示用 + ファイル出力用）</summary>
public sealed class Logger
{
    private readonly object _lock = new();

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public event Action<LogEntry>? LogAdded;

    public void Info(string message) => Add(Models.LogLevel.Info, message);
    public void Warn(string message) => Add(Models.LogLevel.Warning, message);
    public void Error(string message) => Add(Models.LogLevel.Error, message);

    private void Add(Models.LogLevel level, string message)
    {
        var entry = new LogEntry { Level = level, Message = message };
        lock (_lock)
        {
            Entries.Add(entry);
        }
        LogAdded?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_lock) { Entries.Clear(); }
    }

    /// <summary>ログをファイルに出力</summary>
    public async Task ExportAsync(string filePath)
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var entry in Entries)
                sb.AppendLine(entry.ToString());
        }
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }
}
