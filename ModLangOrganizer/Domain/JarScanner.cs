using System.IO;
using System.Text.RegularExpressions;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Domain;

/// <summary>jarスキャナ: jarファイルを解析してlang候補を検出する</summary>
public sealed partial class JarScanner
{
    private readonly ArchiveExtractor _extractor = new();
    private readonly FileSystemService _fs = new();

    [GeneratedRegex(@"^assets/([^/]+)/lang/", RegexOptions.IgnoreCase)]
    private static partial Regex LangPathRegex();

    /// <summary>指定ディレクトリ直下のjar一覧を取得</summary>
    public List<string> EnumerateJars(string targetDir)
    {
        return Directory.GetFiles(targetDir, "*.jar", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>1つのjarをスキャンしてlang候補を検出する</summary>
    public JarScanResult ScanJar(string jarPath, string outputRoot)
    {
        var result = new JarScanResult
        {
            JarFileName = Path.GetFileName(jarPath),
            JarFilePath = jarPath
        };

        try
        {
            // スナップショット取得
            result.Snapshot = _fs.BuildSnapshot(jarPath);

            // アーカイブ内エントリ一覧を取得（展開せずに）
            var entries = _extractor.ListEntries(jarPath);
            result.Integrity = JarIntegrity.OK;

            // lang候補を検出
            var langEntries = entries
                .Where(e => LangPathRegex().IsMatch(e))
                .ToList();

            if (langEntries.Count == 0)
            {
                result.Strategy = ProcessingStrategy.NoLang;
                result.PlannedOperations.Add(new PlannedOperation
                {
                    Type = PlannedOperationType.Skip,
                    Description = $"{result.JarFileName}: langなし → スキップ"
                });
                return result;
            }

            // modid別にグループ化
            var grouped = langEntries
                .Select(e =>
                {
                    var match = LangPathRegex().Match(e);
                    return new { ModId = match.Groups[1].Value, Entry = e };
                })
                .GroupBy(x => x.ModId);

            foreach (var group in grouped)
            {
                var candidate = new LangCandidate
                {
                    ModId = group.Key,
                    ArchiveLangPath = $"assets/{group.Key}/lang"
                };

                foreach (var item in group)
                {
                    // ディレクトリエントリは除外（ファイルのみ）
                    if (!item.Entry.EndsWith('/'))
                    {
                        var relativePath = item.Entry[(candidate.ArchiveLangPath.Length + 1)..];
                        candidate.Files.Add(relativePath);
                    }
                }

                result.LangCandidates.Add(candidate);
            }

            result.Strategy = ProcessingStrategy.LangFound;

            // 予定操作を構築
            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Extract,
                Description = $"{result.JarFileName}: 一時展開"
            });

            var jarRootName = Path.GetFileNameWithoutExtension(result.JarFileName);
            var jarOutputRoot = Path.Combine(outputRoot, jarRootName);

            foreach (var candidate in result.LangCandidates)
            {
                var outputLangPath = Path.Combine(candidate.ModId, "lang");
                var outLang = Path.Combine(jarOutputRoot, outputLangPath);
                var logLangPath = $"{jarRootName}/{candidate.ModId}/lang";

                result.PlannedOperations.Add(new PlannedOperation
                {
                    Type = PlannedOperationType.CreateDir,
                    Description = $"ディレクトリ作成: {logLangPath}",
                    DestinationPath = outLang
                });

                foreach (var file in candidate.Files)
                {
                    var destPath = Path.Combine(outLang, file);
                    if (File.Exists(destPath))
                    {
                        // 既存ファイルがある → 実行時に比較（スキャン時点ではConflictCopy候補）
                        result.PlannedOperations.Add(new PlannedOperation
                        {
                            Type = PlannedOperationType.ConflictCopy,
                            Description = $"競合可能性: {logLangPath}/{file}",
                            DestinationPath = destPath
                        });
                    }
                    else
                    {
                        result.PlannedOperations.Add(new PlannedOperation
                        {
                            Type = PlannedOperationType.Copy,
                            Description = $"コピー: {logLangPath}/{file}",
                            DestinationPath = destPath
                        });
                    }
                }
            }

            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Cleanup,
                Description = $"{result.JarFileName}: 作業展開フォルダ削除"
            });
        }
        catch (Exception ex)
        {
            result.Integrity = JarIntegrity.Corrupted;
            result.Strategy = ProcessingStrategy.NoLang;
            result.ErrorMessage = ex.Message;
            result.PlannedOperations.Add(new PlannedOperation
            {
                Type = PlannedOperationType.Skip,
                Description = $"{result.JarFileName}: 読み取りエラー → スキップ"
            });
        }

        return result;
    }
}
