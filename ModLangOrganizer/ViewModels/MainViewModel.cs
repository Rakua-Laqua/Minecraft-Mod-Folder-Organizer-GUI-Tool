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
    private string _activeActionLabel = "実行";

    // スキャン結果保持
    private List<JarScanResult> _scanResults = [];

    public MainViewModel()
    {
        _settingsService = new SettingsService(new SettingsStore(), message => _logger.Warn(message));

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        BrowseOutputCommand = new RelayCommand(BrowseOutput);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanScan);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecuteMainAction);
        ImportCommand = new AsyncRelayCommand(ImportAsync, CanExecuteMainAction);
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
                OutputRoot = JarPathPolicy.GetDefaultOutputRoot(normalized);

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
                OutputRoot = JarPathPolicy.GetDefaultOutputRoot(TargetDir);

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
    public ICommand ImportCommand { get; }
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
            Title = "Mod jarファイルを含む親フォルダを選択"
        };

        if (dialog.ShowDialog() == true)
            TargetDir = dialog.FolderName;
    }

    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "lang入出力ルートフォルダを選択"
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

    private string ResolveOutputRoot()
    {
        if (!string.IsNullOrWhiteSpace(OutputRoot))
            return OutputRoot;

        return JarPathPolicy.GetDefaultOutputRoot(TargetDir);
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
            var outputRoot = ResolveOutputRoot();
            var jars = _scanner.EnumerateJars(TargetDir, outputRoot);
            JarCount = jars.Count;
            _logger.Info($"jarファイル {jars.Count} 件を再帰検出: {TargetDir}");
            _logger.Info($"lang入出力ルート: {outputRoot}");

            await Task.Run(() =>
            {
                for (int i = 0; i < jars.Count; i++)
                {
                    _cts!.Token.ThrowIfCancellationRequested();

                    var result = _scanner.ScanJar(jars[i], TargetDir, outputRoot);
                    _scanResults.Add(result);
                    var displayJar = JarPathPolicy.ToDisplayPath(result.RelativeJarPath);

                    var vm = ModItemViewModel.FromScanResult(result);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        Mods.Add(vm);
                        ProgressPercent = jars.Count > 0 ? (double)(i + 1) / jars.Count * 100 : 0;
                        ProgressText = $"スキャン: {i + 1}/{jars.Count} - {displayJar}";
                    });

                    if (result.Integrity == JarIntegrity.Corrupted)
                        _logger.Error($"破損: {displayJar} - {result.ErrorMessage}");
                    else if (result.Strategy == ProcessingStrategy.NoLang)
                        _logger.Info($"langなし: {displayJar}");
                    else
                        _logger.Info($"lang検出: {displayJar} ({result.LangCandidates.Count}候補)");
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

        if (!EnsureScanSnapshotFresh())
            return;

        var langJars = _scanResults.Where(r => r.Strategy == ProcessingStrategy.LangFound).ToList();
        var outputRoot = ResolveOutputRoot();
        if (MessageBox.Show(
            $"JARからlangファイルを抽出します。\n" +
            $"- 対象jar: {langJars.Count}件\n" +
            $"- 出力先: {outputRoot}\n" +
            $"- 元フォルダの相対構造: 保持\n" +
            $"- バックアップ: {(BackupZip ? "あり" : "なし")}\n" +
            $"- langフォールバック: {(LangFallbackEnabled ? $"あり ({LangFallbackSourceName} → {LangFallbackTargetName})" : "なし")}\n\n実行しますか？",
            "lang抽出の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsExecuting = true;
        _activeActionLabel = "lang抽出";
        ProgressPercent = 0;
        StatusBarText = "lang抽出中...";
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

            var progress = new Progress<ExecutionProgress>(UpdateExecutionProgress);

            var result = await Task.Run(() =>
                executor.ExecuteAsync(_scanResults, outputRoot, options, progress, _cts.Token),
                _cts.Token);

            StatusBarText = $"lang抽出完了: 成功 {result.SuccessCount}, 警告 {result.WarningCount}, スキップ {result.SkipCount}, 失敗 {result.FailCount}, Cleanup失敗 {result.CleanupFailCount}";
            ProgressText = "lang抽出完了";
            _logger.Info(StatusBarText);

            MessageBox.Show(
                $"langの抽出が完了しました。\n\n" +
                $"成功: {result.SuccessCount}\n" +
                $"警告: {result.WarningCount}\n" +
                $"スキップ: {result.SkipCount}\n" +
                $"失敗: {result.FailCount}\n" +
                $"Cleanup失敗: {result.CleanupFailCount}\n\n" +
                $"出力先: {outputRoot}",
                "lang抽出結果", MessageBoxButton.OK,
                result.FailCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = "lang抽出がキャンセルされました";
            _logger.Warn("lang抽出がキャンセルされました");
        }
        catch (Exception ex)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = $"lang抽出エラー: {ex.Message}";
            _logger.Error($"lang抽出エラー: {ex.Message}");
        }
        finally
        {
            IsExecuting = false;
            ScanCompleted = false; // 実行後は再スキャン前提
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ImportAsync()
    {
        if (_scanResults.Count == 0)
            return;

        if (!EnsureScanSnapshotFresh())
            return;

        var outputRoot = ResolveOutputRoot();
        var importer = new JarLangImporter(_logger);
        var plan = importer.CreatePlan(_scanResults, outputRoot);

        if (plan.SourceFileCount == 0)
        {
            StatusBarText = "JARへ反映できるlangファイルが見つかりません。";
            _logger.Warn($"反映元ファイルなし: {outputRoot}");
            MessageBox.Show(
                $"反映元のlangファイルが見つかりません。\n\n" +
                $"反映元: {outputRoot}\n" +
                "先にlangを抽出するか、出力ルートを確認してください。",
                "反映元なし", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var signatureWarning = plan.SignedJarCount > 0
            ? $"\n- 署名付きjar: {plan.SignedJarCount}件（署名検証に影響する可能性があります）"
            : string.Empty;
        var conflictCopyNotice = plan.IgnoredConflictFileCount > 0
            ? $"\n- 除外する競合コピー: {plan.IgnoredConflictFileCount}件"
            : string.Empty;

        if (MessageBox.Show(
            $"外部のlangファイルをJARへ追加・更新します。\n" +
            $"- 対象jar: {plan.ImportableJarCount}件\n" +
            $"- 反映ファイル: {plan.SourceFileCount}件\n" +
            $"- 反映元: {outputRoot}\n" +
            $"- 元フォルダの相対構造: 保持\n" +
            $"- バックアップ: {(BackupZip ? "あり" : "なし（元に戻すには再取得が必要です）")}" +
            signatureWarning +
            conflictCopyNotice +
            "\n\nJAR内の同一パスに異なる内容がある場合は更新されます。実行しますか？",
            "JAR反映の確認", MessageBoxButton.YesNo,
            plan.SignedJarCount > 0 || !BackupZip
                ? MessageBoxImage.Warning
                : MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsExecuting = true;
        _activeActionLabel = "JAR反映";
        ProgressPercent = 0;
        StatusBarText = "JARへ反映中...";
        _cts = new CancellationTokenSource();

        try
        {
            var options = new Models.Options
            {
                BackupZip = BackupZip,
                CancelGranularity = CancelGranularity
            };

            if (BackupZip)
            {
                StatusBarText = "バックアップ作成中...";
                var backupExecutor = new Executor(_logger);
                await backupExecutor.CreateBackupAsync(TargetDir, _cts.Token);
            }

            var progress = new Progress<ExecutionProgress>(UpdateExecutionProgress);
            var result = await Task.Run(() =>
                importer.ImportAsync(plan, options, progress, _cts.Token),
                _cts.Token);

            StatusBarText =
                $"JAR反映完了: 追加 {result.AddedFileCount}, 更新 {result.UpdatedFileCount}, " +
                $"変更なし {result.UnchangedFileCount}, 警告 {result.WarningCount}, " +
                $"スキップ {result.SkipCount}, 失敗 {result.FailCount}";
            ProgressText = "JAR反映完了";
            _logger.Info(StatusBarText);

            MessageBox.Show(
                $"JARへの反映が完了しました。\n\n" +
                $"追加ファイル: {result.AddedFileCount}\n" +
                $"更新ファイル: {result.UpdatedFileCount}\n" +
                $"同一内容: {result.UnchangedFileCount}\n" +
                $"変更なしjar: {result.UnchangedJarCount}\n" +
                $"警告: {result.WarningCount}\n" +
                $"スキップ: {result.SkipCount}\n" +
                $"失敗: {result.FailCount}",
                "JAR反映結果", MessageBoxButton.OK,
                result.FailCount > 0 || result.WarningCount > 0
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = "JAR反映がキャンセルされました";
            _logger.Warn("JAR反映がキャンセルされました");
        }
        catch (Exception ex)
        {
            MarkProcessingModsAsSkipped();
            StatusBarText = $"JAR反映エラー: {ex.Message}";
            _logger.Error($"JAR反映エラー: {ex.Message}");
        }
        finally
        {
            IsExecuting = false;
            ScanCompleted = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool EnsureScanSnapshotFresh()
    {
        var staleJars = _snapshotValidator.Validate(_scanResults);

        // Watcherで取りこぼしても、新規追加JARを実行直前に検出する。
        var outputRoot = ResolveOutputRoot();
        var currentJars = _scanner.EnumerateJars(TargetDir, outputRoot);
        var scannedPaths = _scanResults
            .Select(r => Path.GetFullPath(r.JarFilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedJars = currentJars
            .Where(path => !scannedPaths.Contains(Path.GetFullPath(path)))
            .Select(path => JarPathPolicy.ToDisplayPath(
                JarPathPolicy.GetRelativeJarPath(TargetDir, path)))
            .ToList();

        var changes = staleJars
            .Concat(addedJars.Select(path => $"追加: {path}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (changes.Count == 0)
            return true;

        SnapshotFresh = false;
        var names = string.Join(", ", changes);
        StatusBarText = $"変更検出: {names} -> 再スキャンが必要です";
        _logger.Warn($"jarファイル変更検出: {names}");
        MessageBox.Show(
            $"以下のjarファイルがスキャン後に変更されています。\n再スキャンしてください。\n\n{string.Join(Environment.NewLine, changes)}",
            "再スキャンが必要", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void UpdateExecutionProgress(ExecutionProgress progress)
    {
        ProgressPercent = progress.Total > 0
            ? (double)progress.Current / progress.Total * 100
            : 0;
        ProgressText = $"{_activeActionLabel}: {progress.Current}/{progress.Total} - {progress.JarName}";

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
                OutputRoot = JarPathPolicy.GetDefaultOutputRoot(TargetDir);
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
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnJarChanged;
        _watcher.Created += OnJarChanged;
        _watcher.Deleted += OnJarChanged;
        _watcher.Renamed += (sender, e) => OnJarChanged(sender, e);
    }

    private void OnJarChanged(object? sender, FileSystemEventArgs e)
    {
        if (JarPathPolicy.ShouldIgnoreWatchPath(e.FullPath, TargetDir, ResolveOutputRoot()))
            return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!ScanCompleted) return;

            var displayPath = e.Name ?? "不明";
            try
            {
                displayPath = JarPathPolicy.ToDisplayPath(
                    Path.GetRelativePath(TargetDir, e.FullPath));
            }
            catch
            {
                // 表示用の相対化に失敗しても変更検出自体は有効。
            }

            SnapshotFresh = false;
            StatusBarText = "jarファイルの変更を検出しました。再スキャンしてください。";
            _logger.Warn($"jarファイル変更検出: {displayPath}");
        });
    }
}
