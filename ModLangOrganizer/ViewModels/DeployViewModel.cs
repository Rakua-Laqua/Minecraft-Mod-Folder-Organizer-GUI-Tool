using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModLangOrganizer.Helpers;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.ViewModels;

/// <summary>MOD構成デプロイ (Copy Manager) 画面のViewModel</summary>
public sealed class DeployViewModel : ObservableObject
{
    private readonly ModRepositoryScanner _scanner = new();
    private readonly ModDeploymentService _deploymentService = new();
    private readonly ModProfileStore _profileStore = new();
    private readonly ModDeploySettingsStore _settingsStore = new();
    private readonly Dictionary<ModTreeNodeModel, ModTreeNodeModel?> _parents = new();

    private string _repoPath = @"D:\Minecraft_mod_management";
    private string _modsTargetDir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\.minecraft\mods");
    private bool _showDisabledFolders = true;
    private string _searchFilter = string.Empty;
    private DeployMode _selectedMode = DeployMode.Sync;
    private bool _createBackup = true;
    private bool _isDiffDetailsExpanded = true;
    private string _selectionSummary = "選択中: 0 個のMOD (0 B)";
    private int _diffNewCount;
    private int _diffUpdateCount;
    private int _diffDeleteCount;
    private int _diffKeepCount;
    private ModProfileModel? _selectedProfile;
    private bool _hasConflicts;
    private string _conflictSummary = string.Empty;
    private bool _isBusy;
    private int _deployProgressPercent;
    private string _deployProgressText = "待機中";
    private bool _updatingChecks;
    private bool _isInitializing;
    private List<string> _savedSelectionBeforeScan = new();
    private string _savedProfileName = string.Empty;
    private List<DeployConflictModel> _currentConflicts = new();
    private DeployPlanModel? _currentPlan;

    public ObservableCollection<ModProfileModel> Profiles { get; } = new();
    public ObservableCollection<ModTreeNodeModel> ModTreeRoots { get; } = new();
    public ObservableCollection<DiffItemModel> DiffItems { get; } = new();
    public ObservableCollection<string> ActivityLogs { get; } = new();

    public DeployViewModel()
    {
        _isInitializing = true;
        InitializeCommands();
        LoadProfiles();
        LoadSettings();
        _isInitializing = false;

        RecalculateSelection();
        RefreshPreview();

        var expandedRepoPath = Environment.ExpandEnvironmentVariables(RepoPath);
        if (Directory.Exists(expandedRepoPath))
            _ = RescanRepositoryAsync(restorePersistedSelection: true);
    }

    #region Properties

    public string RepoPath
    {
        get => _repoPath;
        set
        {
            if (SetProperty(ref _repoPath, value ?? string.Empty))
                SaveSettings();
        }
    }

    public string ModsTargetDir
    {
        get => _modsTargetDir;
        set
        {
            if (!SetProperty(ref _modsTargetDir, value ?? string.Empty))
                return;

            RefreshPreview();
            SaveSettings();
        }
    }

