using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ModLangOrganizer.Helpers;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.ViewModels;

/// <summary>MOD構成デプロイ (Copy Manager) 画面のViewModel</summary>
public sealed class DeployViewModel : ObservableObject
{
    private string _repoPath = @"D:\Minecraft_mod_management";
    private string _modsTargetDir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\.minecraft\mods");
    private bool _showDisabledFolders = true;
    private string _searchFilter = string.Empty;
    private DeployMode _selectedMode = DeployMode.Sync;
    private bool _createBackup = true;
    private bool _isDiffDetailsExpanded = true;
    private string _selectionSummary = "選択中: 7 個のMOD (40.5 MB)";
    private int _diffNewCount = 2;
    private int _diffUpdateCount = 1;
    private int _diffDeleteCount = 1;
    private int _diffKeepCount = 4;
    private ModProfileModel? _selectedProfile;

    public ObservableCollection<ModProfileModel> Profiles { get; } = new();
    public ObservableCollection<ModTreeNodeModel> ModTreeRoots { get; } = new();
    public ObservableCollection<DiffItemModel> DiffItems { get; } = new();
    public ObservableCollection<string> ActivityLogs { get; } = new();

    public DeployViewModel()
    {
        InitializeCommands();
        LoadSampleData();
    }

    #region Properties

    public string RepoPath
    {
        get => _repoPath;
        set => SetProperty(ref _repoPath, value);
    }

    public string ModsTargetDir
    {
        get => _modsTargetDir;
        set => SetProperty(ref _modsTargetDir, value);
    }

