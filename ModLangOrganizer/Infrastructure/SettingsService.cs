using System.Threading;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>Coordinates settings load/save with debounce and serialized writes.</summary>
public sealed class SettingsService : IDisposable
{
    private readonly SettingsStore _store;
    private readonly TimeSpan _debounceDelay;
    private readonly Action<string>? _warn;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private AppSettings _latest = new();
    private CancellationTokenSource? _pendingSaveCts;

    public SettingsService(SettingsStore store, Action<string>? warn = null, TimeSpan? debounceDelay = null)
    {
        _store = store;
        _warn = warn;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(400);
    }

    public AppSettings Load()
    {
        try
        {
            _latest = _store.Load();
            return _latest.Clone();
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"設定読込エラー: {ex.Message}");
            _latest = new AppSettings();
            return _latest.Clone();
        }
    }

    public void ScheduleSave(AppSettings settings)
    {
        CancellationToken token;
        lock (_stateLock)
        {
            _latest = settings.Clone();
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = new CancellationTokenSource();
            token = _pendingSaveCts.Token;
        }

        _ = SaveDebouncedAsync(token);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        AppSettings snapshot;
        lock (_stateLock)
        {
            _pendingSaveCts?.Cancel();
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
            snapshot = _latest.Clone();
        }

        await PersistAsync(snapshot, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"設定保存エラー: {ex.Message}");
        }

        lock (_stateLock)
        {
            _pendingSaveCts?.Dispose();
            _pendingSaveCts = null;
        }

        _saveLock.Dispose();
    }

    private async Task SaveDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounceDelay, token).ConfigureAwait(false);

            AppSettings snapshot;
            lock (_stateLock)
            {
                snapshot = _latest.Clone();
            }

            await PersistAsync(snapshot, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Newer save request replaced this run.
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"設定保存エラー: {ex.Message}");
        }
    }

    private async Task PersistAsync(AppSettings settings, CancellationToken token)
    {
        await _saveLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() => _store.Save(settings), token).ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}