    public bool ShowDisabledFolders
    {
        get => _showDisabledFolders;
        set
        {
            if (!SetProperty(ref _showDisabledFolders, value))
                return;

            ApplyFilter(SearchFilter);
            SaveSettings();
        }
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value ?? string.Empty))
                ApplyFilter(_searchFilter);
        }
    }

    public DeployMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value))
                return;

            OnPropertyChanged(nameof(DeployButtonText));
            RefreshPreview();
            SaveSettings();
        }
    }

    public bool CreateBackup
    {
        get => _createBackup;
        set
        {
            if (SetProperty(ref _createBackup, value))
                SaveSettings();
        }
    }

    public bool IsDiffDetailsExpanded
    {
        get => _isDiffDetailsExpanded;
        set
        {
            if (SetProperty(ref _isDiffDetailsExpanded, value))
                SaveSettings();
        }
    }

    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => SetProperty(ref _selectionSummary, value);
    }

    public int DiffNewCount
    {
        get => _diffNewCount;
        private set => SetProperty(ref _diffNewCount, value);
    }

    public int DiffUpdateCount
    {
        get => _diffUpdateCount;
        private set => SetProperty(ref _diffUpdateCount, value);
    }

    public int DiffDeleteCount
    {
        get => _diffDeleteCount;
        private set => SetProperty(ref _diffDeleteCount, value);
    }

    public int DiffKeepCount
    {
        get => _diffKeepCount;
        private set => SetProperty(ref _diffKeepCount, value);
    }

    public ModProfileModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value))
                return;

            if (_isInitializing || value is null)
                return;

            _savedProfileName = value.Name;
            ApplyProfile(value);
            AddLog($"プロファイル「{value.Name}」を適用しました ({value.SelectedRelativePaths.Count} 個のMOD)。");
            SaveSettings();
        }
    }

    public bool HasConflicts
    {
        get => _hasConflicts;
        private set => SetProperty(ref _hasConflicts, value);
    }

    public string ConflictSummary
    {
        get => _conflictSummary;
        private set => SetProperty(ref _conflictSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public int DeployProgressPercent
    {
        get => _deployProgressPercent;
        set => SetProperty(ref _deployProgressPercent, value);
    }

    public string DeployProgressText
    {
        get => _deployProgressText;
        set => SetProperty(ref _deployProgressText, value);
    }

    public string DeployButtonText =>
        SelectedMode == DeployMode.Sync
            ? "modsフォルダへ反映（同期デプロイ）"
            : "modsフォルダへ反映（追加デプロイ）";

    #endregion

    #region Commands

    public ICommand BrowseRepoCommand { get; private set; } = null!;
    public ICommand BrowseModsTargetCommand { get; private set; } = null!;
    public ICommand SetDefaultModsDirCommand { get; private set; } = null!;
    public ICommand RescanRepoCommand { get; private set; } = null!;
    public ICommand OpenRepoFolderCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand DeselectAllCommand { get; private set; } = null!;
    public ICommand InvertSelectionCommand { get; private set; } = null!;
    public ICommand ExpandAllCommand { get; private set; } = null!;
    public ICommand CollapseAllCommand { get; private set; } = null!;
    public ICommand ToggleDiffDetailsCommand { get; private set; } = null!;
    public ICommand NewProfileCommand { get; private set; } = null!;
    public ICommand SaveProfileCommand { get; private set; } = null!;
    public ICommand DeleteProfileCommand { get; private set; } = null!;
    public ICommand DeployCommand { get; private set; } = null!;
    public ICommand ClearSearchCommand { get; private set; } = null!;
    public ICommand ShowConflictsCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        BrowseRepoCommand = new AsyncRelayCommand(BrowseRepoAsync, () => !IsBusy);
        BrowseModsTargetCommand = new RelayCommand(BrowseModsTarget, () => !IsBusy);
        SetDefaultModsDirCommand = new RelayCommand(() =>
        {
            ModsTargetDir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\.minecraft\mods");
            AddLog("適用先をMinecraft標準のmodsフォルダに設定しました。");
        }, () => !IsBusy);
        RescanRepoCommand = new AsyncRelayCommand(() => RescanRepositoryAsync(restorePersistedSelection: false), () => !IsBusy);
        OpenRepoFolderCommand = new RelayCommand(OpenRepoFolder, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetCheckAll(true), () => !IsBusy && ModTreeRoots.Count > 0);
        DeselectAllCommand = new RelayCommand(() => SetCheckAll(false), () => !IsBusy && ModTreeRoots.Count > 0);
        InvertSelectionCommand = new RelayCommand(InvertAll, () => !IsBusy && ModTreeRoots.Count > 0);
        ExpandAllCommand = new RelayCommand(() => SetExpandAll(true), () => ModTreeRoots.Count > 0);
        CollapseAllCommand = new RelayCommand(() => SetExpandAll(false), () => ModTreeRoots.Count > 0);
        ToggleDiffDetailsCommand = new RelayCommand(() => IsDiffDetailsExpanded = !IsDiffDetailsExpanded);
        ClearSearchCommand = new RelayCommand(() => SearchFilter = string.Empty);
        NewProfileCommand = new RelayCommand(NewProfile, () => !IsBusy);
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !IsBusy && SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => !IsBusy && SelectedProfile is not null);
        DeployCommand = new AsyncRelayCommand(DeployAsync, CanDeploy);
        ShowConflictsCommand = new RelayCommand(ShowConflictDetails, () => HasConflicts);
    }

    #endregion

    private async Task BrowseRepoAsync()
    {
        var selected = BrowseFolder("MOD管理元（倉庫）フォルダを選択", RepoPath);
        if (selected is null)
            return;

        RepoPath = selected;
        await RescanRepositoryAsync(restorePersistedSelection: false);
    }

    private void BrowseModsTarget()
    {
        var selected = BrowseFolder("Minecraft modsフォルダを選択", ModsTargetDir);
        if (selected is not null)
            ModsTargetDir = selected;
    }

    private static string? BrowseFolder(string title, string initialPath)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void OpenRepoFolder()
    {
        var path = Environment.ExpandEnvironmentVariables(RepoPath.Trim());
        if (!Directory.Exists(path))
        {
            MessageBox.Show("管理元フォルダが存在しません。", "フォルダを開けません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.GetFullPath(path),
            UseShellExecute = true
        });
    }

    private async Task RescanRepositoryAsync(bool restorePersistedSelection)
    {
        if (IsBusy)
            return;

        var rawPath = Environment.ExpandEnvironmentVariables(RepoPath.Trim());
        if (!Directory.Exists(rawPath))
        {
            MessageBox.Show($"指定された管理元フォルダが存在しません。\n{rawPath}", "走査エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var selectionToRestore = restorePersistedSelection
            ? ResolvePersistedSelection()
            : GetSelectedRelativePaths();

        IsBusy = true;
        DeployProgressPercent = 0;
        DeployProgressText = "MOD倉庫を走査中...";
        AddLog("MOD倉庫を再スキャンしています...");

        try
        {
            var roots = await Task.Run(() => _scanner.Scan(rawPath));

            ModTreeRoots.Clear();
            _parents.Clear();
            foreach (var root in roots)
            {
                RegisterNode(root, parent: null);
                ModTreeRoots.Add(root);
            }

            SetSelectedRelativePaths(selectionToRestore);
            ApplyFilter(SearchFilter);
            RecalculateSelection();
            RefreshPreview();

            var totalMods = EnumerateNodes().Count(node => node.IsFile);
            var totalBytes = EnumerateNodes().Where(node => node.IsFile).Sum(node => node.SizeBytes);
            AddLog($"管理元を読み込みました: {totalMods} 個のMOD ({ModRepositoryScanner.FormatBytes(totalBytes)})");
            DeployProgressPercent = 100;
            DeployProgressText = "走査完了";
            SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DeployProgressPercent = 0;
            DeployProgressText = "走査エラー";
            AddLog($"走査エラー: {ex.Message}");
            MessageBox.Show($"フォルダの走査中にエラーが発生しました。\n{ex.Message}", "走査エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RegisterNode(ModTreeNodeModel node, ModTreeNodeModel? parent)
    {
        _parents[node] = parent;
        node.PropertyChanged += OnNodePropertyChanged;
        foreach (var child in node.Children)
            RegisterNode(child, node);
    }

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingChecks || e.PropertyName != nameof(ModTreeNodeModel.IsChecked) || sender is not ModTreeNodeModel node)
            return;

        _updatingChecks = true;
        try
        {
            if (node.IsFolder && node.IsChecked.HasValue)
                SetDescendantChecks(node, node.IsChecked.Value);

            UpdateParentStates(node);
        }
        finally
        {
            _updatingChecks = false;
        }

        SelectionChanged();
    }

    private void SetDescendantChecks(ModTreeNodeModel node, bool check)
    {
        foreach (var child in node.Children)
        {
            child.IsChecked = check;
            SetDescendantChecks(child, check);
        }
    }

    private void UpdateParentStates(ModTreeNodeModel node)
    {
        if (!_parents.TryGetValue(node, out var parent) || parent is null)
            return;

        parent.IsChecked = CalculateFolderCheckState(parent);
        UpdateParentStates(parent);
    }

    private static bool? CalculateFolderCheckState(ModTreeNodeModel folder)
    {
        if (folder.Children.Count == 0)
            return false;

        if (folder.Children.All(child => child.IsChecked == true))
            return true;
        if (folder.Children.All(child => child.IsChecked == false))
            return false;
        return null;
    }

    private void RecalculateFolderStates()
    {
        foreach (var root in ModTreeRoots)
            RecalculateFolderStateRecursive(root);
    }

    private static void RecalculateFolderStateRecursive(ModTreeNodeModel node)
    {
        foreach (var child in node.Children)
            RecalculateFolderStateRecursive(child);

        if (node.IsFolder)
            node.IsChecked = CalculateFolderCheckState(node);
    }

    private void SetCheckAll(bool check)
    {
        _updatingChecks = true;
        try
        {
            foreach (var node in EnumerateNodes().Where(node => node.IsFile))
            {
                if (!check || node.IsVisible)
                    node.IsChecked = check;
            }

            RecalculateFolderStates();
        }
        finally
        {
            _updatingChecks = false;
        }

        SelectionChanged();
    }

    private void InvertAll()
    {
        _updatingChecks = true;
        try
        {
            foreach (var node in EnumerateNodes().Where(node => node.IsFile && node.IsVisible))
                node.IsChecked = node.IsChecked != true;

            RecalculateFolderStates();
        }
        finally
        {
            _updatingChecks = false;
        }

        SelectionChanged();
    }

    private void SetExpandAll(bool expand)
    {
        foreach (var root in ModTreeRoots)
            SetNodeExpandRecursive(root, expand);
    }

    private static void SetNodeExpandRecursive(ModTreeNodeModel node, bool expand)
    {
        node.IsExpanded = expand;
        foreach (var child in node.Children)
            SetNodeExpandRecursive(child, expand);
    }

    private void ApplyFilter(string filter)
    {
        var query = (filter ?? string.Empty).Trim();
        foreach (var root in ModTreeRoots)
            ApplyFilterRecursive(root, query);
    }

    private bool ApplyFilterRecursive(ModTreeNodeModel node, string query)
    {
        if (!ShowDisabledFolders && node.IsDisabledFolder)
        {
            SetVisibilityRecursive(node, false);
            return false;
        }

        var childMatched = false;
        foreach (var child in node.Children)
            childMatched |= ApplyFilterRecursive(child, query);

        var ownMatched = string.IsNullOrEmpty(query) || node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        var visible = string.IsNullOrEmpty(query) || ownMatched || childMatched;
        node.IsVisible = visible;

        if (!string.IsNullOrEmpty(query) && childMatched)
            node.IsExpanded = true;

        return visible;
    }

    private static void SetVisibilityRecursive(ModTreeNodeModel node, bool visible)
    {
        node.IsVisible = visible;
        foreach (var child in node.Children)
            SetVisibilityRecursive(child, visible);
    }

    private void SelectionChanged()
    {
        _savedSelectionBeforeScan = GetSelectedRelativePaths();
        RecalculateSelection();
        RefreshPreview();
        SaveSettings();
    }

    private void RecalculateSelection()
    {
        var selected = GetSelectedMods();
        SelectionSummary = $"選択中: {selected.Count} 個のMOD ({ModRepositoryScanner.FormatBytes(selected.Sum(mod => mod.SizeBytes))})";
    }

    private List<ModTreeNodeModel> GetSelectedMods() =>
        EnumerateNodes()
            .Where(node => node.IsFile && node.IsChecked == true)
            .ToList();

    private List<string> GetSelectedRelativePaths() =>
        GetSelectedMods()
            .Select(node => node.RelativePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IEnumerable<ModTreeNodeModel> EnumerateNodes()
    {
        foreach (var root in ModTreeRoots)
        {
            foreach (var node in EnumerateNodeRecursive(root))
                yield return node;
        }
    }

    private static IEnumerable<ModTreeNodeModel> EnumerateNodeRecursive(ModTreeNodeModel node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateNodeRecursive(child))
                yield return descendant;
        }
    }

    private void SetSelectedRelativePaths(IEnumerable<string> relativePaths)
    {
        var selected = new HashSet<string>(
            relativePaths.Select(ModRepositoryScanner.NormalizeRelativePath),
            StringComparer.OrdinalIgnoreCase);

        _updatingChecks = true;
        try
        {
            foreach (var node in EnumerateNodes().Where(node => node.IsFile))
                node.IsChecked = selected.Contains(ModRepositoryScanner.NormalizeRelativePath(node.RelativePath));

            RecalculateFolderStates();
        }
        finally
        {
            _updatingChecks = false;
        }

        _savedSelectionBeforeScan = GetSelectedRelativePaths();
    }

    private List<string> ResolvePersistedSelection()
    {
        var profile = Profiles.FirstOrDefault(p => string.Equals(p.Name, _savedProfileName, StringComparison.OrdinalIgnoreCase));
        return profile is not null
            ? profile.SelectedRelativePaths.ToList()
            : _savedSelectionBeforeScan.ToList();
    }

    private void RefreshPreview()
    {
        var selectedMods = GetSelectedMods();
        _currentConflicts = _deploymentService.DetectConflicts(selectedMods).ToList();
        HasConflicts = _currentConflicts.Count > 0;
        ConflictSummary = HasConflicts
            ? $"同名MODの衝突が {_currentConflicts.Count} 件あります。解消するまでデプロイできません。"
            : string.Empty;

        _currentPlan = null;
        DiffItems.Clear();
        DiffNewCount = 0;
        DiffUpdateCount = 0;
        DiffDeleteCount = 0;
        DiffKeepCount = 0;

        if (!HasConflicts && !string.IsNullOrWhiteSpace(ModsTargetDir))
        {
            try
            {
                _currentPlan = _deploymentService.CalculateDiff(selectedMods, ModsTargetDir, SelectedMode);
                PopulateDiff(_currentPlan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                _currentPlan = null;
                DeployProgressText = $"差分計算エラー: {ex.Message}";
            }
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private void PopulateDiff(DeployPlanModel plan)
    {
        DiffNewCount = plan.ToAdd.Count;
        DiffUpdateCount = plan.ToUpdate.Count;
        DiffDeleteCount = plan.ToDelete.Count;
        DiffKeepCount = plan.ToKeep.Count;

        foreach (var mod in plan.ToAdd)
        {
            DiffItems.Add(new DiffItemModel
            {
                FileName = mod.Name,
                Type = DiffItemType.New,
                SizeBytes = mod.SizeBytes,
                FormattedSize = ModRepositoryScanner.FormatBytes(mod.SizeBytes),
                Reason = $"modsへ新規コピー / 元: {mod.RelativePath}"
            });
        }

        foreach (var mod in plan.ToUpdate)
        {
            DiffItems.Add(new DiffItemModel
            {
                FileName = mod.Name,
                Type = DiffItemType.Update,
                SizeBytes = mod.SizeBytes,
                FormattedSize = ModRepositoryScanner.FormatBytes(mod.SizeBytes),
                Reason = $"既存MODと差分があるため上書き / 元: {mod.RelativePath}"
            });
        }

        foreach (var target in plan.ToDelete)
        {
            DiffItems.Add(new DiffItemModel
            {
                FileName = target.FileName,
                Type = DiffItemType.Delete,
                SizeBytes = target.SizeBytes,
                FormattedSize = ModRepositoryScanner.FormatBytes(target.SizeBytes),
                Reason = ".modmanager.json の管理対象かつ未選択のため整理"
            });
        }

        foreach (var mod in plan.ToKeep)
        {
            DiffItems.Add(new DiffItemModel
            {
                FileName = mod.Name,
                Type = DiffItemType.Keep,
                SizeBytes = mod.SizeBytes,
                FormattedSize = ModRepositoryScanner.FormatBytes(mod.SizeBytes),
                Reason = "同サイズ・同更新時刻のためコピーを省略"
            });
        }

        foreach (var target in plan.UnmanagedFiles)
        {
            DiffItems.Add(new DiffItemModel
            {
                FileName = target.FileName,
                Type = DiffItemType.Protected,
                SizeBytes = target.SizeBytes,
                FormattedSize = ModRepositoryScanner.FormatBytes(target.SizeBytes),
                Reason = "手動配置ファイルとして保護（同期でも削除しません）"
            });
        }
    }

    private bool CanDeploy() =>
        !IsBusy &&
        !HasConflicts &&
        GetSelectedMods().Count > 0 &&
        !string.IsNullOrWhiteSpace(ModsTargetDir);

    private async Task DeployAsync()
    {
        var selectedMods = GetSelectedMods();
        if (selectedMods.Count == 0)
        {
            MessageBox.Show("反映するMODが選択されていません。", "確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentConflicts = _deploymentService.DetectConflicts(selectedMods).ToList();
        if (_currentConflicts.Count > 0)
        {
            HasConflicts = true;
            ShowConflictDetails();
            return;
        }

        DeployPlanModel plan;
        try
        {
            plan = _deploymentService.CalculateDiff(selectedMods, ModsTargetDir, SelectedMode);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"差分計算に失敗しました。\n{ex.Message}", "デプロイエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var modeText = SelectedMode == DeployMode.Sync ? "【同期モード】" : "【追加モード】";
        var confirmation =
            $"{modeText} でmodsフォルダへ反映します。\n\n" +
            $"・新規配置: {plan.ToAdd.Count} 個\n" +
            $"・上書き更新: {plan.ToUpdate.Count} 個\n" +
            $"・削除整理: {plan.ToDelete.Count} 個\n" +
            $"・変更なし: {plan.ToKeep.Count} 個\n" +
            $"・手動配置の保護MOD: {plan.UnmanagedFiles.Count} 個\n\n" +
            $"適用先: {Environment.ExpandEnvironmentVariables(ModsTargetDir)}\n\n" +
            "実行してよろしいですか？";

        if (MessageBox.Show(confirmation, "デプロイの確認", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        DeployProgressPercent = 0;
        DeployProgressText = "デプロイ準備中...";
        AddLog($"{modeText} でmodsフォルダへの反映を開始します...");

        try
        {
            var progress = new Progress<DeployProgressModel>(item =>
            {
                DeployProgressPercent = item.Percent;
                DeployProgressText = item.Message;
            });

            var result = await _deploymentService.ExecuteAsync(
                selectedMods,
                ModsTargetDir,
                SelectedMode,
                CreateBackup,
                progress);

            foreach (var log in result.Logs)
                ActivityLogs.Add(log);

            if (result.Success)
            {
                DeployProgressPercent = 100;
                DeployProgressText = "完了";
                var summary =
                    "反映が正常に完了しました。\n\n" +
                    $"・追加: {result.AddedCount} 個\n" +
                    $"・更新: {result.UpdatedCount} 個\n" +
                    $"・削除: {result.DeletedCount} 個\n" +
                    $"・維持: {result.KeptCount} 個";

                if (!string.IsNullOrWhiteSpace(result.BackupPath))
                    summary += $"\n\n直前状態をバックアップしました:\n{result.BackupPath}";

                MessageBox.Show(summary, "反映完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                DeployProgressText = "エラー";
                MessageBox.Show($"デプロイ中にエラーが発生しました。\n{result.ErrorMessage}", "デプロイ失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            IsBusy = false;
            RefreshPreview();
            SaveSettings();
        }
    }

    private void ShowConflictDetails()
    {
        if (_currentConflicts.Count == 0)
            return;

        var lines = new List<string>
        {
            $"同名のMODが複数選択されています（合計 {_currentConflicts.Count} 件の衝突）。",
            "modsフォルダ直下には同一ファイル名のMODを1つしか配置できないため、どちらか一方を選択解除してください。",
            string.Empty
        };

        foreach (var conflict in _currentConflicts.Take(10))
        {
            lines.Add($"【{conflict.FileName}】({conflict.Paths.Count} 箇所)");
            lines.AddRange(conflict.Paths.Select(path => $"  ・{path}"));
            lines.Add(string.Empty);
        }

        if (_currentConflicts.Count > 10)
            lines.Add($"... 他 {_currentConflicts.Count - 10} 件の衝突があります。");

        MessageBox.Show(string.Join(Environment.NewLine, lines), "同名MOD衝突の警告", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var profile in _profileStore.Load())
            Profiles.Add(profile);
    }

    private void NewProfile()
    {
        var name = PromptForProfileName("新規MOD構成プロファイル", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return;

        var existing = Profiles.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null &&
            MessageBox.Show($"プロファイル「{existing.Name}」は既に存在します。上書きしますか？", "上書き確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var profile = existing ?? new ModProfileModel { Name = name.Trim() };
        profile.Name = name.Trim();
        profile.SelectedRelativePaths = GetSelectedRelativePaths();
        profile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        if (existing is null)
            Profiles.Add(profile);

        PersistProfiles();
        _isInitializing = true;
        SelectedProfile = profile;
        _isInitializing = false;
        _savedProfileName = profile.Name;
        AddLog($"新規プロファイル「{profile.Name}」を保存しました。");
        SaveSettings();
    }

    private void SaveProfile()
    {
        if (SelectedProfile is null)
            return;

        SelectedProfile.SelectedRelativePaths = GetSelectedRelativePaths();
        SelectedProfile.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        PersistProfiles();
        AddLog($"プロファイル「{SelectedProfile.Name}」を上書き保存しました。");
        MessageBox.Show($"プロファイル「{SelectedProfile.Name}」を上書き保存しました。", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
        SaveSettings();
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null)
            return;

        var deleting = SelectedProfile;
        if (MessageBox.Show($"プロファイル「{deleting.Name}」を削除しますか？", "削除確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Profiles.Remove(deleting);
        PersistProfiles();
        _isInitializing = true;
        SelectedProfile = Profiles.FirstOrDefault();
        _isInitializing = false;
        _savedProfileName = SelectedProfile?.Name ?? string.Empty;
        AddLog($"プロファイル「{deleting.Name}」を削除しました。");
        SaveSettings();
    }

    private void ApplyProfile(ModProfileModel profile)
    {
        if (ModTreeRoots.Count == 0)
        {
            _savedSelectionBeforeScan = profile.SelectedRelativePaths.ToList();
            return;
        }

        SetSelectedRelativePaths(profile.SelectedRelativePaths);
        SelectionChanged();
    }

    private void PersistProfiles()
    {
        try
        {
            _profileStore.SaveAll(Profiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"プロファイルの保存に失敗しました。\n{ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSettings()
    {
        var settings = _settingsStore.Load();
        _repoPath = settings.RepoPath;
        _modsTargetDir = settings.ModsTargetDir;
        _showDisabledFolders = settings.ShowDisabledFolders;
        _selectedMode = settings.SelectedMode;
        _createBackup = settings.CreateBackup;
        _isDiffDetailsExpanded = settings.IsDiffDetailsExpanded;
        _savedSelectionBeforeScan = settings.LastSelectedRelativePaths ?? new List<string>();
        _savedProfileName = settings.LastProfileName ?? string.Empty;
        _selectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.Name, _savedProfileName, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveSettings()
    {
        if (_isInitializing)
            return;

        var selection = ModTreeRoots.Count > 0 ? GetSelectedRelativePaths() : _savedSelectionBeforeScan.ToList();
        _savedSelectionBeforeScan = selection;
        _savedProfileName = SelectedProfile?.Name ?? _savedProfileName;

        var settings = new DeploySettingsModel
        {
            RepoPath = RepoPath,
            ModsTargetDir = ModsTargetDir,
            ShowDisabledFolders = ShowDisabledFolders,
            SelectedMode = SelectedMode,
            CreateBackup = CreateBackup,
            IsDiffDetailsExpanded = IsDiffDetailsExpanded,
            LastProfileName = _savedProfileName,
            LastSelectedRelativePaths = selection
        };

        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog($"設定保存エラー: {ex.Message}");
        }
    }

    private static string? PromptForProfileName(string title, string initialValue)
    {
        var owner = Application.Current?.MainWindow;
        var input = new TextBox
        {
            MinWidth = 320,
            Text = initialValue,
            Margin = new Thickness(0, 8, 0, 12)
        };

        var okButton = new Button
        {
            Content = "保存",
            IsDefault = true,
            MinWidth = 80,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            MinWidth = 80
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "プロファイル名を入力してください。" });
        content.Children.Add(input);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = owner
        };

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(dialog, "プロファイル名を入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dialog.DialogResult = true;
        };

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        ActivityLogs.Add($"[{timestamp}] {message}");
    }
}
