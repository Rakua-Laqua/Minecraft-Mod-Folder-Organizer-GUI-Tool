using System.IO;
using System.IO.Compression;
using OrganizerTool.Infrastructure;
using OrganizerTool.Models;

namespace OrganizerTool.Domain;

public sealed class Executor
{
    private readonly FileSystem _fs;

    public Executor(FileSystem fs)
    {
        _fs = fs;
    }

    public async Task ExecuteAsync(
        ExecutionPlan plan,
        AppOptions options,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError,
        CancellationToken cancellationToken)
    {
        // 実ファイルI/Oが主なので、UIスレッドを塞がないようにTask.Runに寄せる
        await Task.Run(() => ExecuteCore(plan, options, logInfo, logWarn, logError, cancellationToken), cancellationToken);
    }

    private void ExecuteCore(
        ExecutionPlan plan,
        AppOptions options,
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string> logError,
        CancellationToken cancellationToken)
    {
        var dryRunPrefix = options.DryRun ? "[DRY-RUN] " : "";

        foreach (var op in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logInfo(dryRunPrefix + op.Describe());

            if (options.DryRun)
            {
                continue;
            }

            try
            {
                switch (op)
                {
                    case EnsureDirectoryOperation mkdir:
                        _fs.EnsureDirectory(mkdir.Path);
                        break;

                    case MoveWithOverwriteOperation move:
                        _fs.MoveWithOverwrite(move.SourcePath, move.DestinationPath, options.DeleteMode);
                        break;

                    case DeletePathOperation del:
                        _fs.DeletePath(del.Path, options.DeleteMode);
                        break;

                    case BackupZipOperation zip:
                        CreateZip(zip.SourceDirectory, zip.ZipPath);
                        break;

                    default:
                        logWarn($"Unknown operation: {op.Kind}");
                        break;
                }
            }
            catch (Exception ex)
            {
                logError($"Operation failed: {op.Describe()} / {ex.Message}");
                throw;
            }
        }
    }

    private static void CreateZip(string sourceDirectory, string zipPath)
    {
        var parent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(sourceDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
    }
}
