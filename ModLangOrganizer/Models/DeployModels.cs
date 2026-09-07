using System.Collections.ObjectModel;
using ModLangOrganizer.Helpers;

namespace ModLangOrganizer.Models;

/// <summary>デプロイ動作モード</summary>
public enum DeployMode
{
    /// <summary>同期モード: 選択MODのみを配置し、過去に配置した未選択MODを安全に整理（手動MOD保護）</summary>
    Sync,
    /// <summary>追加モード: 選択MODのみをコピー（削除なし）</summary>
    Add
}

/// <summary>差分ファイルの種類</summary>
public enum DiffItemType
{
    New,
    Update,
    Delete,
    Keep,
    Protected
}

/// <summary>差分詳細エントリ</summary>
public sealed class DiffItemModel : ObservableObject
{
    public string FileName { get; set; } = string.Empty;
    public DiffItemType Type { get; set; }
    public long SizeBytes { get; set; }
    public string FormattedSize { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    public string TypeIcon => Type switch
    {
        DiffItemType.New => "➕",
        DiffItemType.Update => "🔄",
        DiffItemType.Delete => "➖",
        DiffItemType.Keep => "⏸️",
        DiffItemType.Protected => "🛡️",
        _ => "•"
    };

    public string TypeLabel => Type switch
    {
        DiffItemType.New => "新規配置",
        DiffItemType.Update => "上書き更新",
        DiffItemType.Delete => "整理削除",
        DiffItemType.Keep => "維持",
        DiffItemType.Protected => "手動配置保護",
        _ => ""
    };
}

/// <summary>MODツリーノード（フォルダ or JARファイル、三態チェック対応）</summary>
public sealed class ModTreeNodeModel : ObservableObject
{
    private bool? _isChecked = false;
    private bool _isExpanded = true;
    private bool _isVisible = true;

    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool IsFile => !IsFolder;
    public bool IsDisabledFolder { get; set; }
    public long SizeBytes { get; set; }
    public string FormattedSize { get; set; } = string.Empty;
    public int ModCount { get; set; }

    public ObservableCollection<ModTreeNodeModel> Children { get; } = new();

    /// <summary>三態チェック状態 (true: 選択, false: 解除, null: 半選択)</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    /// <summary>ツリー展開状態</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>検索・無効フォルダフィルタによる表示状態</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool IsThreeState => IsFolder;
    public string DisplayIcon => IsFolder ? "📁" : "📦";
    public string DisplayBadge => IsFolder && ModCount > 0 ? $"({ModCount} MOD)" : "";
}

/// <summary>プロファイルモデル</summary>
public sealed class ModProfileModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public List<string> SelectedRelativePaths { get; set; } = new();
    public string Description { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>適用先に存在するJARファイル。</summary>
public sealed class ExistingModFileModel
{
    public string FileName { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
}

/// <summary>デプロイ前の差分計画。</summary>
public sealed class DeployPlanModel
{
    public List<ModTreeNodeModel> ToAdd { get; } = new();
    public List<ModTreeNodeModel> ToUpdate { get; } = new();
    public List<ModTreeNodeModel> ToKeep { get; } = new();
    public List<ExistingModFileModel> ToDelete { get; } = new();
    public List<ExistingModFileModel> UnmanagedFiles { get; } = new();

    public int TotalChanges => ToAdd.Count + ToUpdate.Count + ToDelete.Count;
}

/// <summary>同名JARが複数選択された衝突情報。</summary>
public sealed class DeployConflictModel
{
    public string FileName { get; init; } = string.Empty;
    public List<string> Paths { get; init; } = new();
}

/// <summary>デプロイ進捗。</summary>
public sealed class DeployProgressModel
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string Message { get; init; } = string.Empty;

    public int Percent => Total <= 0 ? 0 : (int)Math.Round(Current * 100d / Total);
}

/// <summary>デプロイ実行結果。</summary>
public sealed class DeployExecutionResult
{
    public bool Success { get; init; }
    public int AddedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int DeletedCount { get; init; }
    public int KeptCount { get; init; }
    public string? BackupPath { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public List<string> Logs { get; init; } = new();
}

/// <summary>Copy Manager画面の永続設定。</summary>
public sealed class DeploySettingsModel
{
    public string RepoPath { get; set; } = @"D:\Minecraft_mod_management";
    public string ModsTargetDir { get; set; } = Environment.ExpandEnvironmentVariables(@"%APPDATA%\.minecraft\mods");
    public bool ShowDisabledFolders { get; set; } = true;
    public DeployMode SelectedMode { get; set; } = DeployMode.Sync;
    public bool CreateBackup { get; set; } = true;
    public bool IsDiffDetailsExpanded { get; set; } = true;
    public string LastProfileName { get; set; } = string.Empty;
    public List<string> LastSelectedRelativePaths { get; set; } = new();
}
