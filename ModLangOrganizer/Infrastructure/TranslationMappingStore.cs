using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Infrastructure;

/// <summary>
/// ワークスペースごとの mapping.json を %LOCALAPPDATA%\ModLangOrganizer\workspaces\ に永続保存・管理する。
/// </summary>
public sealed class TranslationMappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string BaseWorkspacesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModLangOrganizer", "workspaces");

    /// <summary>
    /// targetRoot と outputRoot の正規化パスから一意なワークスペースID（SHA-256の先頭16文字）を生成する。
    /// </summary>
    public static string ComputeWorkspaceId(string targetRoot, string? outputRoot = null)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
            return "default";

        var normTarget = Path.GetFullPath(targetRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        string key;
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            var normOutput = Path.GetFullPath(outputRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToLowerInvariant();
            key = normTarget + "|" + normOutput;
        }
        else
        {
            key = normTarget;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hashBytes)[..16];
    }

    /// <summary>
    /// ワークスペースのディレクトリパスを取得する。
    /// </summary>
    public static string GetWorkspaceDirectory(string targetRoot, string? outputRoot = null)
    {
        var workspaceId = ComputeWorkspaceId(targetRoot, outputRoot);
        return Path.Combine(BaseWorkspacesDir, workspaceId);
    }

    public static string GetMappingPath(string targetRoot, string? outputRoot = null)
    {
        return Path.Combine(GetWorkspaceDirectory(targetRoot, outputRoot), "mapping.json");
    }

    /// <summary>
    /// 指定された targetRoot (および outputRoot) の mapping.json を読み込む。存在しないか破損時は新規作成またはバックアップから復旧する。
    /// </summary>
    public WorkspaceMapping Load(string targetRoot, string? outputRoot = null)
    {
        var mappingPath = GetMappingPath(targetRoot, outputRoot);
        var backupPath = mappingPath + ".bak";

        // 新しいペアのパスが存在しない場合、後方互換として targetRoot 単体パスを確認
        // ただし、旧mappingのEditPathはTargetRoot相対のため、OutputRoot == TargetRoot（既定値）の場合のみ自動移行を許可する
        if (!File.Exists(mappingPath) && !string.IsNullOrWhiteSpace(outputRoot))
        {
            var isDefaultOutput = string.Equals(
                Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

            if (isDefaultOutput)
            {
                var legacyPath = GetMappingPath(targetRoot, null);
                if (File.Exists(legacyPath))
                {
                    mappingPath = legacyPath;
                    backupPath = legacyPath + ".bak";
                }
            }
        }

        if (File.Exists(mappingPath))
        {
            try
            {
                var json = File.ReadAllText(mappingPath, Encoding.UTF8);
                var mapping = JsonSerializer.Deserialize<WorkspaceMapping>(json, JsonOptions);
                if (mapping != null)
                {
                    mapping.TargetRoot = targetRoot;
                    if (!string.IsNullOrWhiteSpace(outputRoot))
                        mapping.OutputRoot = outputRoot;
                    return mapping;
                }
            }
            catch
            {
                // 破損時はバックアップを試す
                if (File.Exists(backupPath))
                {
                    try
                    {
                        var bakJson = File.ReadAllText(backupPath, Encoding.UTF8);
                        var bakMapping = JsonSerializer.Deserialize<WorkspaceMapping>(bakJson, JsonOptions);
                        if (bakMapping != null)
                        {
                            bakMapping.TargetRoot = targetRoot;
                            if (!string.IsNullOrWhiteSpace(outputRoot))
                                bakMapping.OutputRoot = outputRoot;
                            return bakMapping;
                        }
                    }
                    catch { }
                }
            }
        }

        return new WorkspaceMapping
        {
            TargetRoot = targetRoot,
            OutputRoot = outputRoot ?? string.Empty,
            Entries = []
        };
    }

    /// <summary>
    /// mapping.json をアトミックに永続保存する。
    /// 一時ファイルに完全出力したのちに置換し、直前の正常データを .bak に残す。
    /// </summary>
    public void Save(string targetRoot, string? outputRoot, WorkspaceMapping mapping)
    {
        var workspaceDir = GetWorkspaceDirectory(targetRoot, outputRoot);
        Directory.CreateDirectory(workspaceDir);

        mapping.TargetRoot = targetRoot;
        if (!string.IsNullOrWhiteSpace(outputRoot))
            mapping.OutputRoot = outputRoot;

        var json = JsonSerializer.Serialize(mapping, JsonOptions);

        var mappingPath = GetMappingPath(targetRoot, outputRoot);
        var tempPath = mappingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backupPath = mappingPath + ".bak";

        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(false));

            if (File.Exists(mappingPath))
            {
                // バックアップの作成
                try
                {
                    File.Copy(mappingPath, backupPath, overwrite: true);
                }
                catch { }

                File.Replace(tempPath, mappingPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, mappingPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public void Save(string targetRoot, WorkspaceMapping mapping) =>
        Save(targetRoot, mapping.OutputRoot, mapping);
}
