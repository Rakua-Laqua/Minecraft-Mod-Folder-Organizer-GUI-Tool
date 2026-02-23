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

/// <summary>メインウィンドウViewModel</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly JarScanner _scanner = new();
    private readonly SnapshotValidator _snapshotValidator = new();
    private readonly Logger _logger = new();

    private string _targetDir = string.Empty;
    private string _outputRoot = string.Empty;
    private bool _outputRootSameAsTarget = true;
    private bool _backupZip;
    private CancelGranularity _cancelGranularity = CancelGranularity.PerJar;
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

    // スキャン結果保持
    private List<JarScanResult> _scanResults = [];

    public MainViewModel()
    {
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !string.IsNullOrEmpty(TargetDir) && !IsScanning && !IsExecuting);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => ScanCompleted && SnapshotFresh && !IsExecuting && !IsScanning);
        CancelCommand = new RelayCommand(Cancel, () => IsScanning || IsExecuting);
        SaveLogCommand = new AsyncRelayCommand(SaveLogAsync);

        _logger.LogAdded += entry =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(LogEntries));
            });
        };
    }

    // ---- Properties ----

    public string TargetDir
    {
        get => _targetDir;
        set
        {
            if (SetProperty(ref _targetDir, value))
            {
                if (_outputRootSameAsTarget)
                    OutputRoot = value;
                ScanCompleted = false;
                JarCount = 0;
                SetupWatcher();
                StatusBarText = string.IsNullOrEmpty(value) ? "フォルダを選択してください" : $"選択済: {value}   [スキャン] を押してください";
            }
        }
    }

    public string OutputRoot
    {
        get => _outputRoot;
        set => SetProperty(ref _outputRoot, value);
    }

    public bool OutputRootSameAsTarget
    {
        get => _outputRootSameAsTarget;
        set
        {
            if (SetProperty(ref _outputRootSameAsTarget, value) && value)
                OutputRoot = TargetDir;
        }
    }

    public bool BackupZip
    {
        get => _backupZip;
        set => SetProperty(ref _backupZip, value);
    }

    public CancelGranularity CancelGranularity
    {
        get => _cancelGranularity;
        set => SetProperty(ref _cancelGranularity, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        set => SetProperty(ref _isExecuting, value);
    }

    public bool ScanCompleted
    {
        get => _scanCompleted;
        set => SetProperty(ref _scanCompleted, value);
    }

    public bool SnapshotFresh
    {
        get => _snapshotFresh;
        set => SetProperty(ref _snapshotFresh, value);
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

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Mod jarファイルがあるフォルダを選択"
        };

        if (dialog.ShowDialog() == true)
        {
            TargetDir = dialog.FolderName;
        }
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

    private async Task ScanAsync()
    {
        if (string.IsNullOrEmpty(TargetDir) || !Directory.Exists(TargetDir)) return;

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
                    _cts.Token.ThrowIfCancellationRequested();

                    var result = _scanner.ScanJar(jars[i], outputRoot);
                    _scanResults.Add(result);

                    var vm = ModItemViewModel.FromScanResult(result);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Mods.Add(vm);
                        ProgressPercent = (double)(i + 1) / jars.Count * 100;
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
        if (_scanResults.Count == 0) return;

        // 整合性チェック
        var staleJars = _snapshotValidator.Validate(_scanResults);
        if (staleJars.Count > 0)
        {
            SnapshotFresh = false;
            var names = string.Join(", ", staleJars);
            StatusBarText = $"変更検出: {names} → 再スキャンが必要です";
            _logger.Warn($"jarファイル変更検出: {names}");
            MessageBox.Show(
                $"以下のjarファイルがスキャン後に変更されています。再スキャンしてください。\n\n{names}",
                "再スキャンが必要", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 確認ダイアログ
        var langJars = _scanResults.Where(r => r.Strategy == ProcessingStrategy.LangFound).ToList();
        if (MessageBox.Show(
            $"以下の処理を実行します:\n" +
            $"• 対象jar: {langJars.Count}件\n" +
            $"• 出力先: {OutputRoot}\n" +
            $"• バックアップ: {(BackupZip ? "あり" : "なし")}\n\n実行しますか？",
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
                CancelGranularity = CancelGranularity
            };

            // バックアップ
            if (BackupZip)
            {
                StatusBarText = "バックアップ作成中...";
                await executor.CreateBackupAsync(TargetDir, _cts.Token);
            }

            var outputRoot = string.IsNullOrEmpty(OutputRoot) ? TargetDir : OutputRoot;

            var progress = new Progress<(int current, int total, string jarName)>(p =>
            {
                ProgressPercent = p.total > 0 ? (double)p.current / p.total * 100 : 0;
                ProgressText = $"実行: {p.current}/{p.total} - {p.jarName}";

                // ステータス更新
                if (p.current < Mods.Count)
                    Mods[p.current].Status = ModStatus.Processing;
                if (p.current > 0 && p.current - 1 < Mods.Count)
                {
                    var prev = Mods[p.current - 1];
                    if (prev.Status == ModStatus.Processing)
                        prev.Status = ModStatus.Success;
                }
            });

            var result = await Task.Run(() =>
                executor.ExecuteAsync(_scanResults, outputRoot, options, progress, _cts.Token),
                _cts.Token);

            // 最終ステータス更新
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
            StatusBarText = "実行がキャンセルされました";
            _logger.Warn("実行がキャンセルされました");
        }
        catch (Exception ex)
        {
            StatusBarText = $"実行エラー: {ex.Message}";
            _logger.Error($"実行エラー: {ex.Message}");
        }
        finally
        {
            IsExecuting = false;
            ScanCompleted = false; // 再スキャン必要
            _cts?.Dispose();
            _cts = null;
        }
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

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        if (string.IsNullOrEmpty(TargetDir) || !Directory.Exists(TargetDir)) return;

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
            if (ScanCompleted)
            {
                SnapshotFresh = false;
                StatusBarText = "⚠ jarファイルの変更を検出しました。再スキャンを推奨します。";
                _logger.Warn($"jarファイル変更検出: {e?.Name ?? "不明"}");
            }
        });
    }
}
