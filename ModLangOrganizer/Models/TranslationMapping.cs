using System;
using System.Collections.Generic;

namespace ModLangOrganizer.Models;

/// <summary>
/// ワークスペース（MOD親フォルダ）ごとの翻訳ファイル対応情報
/// </summary>
public sealed class WorkspaceMapping
{
    public int SchemaVersion { get; set; } = 1;
    public string TargetRoot { get; set; } = string.Empty;
    public string OutputRoot { get; set; } = string.Empty;
    public List<TranslationMappingEntry> Entries { get; set; } = [];
}

/// <summary>
/// 編集フォルダとJAR内パスの1対1対応エントリ
/// </summary>
public sealed class TranslationMappingEntry
{
    /// <summary>
    /// 編集用相対パス（例: carbonconfig/ja_jp.json または PuzzlesLib-xxx/ja_jp.json）
    /// </summary>
    public string EditPath { get; set; } = string.Empty;

    /// <summary>
    /// 親フォルダからのJAR相対パス（例: carbonconfig-forge-1.20.1-xxx.jar）
    /// </summary>
    public string JarRelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Mod ID（例: carbonconfig）
    /// </summary>
    public string ModId { get; set; } = string.Empty;

    /// <summary>
    /// JAR内の正確なlangパス（例: assets/carbonconfig/lang/ja_jp.json）
    /// </summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>
    /// 最終更新日時
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;
}
