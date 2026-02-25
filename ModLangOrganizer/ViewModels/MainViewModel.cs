using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ModLangOrganizer.Domain;
using ModLangOrganizer.Helpers;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.ViewModels;

/// <summary>Main window ViewModel.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly JarScanner _scanner = new();
    private readonly SnapshotValidator _snapshotValidator = new();
    private readonly Logger _logger = new();
    private readonly SettingsService _settingsService;

    private string _targetDir = string.Empty;
    private string _outputRoot = string.Empty;
    private bool _outputRootSameAsTarget = true;
    private bool _backupZip;
    private CancelGranularity _cancelGranularity = CancelGranularity.PerJar;
    private bool _langFallbackEnabled;
    private string _langFallbackSourceName = "en_us";
    private string _langFallbackTargetName = "ja_jp";
    private bool _isScanning;
    private bool _isExecuting;
    private bool _scanCompleted;
    private bool _snapshotFresh = true;
    private double _progressPercent;
    private string _progressText = string.Empty;
    private string _statusBarText = "フォルダを選択してください";
    private int _jarCount;
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private bool _isApplyingSettings;
    private bool _disposed;

    // スキャン結果保持
    private List<JarScanResult> _scanResults = [];

    public MainViewModel()
    {
        _settingsService = new SettingsService(new SettingsStore(), message => _logger.Warn(message));

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecuteMainAction);
        CancelCommand = new RelayCommand(Cancel, CanCancel);
        SaveLogCommand = new AsyncRelayCommand(SaveLogAsync);

        _logger.LogAdded += _ =>
        {
            Application.Current?.Dispatcher.Invoke(() => OnPropertyChanged(nameof(LogEntries)));
        };

        LoadSettings();
    }

    // ---- Properties ----

    public string TargetDir
    {
        get => _targetDir;
        set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _targetDir, normalized)) return;

            if (_outputRootSameAsTarget)
                OutputRoot = normalized;

            ScanCompleted = false;
            JarCount = 0;
            SetupWatcher();
            StatusBarText = string.IsNullOrEmpty(normalized)
                ? "フォルダを選択してください"
                : $"選択済: {normalized}   [スキャン] を押してください";

            SaveSettings();
            RefreshCommands();
        }
    }

    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _outputRoot, normalized))
                SaveSettings();
        }
    }

    public bool OutputRootSameAsTarget
    {
        get => _outputRootSameAsTarget;
        set
        {
            if (!SetProperty(ref _outputRootSameAsTarget, value)) return;

            if (value)
                OutputRoot = TargetDir;

            SaveSettings();
        }
    }

    public bool BackupZip
    {
        get => _backupZip;
        set
        {
            if (SetProperty(ref _backupZip, value))
                SaveSettings();
        }
    }

    public CancelGranularity CancelGranularity
    {
        get => _cancelGranularity;
        set
        {
            if (SetProperty(ref _cancelGranularity, value))
                SaveSettings();
        }
    }

    public bool LangFallbackEnabled
    {
        get => _langFallbackEnabled;
        set
        {
            if (SetProperty(ref _langFallbackEnabled, value))
                SaveSettings();
        }
    }

    public string LangFallbackSourceName
    {
        get => _langFallbackSourceName;
        set
        {
            if (SetProperty(ref _langFallbackSourceName, value ?? string.Empty))
                SaveSettings();
        }
    }

    public string LangFallbackTargetName
    {
        get => _langFallbackTargetName;
        set
        {
            if (SetProperty(ref _langFallbackTargetName, value ?? string.Empty))
                SaveSettings();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            RefreshCommands();
        }
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        set
        {
            if (!SetProperty(ref _isExecuting, value)) return;
            RefreshCommands();
        }
    }

    public bool ScanCompleted
    {
        get => _scanCompleted;
        set
        {
            if (!SetProperty(ref _scanCompleted, value)) return;
            RefreshCommands();
        }
    }

    public bool SnapshotFresh
    {
        get => _snapshotFresh;
        set
        {
            if (!SetProperty(ref _snapshotFresh, value)) return;
            RefreshCommands();
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    public int JarCount
    {
        get => _jarCount;
        set => SetProperty(ref _jarCount, value);
    }

    public ObservableCollection<ModItemViewModel> Mods { get; } = [];
    public ObservableCollection<LogEntry> LogEntries => _logger.Entries;

    // ---- Commands ----

    public ICommand BrowseFolderCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveLogCommand { get; }

    // ---- Methods ----

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher?.Dispose();
        _watcher = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _settingsService.Dispose();
    }

    private bool CanScan() =>
        !string.IsNullOrWhiteSpace(TargetDir) &&
        Directory.Exists(TargetDir) &&
        !IsScanning &&
        !IsExecuting;

    private bool CanExecuteMainAction() =>
        ScanCompleted && SnapshotFresh && !IsExecuting && !IsScanning;

    private bool CanCancel() => IsScanning || IsExecuting;

    private void RefreshCommands() => CommandManager.InvalidateRequerySuggested();

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Mod jarファイルがあるフォルダを選択"
        };

        if (dialog.ShowDialog() == true)
            TargetDir = dialog.FolderName;
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "出力ルートフォルダを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            OutputRoot = dialog.FolderName;
            OutputRootSameAsTarget = false;
        }
    }

    private bool EnsureTargetDirValid(bool showMessage)
    {
        var valid = !string.IsNullOrWhiteSpace(TargetDir) && Directory.Exists(TargetDir);
        if (valid) return true;

        if (!showMessage) return false;

        var pathText = string.IsNullOrWhiteSpace(TargetDir) ? "(未設定)" : TargetDir;
        StatusBarText = "親フォルダが存在しません。再選択してください。";
        _logger.Warn($"スキャン不可: 親フォルダが存在しません: {pathText}");
        return false;
    }

    private async Task ScanAsync()
    {
        if (!EnsureTargetDirValid(showMessage: true))
            return;

        IsScanning = true;
        ScanCompleted = false;
        SnapshotFresh = true;
        _scanResults.Clear();
        Mods.Clear();
        _logger.Clear();
        ProgressPercent = 0;
        StatusBarText = "スキャン中...";

        _cts = new CancellationTokenSource();

        try
        {
            var jars = _scanner.EnumerateJars(TargetDir);
            JarCount = jars.Count;
            _logger.Info($"jarファイル {jars.Count} 件を検出: {TargetDir}");

            var outputRoot = string.IsNullOrEmpty(OutputRoot) ? TargetDir : OutputRoot;

            await Task.Run(() =>
            {
                for (int i = 0; i < jars.Count; i++)
                {
                    _cts!.Token.ThrowIfCancellationRequested();

                    var result = _scanner.ScanJar(jars[i], outputRoot);
                    _scanResults.Add(result);

                    var vm = ModItemViewModel.FromScanResult(result);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Mods.Add(vm);
                        ProgressPercent = jars.Count > 0 ? (double)(i + 1) / jars.Count * 100 : 0;
                        ProgressText = $"スキャン: {i + 1}/{jars.Count} - {result.JarFileName}";
                    });

                    if (result.Integrity == JarIntegrity.Corrupted)
                        _logger.Error($"破損: {result.JarFileName} - {result.ErrorMessage}");
                    else if (result.Strategy == ProcessingStrategy.NoLang)
                        _logger.Info($"langなし: {result.JarFileName}");
                    else
                        _logger.Info($"lang検出: {result.JarFileName} ({result.LangCandidates.Count}候補)");
                }
            }, _cts.Token);

            ScanCompleted = true;

            var langCount = _scanResults.Count(r => r.Strategy == ProcessingStrategy.LangFound);
            var skipCount = _scanResults.Count(r => r.Strategy == ProcessingStrategy.NoLang);
            var errCount = _scanResults.Count(r => r.Integrity == JarIntegrity.Corrupted);
            StatusBarText = $"スキャン完了: {JarCount}件 (lang検出: {langCount}, スキップ: {skipCount}, エラー: {errCount})";
            ProgressText = "スキャン完了";
            _logger.Info(StatusBarText);
        }
        catch (OperationCanceledException)
        {
            StatusBarText = "スキャンがキャンセルされました";
            _logger.Warn("スキャンがキャンセルされました");
        }
        catch (Exception ex)
        {
            StatusBarText = $"スキャンエラー: {ex.Message}";
            _logger.Error($"スキャンエラー: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ExecuteAsync()
    {
        if (_scanResults.Count == 0)
            return;

        // 事前整合性チェック
        var staleJars = _snapshotValidator.Validate(_scanResults);
        if (staleJars.Count > 0)
        {
            SnapshotFresh = false;
            var names = string.Join(", ", staleJars);
            StatusBarText = $"変更検出: {names} -> 再スキャンが必要です";
            _logger.Warn($"jarファイル変更検出: {names}");
            MessageBox.Show(
                $"以下のjarファイルがスキャン後に変更されています。\n再スキャンしてください。\n\n{names}",
                "再スキャンが必要", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var langJars = _scanResults.Where(r => r.Strategy == ProcessingStrategy.LangFound).ToList();
        if (MessageBox.Show(
            $"以下の内容で実行します。\n" +
            $"- 対象jar: {langJars.Count}件\n" +
            $"- 出力先: {OutputRoot}\n" +
            $"- バックアップ: {(BackupZip ? "あり" : "なし")}\n" +
            $"- langフォールバック: {(LangFallbackEnabled ? $"あり ({LangFallbackSourceName} → {LangFallbackTargetName})" : "なし")}\n\n実行しますか？",
            "実行確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsExecuting = true;
        ProgressPercent = 0;
        StatusBarText = "実行中...";
        _cts = new CancellationTokenSource();

        var executor = new Executor(_logger);

        try
        {
            var options = new Models.Options
            {
                BackupZip = BackupZip,
                CancelGranularity = CancelGranularity,
                LangFallbackEnabled = LangFallbackEnabled,
                LangFallbackSourceName = LangFallbackSourceName,
                LangFallbackTargetName = LangFallbackTargetName
            };

            if (BackupZip)
            {
                StatusBarText = "バックアップ作成中...";
                await executor.CreateBackupAsync(TargetDir, _cts.Token);
            }

            var outputRoot = string.IsNullOrEmpty(OutputRoot) ? TargetDir : OutputRoot;

            var progress = new Progress<ExecutionProgress>(UpdateExecutionProgress);

            var result = await Task.Run(() =>
                executor.ExecuteAsync(_scanResults, outputRoot, options, progress, _cts.Token),
                _cts.Token);

            StatusBarText = $"完了: 成功 {result.SuccessCount}, 警告 {result.WarningCount}, スキップ {result.SkipCount}, 失敗 {result.FailCount}, Cleanup失敗 {result.CleanupFailCount}";
            ProgressText = "実行完了";
            _logger.Info(StatusBarText);

            MessageBox.Show(
                $"実行が完了しました。\n\n" +
                $"成功: {result.SuccessCount}\n" +
                $"警告: {result.WarningCount}\n" +
                $"スキップ: {result.SkipCount}\n" +
                $"失敗: {result.FailCount}\n" +
                $"Cleanup失敗: {result.CleanupFailCount}",
                "実行結果", MessageBoxButton.OK,
                result.FailCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = "実行がキャンセルされました";
            _logger.Warn("実行がキャンセルされました");
        }
        catch (Exception ex)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = $"実行エラー: {ex.Message}";
            _logger.Error($"実行エラー: {ex.Message}");
        }
        finally
        {
            IsExecuting = false;
            ScanCompleted = false; // 実行後は再スキャン前提
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateExecutionProgress(ExecutionProgress progress)
    {
        ProgressPercent = progress.Total > 0
            ? (double)progress.Current / progress.Total * 100
            : 0;
        ProgressText = $"実行: {progress.Current}/{progress.Total} - {progress.JarName}";

        if (progress.Index < 0 || progress.Index >= Mods.Count)
            return;

        if (progress.Stage == ExecutionProgressStage.Started)
        {
            Mods[progress.Index].Status = ModStatus.Processing;
            return;
        }

        if (progress.FinalStatus is ModStatus finalStatus)
            Mods[progress.Index].Status = finalStatus;
    }

    private void MarkProcessingModsAsSkipped()
    {
        foreach (var mod in Mods.Where(m => m.Status == ModStatus.Processing))
            mod.Status = ModStatus.Skipped;
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusBarText = "キャンセル要求中...";
    }

    private async Task SaveLogAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
            FileName = $"mod-organizer-log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            Title = "ログを保存"
        };

        if (dialog.ShowDialog() == true)
        {
            await _logger.ExportAsync(dialog.FileName);
            _logger.Info($"ログ保存: {dialog.FileName}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            _isApplyingSettings = true;
            var settings = _settingsService.Load();
            var savedTarget = settings.TargetDir ?? string.Empty;
            var invalidSavedTarget = !string.IsNullOrWhiteSpace(savedTarget) && !Directory.Exists(savedTarget);

            OutputRootSameAsTarget = settings.OutputRootSameAsTarget;
            TargetDir = invalidSavedTarget ? string.Empty : savedTarget;

            if (settings.OutputRootSameAsTarget)
                OutputRoot = TargetDir;
            else
                OutputRoot = settings.OutputRoot ?? string.Empty;

            BackupZip = settings.BackupZip;
            CancelGranularity = settings.CancelGranularity;
            LangFallbackEnabled = settings.LangFallbackEnabled;
            LangFallbackSourceName = settings.LangFallbackSourceName;
            LangFallbackTargetName = settings.LangFallbackTargetName;

            if (invalidSavedTarget)
            {
                StatusBarText = $"保存済みの親フォルダが見つかりません。再選択してください: {savedTarget}";
                _logger.Warn($"保存済みフォルダが存在しません: {savedTarget}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"設定読込エラー: {ex.Message}");
        }
        finally
        {
            _isApplyingSettings = false;
            RefreshCommands();
        }
    }

    private void SaveSettings()
    {
        if (_isApplyingSettings || _disposed) return;

        try
        {
            _settingsService.ScheduleSave(BuildCurrentSettings());
        }
        catch (Exception ex)
        {
            _logger.Warn($"設定保存エラー: {ex.Message}");
        }
    }

    private AppSettings BuildCurrentSettings()
    {
        return new AppSettings
        {
            TargetDir = TargetDir,
            OutputRoot = OutputRoot,
            OutputRootSameAsTarget = OutputRootSameAsTarget,
            BackupZip = BackupZip,
            CancelGranularity = CancelGranularity,
            LangFallbackEnabled = LangFallbackEnabled,
            LangFallbackSourceName = LangFallbackSourceName,
            LangFallbackTargetName = LangFallbackTargetName
        };
    }

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        if (string.IsNullOrWhiteSpace(TargetDir) || !Directory.Exists(TargetDir))
            return;

        _watcher = new FileSystemWatcher(TargetDir, "*.jar")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnJarChanged;
        _watcher.Created += OnJarChanged;
        _watcher.Deleted += OnJarChanged;
        _watcher.Renamed += (_, _) => OnJarChanged(null, null!);
    }

    private void OnJarChanged(object? sender, FileSystemEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!ScanCompleted) return;

            SnapshotFresh = false;
            StatusBarText = "jarファイルの変更を検出しました。再スキャンしてください。";
            _logger.Warn($"jarファイル変更検出: {e?.Name ?? "不明"}");
        });
    }
}
