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
    Keep
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
        _ => "•"
    };

    public string TypeLabel => Type switch
    {
        DiffItemType.New => "新規配置",
        DiffItemType.Update => "上書き更新",
        DiffItemType.Delete => "整理削除",
        DiffItemType.Keep => "維持",
        _ => ""
    };
}

/// <summary>MODツリーノード（フォルダ or JARファイル、三態チェック対応）</summary>
public sealed class ModTreeNodeModel : ObservableObject
{
    private bool? _isChecked = false;
    private bool _isExpanded = true;

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

    public string DisplayIcon => IsFolder ? "📁" : "📦";
    public string DisplayBadge => IsFolder && ModCount > 0 ? $"({ModCount} MOD)" : "";
}

/// <summary>プロファイルモデル</summary>
public sealed class ModProfileModel : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public List<string> SelectedRelativePaths { get; set; } = new();

    public override string ToString() => Name;
}