    public bool ShowDisabledFolders
    {
        get => _showDisabledFolders;
        set => SetProperty(ref _showDisabledFolders, value);
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
            {
                ApplyFilter(value);
            }
        }
    }

    public DeployMode SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    public bool CreateBackup
    {
        get => _createBackup;
        set => SetProperty(ref _createBackup, value);
    }

    public bool IsDiffDetailsExpanded
    {
        get => _isDiffDetailsExpanded;
        set => SetProperty(ref _isDiffDetailsExpanded, value);
    }

    public string SelectionSummary
    {
        get => _selectionSummary;
        set => SetProperty(ref _selectionSummary, value);
    }

    public int DiffNewCount
    {
        get => _diffNewCount;
        set => SetProperty(ref _diffNewCount, value);
    }

    public int DiffUpdateCount
    {
        get => _diffUpdateCount;
        set => SetProperty(ref _diffUpdateCount, value);
    }

    public int DiffDeleteCount
    {
        get => _diffDeleteCount;
        set => SetProperty(ref _diffDeleteCount, value);
    }

    public int DiffKeepCount
    {
        get => _diffKeepCount;
        set => SetProperty(ref _diffKeepCount, value);
    }

    public ModProfileModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value != null)
            {
                AddLog($"プロファイル「{value.Name}」を選択しました。");
            }
        }
    }

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

    private void InitializeCommands()
    {
        BrowseRepoCommand = new RelayCommand(_ => BrowseFolder(p => RepoPath = p));
        BrowseModsTargetCommand = new RelayCommand(_ => BrowseFolder(p => ModsTargetDir = p));
        SetDefaultModsDirCommand = new RelayCommand(_ =>
        {
            ModsTargetDir = Environment.ExpandEnvironmentVariables(@"%APPDATA%\.minecraft\mods");
            AddLog("適用先をMinecraft標準のmodsフォルダに設定しました。");
        });
        RescanRepoCommand = new RelayCommand(_ =>
        {
            AddLog("🔄 MOD倉庫を再スキャンしています...");
            AddLog("✅ 42個のMOD JARを検出しました。");
        });
        OpenRepoFolderCommand = new RelayCommand(_ =>
        {
            if (Directory.Exists(RepoPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = RepoPath,
                    UseShellExecute = true
                });
            }
        });
        SelectAllCommand = new RelayCommand(_ => SetCheckAll(true));
        DeselectAllCommand = new RelayCommand(_ => SetCheckAll(false));
        InvertSelectionCommand = new RelayCommand(_ => InvertAll());
        ExpandAllCommand = new RelayCommand(_ => SetExpandAll(true));
        CollapseAllCommand = new RelayCommand(_ => SetExpandAll(false));
        ToggleDiffDetailsCommand = new RelayCommand(_ => IsDiffDetailsExpanded = !IsDiffDetailsExpanded);
        ClearSearchCommand = new RelayCommand(_ => SearchFilter = string.Empty);
        
        NewProfileCommand = new RelayCommand(_ =>
        {
            AddLog("新規プロファイル保存ダイアログ (機能統合時に実装)");
        });
        SaveProfileCommand = new RelayCommand(_ =>
        {
            if (SelectedProfile != null)
                AddLog($"プロファイル「{SelectedProfile.Name}」を上書き保存しました。");
        });
        DeleteProfileCommand = new RelayCommand(_ =>
        {
            if (SelectedProfile != null)
            {
                AddLog($"プロファイル「{SelectedProfile.Name}」を削除しました。");
                Profiles.Remove(SelectedProfile);
                SelectedProfile = Profiles.FirstOrDefault();
            }
        });

        DeployCommand = new RelayCommand(_ =>
        {
            AddLog("🚀 modsフォルダへの同期反映を開始します...");
            if (CreateBackup)
            {
                AddLog("📦 直前バックアップを作成しました: .modmanager_backup/backup_20260908.zip");
            }
            AddLog("✅ 新規ファイルを配置中...");
            AddLog("✅ 未選択ファイルを安全に整理完了");
            AddLog("🎉 同期反映が完了しました！");
            MessageBox.Show("選択したMODが正常に .minecraft/mods へ同期反映されました！", "反映完了", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    #endregion

    private void BrowseFolder(Action<string> onSelected)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "フォルダを選択してください"
        };
        if (dialog.ShowDialog() == true)
        {
            onSelected(dialog.FolderName);
        }
    }

    private void AddLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        ActivityLogs.Add($"[{timestamp}] {message}");
    }

    private void SetCheckAll(bool check)
    {
        foreach (var root in ModTreeRoots)
        {
            SetNodeCheckRecursive(root, check);
        }
        RecalculateSelection();
    }

    private void InvertAll()
    {
        foreach (var root in ModTreeRoots)
        {
            InvertNodeCheckRecursive(root);
        }
        RecalculateSelection();
    }

    private void SetNodeCheckRecursive(ModTreeNodeModel node, bool check)
    {
        node.IsChecked = check;
        foreach (var child in node.Children)
        {
            SetNodeCheckRecursive(child, check);
        }
    }

    private void InvertNodeCheckRecursive(ModTreeNodeModel node)
    {
        if (node.IsFile)
            node.IsChecked = !(node.IsChecked == true);

        foreach (var child in node.Children)
            InvertNodeCheckRecursive(child);
    }

    private void SetExpandAll(bool expand)
    {
        foreach (var root in ModTreeRoots)
        {
            SetNodeExpandRecursive(root, expand);
        }
    }

    private void SetNodeExpandRecursive(ModTreeNodeModel node, bool expand)
    {
        node.IsExpanded = expand;
        foreach (var child in node.Children)
        {
            SetNodeExpandRecursive(child, expand);
        }
    }

    private void ApplyFilter(string filter)
    {
        // 絞り込みロジック (UIモック用)
    }

    private void RecalculateSelection()
    {
        int count = 0;
        long totalBytes = 0;
        foreach (var root in ModTreeRoots)
        {
            CountCheckedRecursive(root, ref count, ref totalBytes);
        }
        SelectionSummary = $"選択中: {count} 個のMOD ({FormatBytes(totalBytes)})";
        DiffNewCount = Math.Max(0, count - 3);
        DiffKeepCount = Math.Min(count, 4);
    }

    private void CountCheckedRecursive(ModTreeNodeModel node, ref int count, ref long bytes)
    {
        if (node.IsFile && node.IsChecked == true)
        {
            count++;
            bytes += node.SizeBytes;
        }
        foreach (var child in node.Children)
        {
            CountCheckedRecursive(child, ref count, ref bytes);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{(double)bytes / (1024 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024) return $"{(double)bytes / (1024 * 1024):F1} MB";
        if (bytes >= 1024) return $"{(double)bytes / 1024:F1} KB";
        return $"{bytes} B";
    }

    private void LoadSampleData()
    {
        // プロファイル
        var p1 = new ModProfileModel { Name = "⚡ 1.20.1 工業＋軽量化構成" };
        var p2 = new ModProfileModel { Name = "🍃 最小軽量化・Vanilla+ 構成" };
        var p3 = new ModProfileModel { Name = "⚔️ RPGダンジョン構成" };
        Profiles.Add(p1);
        Profiles.Add(p2);
        Profiles.Add(p3);
        SelectedProfile = p1;

        // ツリーデータ
        var coreFolder = new ModTreeNodeModel
        {
            Name = "1.20.1 軽量化・前提",
            IsFolder = true,
            ModCount = 4,
            FormattedSize = "18.4 MB",
            IsChecked = true,
            IsExpanded = true
        };
        coreFolder.Children.Add(new ModTreeNodeModel { Name = "rubidium-mc1.20.1-0.7.1.jar", IsFolder = false, FormattedSize = "4.2 MB", SizeBytes = 4404019, IsChecked = true });
        coreFolder.Children.Add(new ModTreeNodeModel { Name = "oculus-mc1.20.1-1.6.9.jar", IsFolder = false, FormattedSize = "3.8 MB", SizeBytes = 3984588, IsChecked = true });
        coreFolder.Children.Add(new ModTreeNodeModel { Name = "cloth-config-11.1.118-forge.jar", IsFolder = false, FormattedSize = "1.9 MB", SizeBytes = 1992294, IsChecked = true });
        coreFolder.Children.Add(new ModTreeNodeModel { Name = "architectury-9.2.14-forge.jar", IsFolder = false, FormattedSize = "8.5 MB", SizeBytes = 8912896, IsChecked = true });

        var createFolder = new ModTreeNodeModel
        {
            Name = "工業_Create一式",
            IsFolder = true,
            ModCount = 3,
            FormattedSize = "22.1 MB",
            IsChecked = true,
            IsExpanded = true
        };
        createFolder.Children.Add(new ModTreeNodeModel { Name = "create-1.20.1-0.5.1.f.jar", IsFolder = false, FormattedSize = "14.8 MB", SizeBytes = 15518924, IsChecked = true });
        createFolder.Children.Add(new ModTreeNodeModel { Name = "create_steam_and_rails-1.6.4.jar", IsFolder = false, FormattedSize = "5.2 MB", SizeBytes = 5452595, IsChecked = true });
        createFolder.Children.Add(new ModTreeNodeModel { Name = "create_crafts_additions-1.20.1.jar", IsFolder = false, FormattedSize = "2.1 MB", SizeBytes = 2202009, IsChecked = true });

        var qolFolder = new ModTreeNodeModel
        {
            Name = "便利系_UI_マップ",
            IsFolder = true,
            ModCount = 2,
            FormattedSize = "4.7 MB",
            IsChecked = false,
            IsExpanded = true
        };
        qolFolder.Children.Add(new ModTreeNodeModel { Name = "jei-1.20.1-forge-15.3.0.4.jar", IsFolder = false, FormattedSize = "2.6 MB", SizeBytes = 2726297, IsChecked = false });
        qolFolder.Children.Add(new ModTreeNodeModel { Name = "journeymap-1.20.1-5.9.18-forge.jar", IsFolder = false, FormattedSize = "2.1 MB", SizeBytes = 2202009, IsChecked = false });

        var backupFolder = new ModTreeNodeModel
        {
            Name = "_backup_旧バージョン",
            IsFolder = true,
            IsDisabledFolder = true,
            ModCount = 1,
            FormattedSize = "14.1 MB",
            IsChecked = false,
            IsExpanded = false
        };
        backupFolder.Children.Add(new ModTreeNodeModel { Name = "create-1.20.1-0.5.1.c.jar", IsFolder = false, FormattedSize = "14.1 MB", SizeBytes = 14784921, IsChecked = false });

        ModTreeRoots.Add(coreFolder);
        ModTreeRoots.Add(createFolder);
        ModTreeRoots.Add(qolFolder);
        ModTreeRoots.Add(backupFolder);

        // 差分データ
        DiffItems.Add(new DiffItemModel { FileName = "create_steam_and_rails-1.6.4.jar", Type = DiffItemType.New, FormattedSize = "5.2 MB", Reason = "新規配置" });
        DiffItems.Add(new DiffItemModel { FileName = "create_crafts_additions-1.20.1.jar", Type = DiffItemType.New, FormattedSize = "2.1 MB", Reason = "新規配置" });
        DiffItems.Add(new DiffItemModel { FileName = "rubidium-mc1.20.1-0.7.1.jar", Type = DiffItemType.Update, FormattedSize = "4.2 MB", Reason = "バージョン更新（旧0.7.0）" });
        DiffItems.Add(new DiffItemModel { FileName = "jei-1.20.1-forge-15.3.0.4.jar", Type = DiffItemType.Delete, FormattedSize = "2.6 MB", Reason = "プロファイル除外による自動整理" });

        AddLog("MOD倉庫を読み込みました: 42 JARs");
        AddLog("プロファイル「1.20.1 工業＋軽量化構成」を適用しました。");
        AddLog("差分計算完了: +2 新規, 1 更新, -1 削除");
    }
}
