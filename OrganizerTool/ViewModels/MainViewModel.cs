using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinForms = System.Windows.Forms;
using Microsoft.Win32;
using OrganizerTool.Domain;
using OrganizerTool.Infrastructure;
using OrganizerTool.Models;

namespace OrganizerTool.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ModScanner _scanner = new();
    private readonly OperationPlanner _planner = new();
    private readonly Executor _executor = new(new FileSystem());

    private readonly Logger _logger;
    private readonly SettingsStore _settingsStore = new();

    private string _targetDir = "";
    private bool _isBusy;
    private double _progressValue;
    private string _progressText = "";

    private bool _dryRun;
    private bool _backupZip;
    private bool _jarMode;

    private MultiLangMode _multiLangMode = MultiLangMode.FirstOnly;
    private DeleteMode _deleteMode = DeleteMode.Permanent;

    private bool _suppressSettingsSave;

    private bool _scanCompleted;
    private bool _cancelRequested;

    public MainViewModel()
    {
        _logger = new Logger(Logs);

        LoadSettings();

        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        ScanCommand = new RelayCommand(async () => await ScanAsync(), () => !IsBusy && Directory.Exists(TargetDir));
        ExecuteCommand = new RelayCommand(async () => await ExecuteAsync(), () => !IsBusy && ScanCompleted && Mods.Count > 0);
        CancelCommand = new RelayCommand(RequestCancel, () => IsBusy);
        SaveLogCommand = new RelayCommand(SaveLog, () => Logs.Count > 0);

        Logs.CollectionChanged += (_, _) => SaveLogCommand.RaiseCanExecuteChanged();
        Mods.CollectionChanged += (_, _) => ExecuteCommand.RaiseCanExecuteChanged();
    }

    public ObservableCollection<ModItemViewModel> Mods { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();

    public RelayCommand BrowseCommand { get; }
    public RelayCommand ScanCommand { get; }
    public RelayCommand ExecuteCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveLogCommand { get; }

    public string TargetDir
    {
        get => _targetDir;
        set
        {
            if (SetProperty(ref _targetDir, value))
            {
                ScanCompleted = false;
                RaiseCanExecuteAll();
                SaveSettings();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCanExecuteAll();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public bool DryRun
    {
        get => _dryRun;
        set
        {
            if (SetProperty(ref _dryRun, value))
            {
                RecalculatePlans();
                SaveSettings();
            }
        }
    }

    public bool BackupZip
    {
        get => _backupZip;
        set
        {
            if (SetProperty(ref _backupZip, value))
            {
                SaveSettings();
            }
        }
    }

    public bool JarMode
    {
        get => _jarMode;
        set
        {
            if (SetProperty(ref _jarMode, value))
            {
                // スキャン結果の対象範囲が変わるため、再スキャンを促す
                ScanCompleted = false;
                RaiseCanExecuteAll();
                SaveSettings();
            }
        }
    }

    public bool MultiLangFirstOnly
    {
        get => _multiLangMode == MultiLangMode.FirstOnly;
        set
        {
            if (value)
            {
                _multiLangMode = MultiLangMode.FirstOnly;
                OnPropertyChanged(nameof(MultiLangFirstOnly));
                OnPropertyChanged(nameof(MultiLangMergeAll));
                OnPropertyChanged(nameof(MultiLangSeparateFolders));
                RecalculatePlans();
                SaveSettings();
            }
        }
    }

    public bool MultiLangMergeAll
    {
        get => _multiLangMode == MultiLangMode.MergeAll;
        set
        {
            if (value)
            {
                _multiLangMode = MultiLangMode.MergeAll;
                OnPropertyChanged(nameof(MultiLangFirstOnly));
                OnPropertyChanged(nameof(MultiLangMergeAll));
                OnPropertyChanged(nameof(MultiLangSeparateFolders));
                RecalculatePlans();
                SaveSettings();
            }
        }
    }

    public bool MultiLangSeparateFolders
    {
        get => _multiLangMode == MultiLangMode.SeparateFolders;
        set
        {
            if (value)
            {
                _multiLangMode = MultiLangMode.SeparateFolders;
                OnPropertyChanged(nameof(MultiLangFirstOnly));
                OnPropertyChanged(nameof(MultiLangMergeAll));
                OnPropertyChanged(nameof(MultiLangSeparateFolders));
                RecalculatePlans();
                SaveSettings();
            }
        }
    }

    public bool DeletePermanent
    {
        get => _deleteMode == DeleteMode.Permanent;
        set
        {
            if (value)
            {
                _deleteMode = DeleteMode.Permanent;
                OnPropertyChanged(nameof(DeletePermanent));
                OnPropertyChanged(nameof(DeleteRecycleBin));
                SaveSettings();
            }
        }
    }

    public bool DeleteRecycleBin
    {
        get => _deleteMode == DeleteMode.RecycleBin;
        set
        {
            if (value)
            {
                _deleteMode = DeleteMode.RecycleBin;
                OnPropertyChanged(nameof(DeletePermanent));
                OnPropertyChanged(nameof(DeleteRecycleBin));
                SaveSettings();
            }
        }
    }

    public bool ScanCompleted
    {
        get => _scanCompleted;
        private set
        {
            if (SetProperty(ref _scanCompleted, value))
            {
                RaiseCanExecuteAll();
            }
        }
    }

    private AppOptions CurrentOptions() => new()
    {
        DryRun = DryRun,
        JarMode = JarMode,
        BackupZip = BackupZip,
        MultiLangMode = _multiLangMode,
        DeleteMode = _deleteMode,
    };

    private void LoadSettings()
    {
        _suppressSettingsSave = true;

        try
        {
            var settings = _settingsStore.LoadOrDefault();

            TargetDir = settings.TargetDir;
            DryRun = settings.DryRun;
            BackupZip = settings.BackupZip;
            JarMode = settings.JarMode;

            _multiLangMode = settings.MultiLangMode;
            OnPropertyChanged(nameof(MultiLangFirstOnly));
            OnPropertyChanged(nameof(MultiLangMergeAll));
            OnPropertyChanged(nameof(MultiLangSeparateFolders));

            _deleteMode = settings.DeleteMode;
            OnPropertyChanged(nameof(DeletePermanent));
            OnPropertyChanged(nameof(DeleteRecycleBin));
        }
        finally
        {
            _suppressSettingsSave = false;
        }
    }

    private void SaveSettings()
    {
        if (_suppressSettingsSave)
        {
            return;
        }

        var settings = new PersistedSettings
        {
            TargetDir = TargetDir,
            DryRun = DryRun,
            BackupZip = BackupZip,
            JarMode = JarMode,
            MultiLangMode = _multiLangMode,
            DeleteMode = _deleteMode,
        };

        _settingsStore.TrySave(settings);
    }

    private void Browse()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Modフォルダ群の親フォルダを選択してください",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        var result = dialog.ShowDialog();
        if (result == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            TargetDir = dialog.SelectedPath;
            _logger.Info($"TargetDir set: {TargetDir}");
        }

        RaiseCanExecuteAll();
    }

    private async Task ScanAsync()
    {
        if (!Directory.Exists(TargetDir))
        {
            return;
        }

        IsBusy = true;
        _cancelRequested = false;
        ScanCompleted = false;
        ProgressValue = 0;
        ProgressText = "スキャン中...";

        Logs.Clear();
        Mods.Clear();

        _logger.Info($"Scan start: {TargetDir}");

        try
        {
            var includeJar = JarMode;
            var results = await Task.Run(() => _scanner.Scan(TargetDir, includeJar, _ => _cancelRequested));

            foreach (var r in results)
            {
                var vm = new ModItemViewModel
                {
                    ModPath = r.ModPath,
                    LangCandidates = r.LangCandidates,
                    SourceType = r.SourceType,
                    ModName = r.ModName,
                    AssetsExists = r.AssetsExists,
                    LangCount = r.LangCandidates.Count,
                };

                Mods.Add(vm);
            }

            RecalculatePlans();

            ScanCompleted = true;
            _logger.Info($"Scan done: {Mods.Count} mods");
        }
        catch (Exception ex)
        {
            _logger.Error($"Scan failed: {ex.Message}");
            ScanCompleted = false;
        }
        finally
        {
            ProgressText = "スキャン完了";
            ProgressValue = 0;
            IsBusy = false;
        }
    }

    private void RecalculatePlans()
    {
        if (Mods.Count == 0)
        {
            return;
        }

        var options = CurrentOptions();

        foreach (var mod in Mods)
        {
            var scan = new ModScanResult
            {
                SourceType = mod.SourceType,
                ModName = mod.ModName,
                ModPath = mod.ModPath,
                AssetsExists = mod.AssetsExists,
                LangCandidates = mod.LangCandidates,
            };

            var plan = _planner.BuildPlan(scan, options, backupRootDir: null);
            mod.PolicyLabel = plan.PolicyLabel;
            mod.PlannedMoves = plan.PlannedMoves;
            mod.PlannedDeletes = plan.PlannedDeletes;
        }
    }

    private void RequestCancel()
    {
        _cancelRequested = true;
        _logger.Warn("Cancel requested (will stop after current mod)");
    }

    private async Task ExecuteAsync()
    {
        if (!ScanCompleted || Mods.Count == 0)
        {
            return;
        }

        if (!Directory.Exists(TargetDir))
        {
            return;
        }

        var langNoneCount = Mods.Count(m => m.LangCount == 0);
        var jarCount = Mods.Count(m => m.SourceType == ModSourceType.Jar);
        var jarOut = TargetDir;

        var confirmMessage =
            $"実行してよろしいですか？\n\n" +
            $"対象: {TargetDir}\n" +
            $"Mod数: {Mods.Count}\n" +
            $"jar数: {jarCount}（jarは削除/改変せず、langのみ抽出します）\n" +
            $"langなし: {langNoneCount}（該当Modは中身が全削除され空フォルダになります）\n\n" +
            $"ドライラン: {(DryRun ? "ON" : "OFF")}\n" +
            $"jarモード: {(JarMode ? "ON" : "OFF")}\n" +
            $"jar出力先: {jarOut}（jar名ごとに <jar名>/lang/ へ抽出）\n" +
            $"複数lang時: {(_multiLangMode switch { MultiLangMode.FirstOnly => "最初の1件", MultiLangMode.MergeAll => "全候補を統合", MultiLangMode.SeparateFolders => "個別に抽出", _ => "(不明)" })}\n" +
            $"削除方式: {(_deleteMode == DeleteMode.Permanent ? "完全削除" : "ゴミ箱") }\n" +
            $"バックアップZip: {(BackupZip ? "ON" : "OFF")}\n";

        var confirm = System.Windows.MessageBox.Show(confirmMessage, "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            _logger.Info("Execution cancelled by user.");
            return;
        }

        IsBusy = true;
        _cancelRequested = false;
        ProgressValue = 0;

        var options = CurrentOptions();

        // バックアップは実行単位でまとめて作る
        var backupRunDir = options.BackupZip
            ? Path.Combine(TargetDir, "_backup", DateTime.Now.ToString("yyyyMMdd_HHmmss"))
            : null;

        _logger.Info("Execute start");

        var success = 0;
        var warning = 0;
        var failed = 0;

        try
        {
            for (var i = 0; i < Mods.Count; i++)
            {
                var mod = Mods[i];
                ProgressValue = (double)i / Mods.Count * 100.0;
                ProgressText = $"処理中: {mod.ModName} ({i + 1}/{Mods.Count})";

                // plan 作成
                var scan = new ModScanResult
                {
                    SourceType = mod.SourceType,
                    ModName = mod.ModName,
                    ModPath = mod.ModPath,
                    AssetsExists = mod.AssetsExists,
                    LangCandidates = mod.LangCandidates,
                };

                var plan = _planner.BuildPlan(scan, options, backupRunDir);

                try
                {
                    await _executor.ExecuteAsync(
                        plan,
                        options,
                        logInfo: _logger.Info,
                        logWarn: _logger.Warn,
                        logError: _logger.Error,
                        cancellationToken: CancellationToken.None);

                    if (plan.LangCandidates.Count == 0)
                    {
                        mod.Status = ModStatus.Warning;
                        warning++;
                    }
                    else
                    {
                        mod.Status = ModStatus.Success;
                        success++;
                    }

                    mod.RefreshStatusLabel();
                }
                catch (Exception ex)
                {
                    mod.Status = ModStatus.Failed;
                    mod.RefreshStatusLabel();
                    failed++;
                    _logger.Error($"Mod failed: {mod.ModName} / {ex.Message}");
                }

                if (_cancelRequested)
                {
                    _logger.Warn("Cancel detected. Stop after current mod.");

                    for (var j = i + 1; j < Mods.Count; j++)
                    {
                        Mods[j].Status = ModStatus.Cancelled;
                        Mods[j].RefreshStatusLabel();
                    }

                    break;
                }
            }

            ProgressValue = 100;
            ProgressText = "完了";

            _logger.Info($"Summary: success={success}, warning={warning}, failed={failed}");

            System.Windows.MessageBox.Show(
                $"完了しました。\n\n成功: {success}\n警告: {warning}\n失敗: {failed}",
                "実行結果",
                MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            IsBusy = false;
            RaiseCanExecuteAll();
        }
    }

    private void SaveLog()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "ログ保存",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "run-log.txt",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _logger.ExportText());
            _logger.Info($"Log saved: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            _logger.Error($"Log save failed: {ex.Message}");
        }
    }

    private void RaiseCanExecuteAll()
    {
        BrowseCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        ExecuteCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SaveLogCommand.RaiseCanExecuteChanged();
    }
}
