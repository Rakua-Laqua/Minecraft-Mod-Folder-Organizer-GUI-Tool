namespace ModLangOrganizer.Models;

/// <summary>
/// Minecraft リソースパックの pack_format 選択肢
/// </summary>
public sealed class PackFormatOption
{
    public int Format { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}
