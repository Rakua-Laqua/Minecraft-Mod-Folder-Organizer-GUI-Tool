namespace OrganizerTool.Models;

public enum ModSourceType
{
    Directory = 0,
    Jar = 1,
}

public enum MultiLangMode
{
    FirstOnly = 0,
    MergeAll = 1,
    SeparateFolders = 2,
}

public enum DeleteMode
{
    Permanent = 0,
    RecycleBin = 1,
}

public enum ModPlanPolicy
{
    LangFound = 0,
    LangNotFound = 1,
}

public enum ModStatus
{
    Unprocessed = 0,
    Success = 1,
    Warning = 2,
    Failed = 3,
    Cancelled = 4,
}

public enum LogLevel
{
    Info = 0,
    Warn = 1,
    Error = 2,
}
