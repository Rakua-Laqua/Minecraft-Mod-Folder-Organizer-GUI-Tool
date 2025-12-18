using OrganizerTool.Models;

namespace OrganizerTool.ViewModels;

public sealed class ModItemViewModel : ViewModelBase
{
    private string _modName = "";
    private bool _assetsExists;
    private int _langCount;
    private string _policyLabel = "";
    private int _plannedMoves;
    private int _plannedDeletes;
    private ModStatus _status = ModStatus.Unprocessed;

    public required string ModPath { get; init; }
    public required IReadOnlyList<string> LangCandidates { get; init; }

    public string ModName
    {
        get => _modName;
        set => SetProperty(ref _modName, value);
    }

    public bool AssetsExists
    {
        get => _assetsExists;
        set => SetProperty(ref _assetsExists, value);
    }

    public int LangCount
    {
        get => _langCount;
        set => SetProperty(ref _langCount, value);
    }

    public string PolicyLabel
    {
        get => _policyLabel;
        set => SetProperty(ref _policyLabel, value);
    }

    public int PlannedMoves
    {
        get => _plannedMoves;
        set => SetProperty(ref _plannedMoves, value);
    }

    public int PlannedDeletes
    {
        get => _plannedDeletes;
        set => SetProperty(ref _plannedDeletes, value);
    }

    public ModStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusLabel => Status switch
    {
        ModStatus.Success => "成功",
        ModStatus.Warning => "警告",
        ModStatus.Failed => "失敗",
        ModStatus.Cancelled => "キャンセル",
        _ => "未処理",
    };

    public void RefreshStatusLabel() => OnPropertyChanged(nameof(StatusLabel));
}
