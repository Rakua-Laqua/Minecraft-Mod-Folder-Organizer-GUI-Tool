using System.IO;
using System.Reflection;
using ModLangOrganizer.Domain;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;

namespace RegressionTests;

internal static partial class Program
{
    private static void TestDeployContentIdentity()
    {
        using var temp = new TempDir();
        var target = temp.CreateSub("mods");
        var source = temp.File("sample.jar");
        var deployed = Path.Combine(target, "sample.jar");
        File.WriteAllText(source, "new bytes");
        File.WriteAllText(deployed, "old bytes");
        var timestamp = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(source, timestamp);
        File.SetLastWriteTimeUtc(deployed, timestamp);
        var selected = new[] { new ModTreeNodeModel { Name = "sample.jar", FullPath = source } };
        var service = new ModDeploymentService();

        Expect(service.CalculateDiff(selected, target, DeployMode.Sync).ToUpdate.Count == 1,
            "Different bytes with equal size and timestamps must be updated.");
        var result = Await(service.ExecuteAsync(selected, target, DeployMode.Sync, false));
        Expect(result.Success && result.UpdatedCount == 1, "Changed bytes must be deployed.");
        Expect(File.ReadAllText(deployed) == "new bytes", "Deployment must install the new content.");

        File.SetLastWriteTimeUtc(deployed, timestamp.AddDays(-1));
        Expect(service.CalculateDiff(selected, target, DeployMode.Sync).ToKeep.Count == 1,
            "Identical bytes must be kept even when timestamps differ.");
    }

    private static void TestDeployStagingFailure()
    {
        using var fixture = new DeploymentFixture();
        using (new FileStream(fixture.Selected[1].FullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = fixture.Execute();
            Expect(!result.Success, "An unreadable source must fail deployment.");
            Expect(result.AddedCount == 0 && result.UpdatedCount == 0 && result.DeletedCount == 0,
                "Staging failure must not report applied changes.");
        }
        fixture.ExpectOriginalState();
        fixture.ExpectNoTransaction();
    }

    private static void TestDeployCommitFailureAndRetry()
    {
        using var fixture = new DeploymentFixture();
        FileStream? manifestLock = null;
        DeployExecutionResult result;
        try
        {
            result = fixture.Execute(new InlineDeployProgress(progress =>
            {
                if (progress.Message == "管理情報を保存中...")
                    manifestLock = new FileStream(fixture.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }));
        }
        finally
        {
            manifestLock?.Dispose();
        }
        Expect(!result.Success, "A locked manifest must fail the commit.");
        Expect(result.AddedCount == 0 && result.UpdatedCount == 0 && result.DeletedCount == 0,
            "A complete rollback must reset applied counts.");
        fixture.ExpectOriginalState();
        fixture.ExpectNoTransaction();

        result = fixture.Execute();
        Expect(result.Success && result.AddedCount == 1 && result.UpdatedCount == 1 && result.DeletedCount == 1,
            "Retry after rollback must apply the entire plan.");
        Expect(!File.Exists(Path.Combine(fixture.Target, "old.jar")), "Sync must remove the deselected managed file.");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "a.jar")) == "new addition", "Addition content mismatch.");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "update.jar")) == "updated content", "Update content mismatch.");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "manual.jar")) == "manual", "Manual MOD must be protected.");
        Expect(fixture.Service.LoadManifest(fixture.Target).SetEquals(new[] { "a.jar", "update.jar" }),
            "Successful retry must track exactly the selected files.");
        fixture.ExpectNoTransaction();
    }

    private static void TestDeployCancellationRollback()
    {
        using var fixture = new DeploymentFixture();
        using var cancellation = new CancellationTokenSource();
        var result = fixture.Execute(new InlineDeployProgress(progress =>
        {
            if (progress.Message == "更新コピー: update.jar")
                cancellation.Cancel();
        }), cancellation.Token);
        Expect(!result.Success && result.ErrorMessage.Contains("キャンセル"), "Cancellation must be reported.");
        Expect(result.AddedCount == 0 && result.DeletedCount == 0, "Canceled changes must be rolled back.");
        fixture.ExpectOriginalState();
        fixture.ExpectNoTransaction();
    }

    private static void TestDeployRollbackRecoveryData()
    {
        using var fixture = new DeploymentFixture();
        using var cancellation = new CancellationTokenSource();
        FileStream? installedLock = null;
        DeployExecutionResult result;
        try
        {
            result = fixture.Execute(new InlineDeployProgress(progress =>
            {
                if (progress.Message == "管理情報を保存中...")
                {
                    installedLock = new FileStream(Path.Combine(fixture.Target, "update.jar"),
                        FileMode.Open, FileAccess.Read, FileShare.Read);
                    cancellation.Cancel();
                }
            }), cancellation.Token);
        }
        finally
        {
            installedLock?.Dispose();
        }
        Expect(!result.Success && result.ErrorMessage.Contains("自動復元に失敗"), "Incomplete rollback must be explicit.");
        Expect(result.BackupPath != null && Directory.Exists(result.BackupPath), "Recovery directory must be retained.");
        Expect(result.ErrorMessage.Contains(result.BackupPath!), "Error must locate recovery data.");
        Expect(File.ReadAllText(Path.Combine(result.BackupPath!, "originals", "update.jar")) == "before",
            "The original bytes must survive a failed restoration.");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "old.jar")) == "old managed",
            "Other originals must be restored despite one rollback failure.");
        Expect(!File.Exists(Path.Combine(fixture.Target, "a.jar")), "Other additions must still be removed.");
        Expect(File.ReadAllBytes(fixture.ManifestPath).SequenceEqual(fixture.ManifestBytes),
            "Failed commit must preserve the original manifest bytes.");
    }

    private static void TestDeployAddModeAndBackup()
    {
        using var fixture = new DeploymentFixture();
        var result = Await(fixture.Service.ExecuteAsync(fixture.Selected, fixture.Target, DeployMode.Add, true));
        Expect(result.Success && result.DeletedCount == 0, "Add mode must not remove managed files.");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "old.jar")) == "old managed", "Add mode must retain old MODs.");
        Expect(result.BackupPath != null && File.ReadAllText(Path.Combine(result.BackupPath, "update.jar")) == "before",
            "Optional backup must contain the previous update bytes.");
        Expect(fixture.Service.LoadManifest(fixture.Target).SetEquals(new[] { "old.jar", "a.jar", "update.jar" }),
            "Add mode must merge managed names.");
        fixture.ExpectNoTransaction();
    }

    private static void TestResourcePackAtomicDirectoryMove()
    {
        using var temp = new TempDir();
        var source = temp.CreateSub("source");
        var destination = temp.CreateSub("destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "lang.json"), "source original");
        File.WriteAllText(Path.Combine(source, "other.txt"), "second original");
        File.WriteAllText(Path.Combine(destination, "existing.txt"), "destination original");
        var method = typeof(ResourcePackBuilder).GetMethod("SafeMoveDirectoryWithRetry", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Directory move helper not found.");
        var failed = false;
        try { method.Invoke(null, new object[] { source, destination, 1 }); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException)
        {
            failed = true;
        }
        Expect(failed, "Moving onto an existing directory must fail without a per-file fallback.");
        Expect(File.ReadAllText(Path.Combine(source, "nested", "lang.json")) == "source original",
            "Failed directory move must retain nested source content.");
        Expect(File.ReadAllText(Path.Combine(source, "other.txt")) == "second original", "All source files must remain.");
        Expect(Directory.GetFileSystemEntries(destination).Length == 1
            && File.ReadAllText(Path.Combine(destination, "existing.txt")) == "destination original",
            "Failed directory move must leave the destination untouched.");
    }

    private static void TestResourcePackFolderReplacement()
    {
        using var temp = new TempDir();
        var source = temp.File("en_us.json");
        File.WriteAllText(source, "{\"message\":\"new\"}");
        var destination = temp.CreateSub("pack");
        File.WriteAllText(Path.Combine(destination, "previous.txt"), "previous pack");
        var batch = new JarImportBatchPlan();
        var plan = new JarImportPlan { ScanResult = CreateScan("example.jar") };
        plan.Files.Add(new JarImportFile(source, "assets/foo/lang/en_us.json"));
        batch.JarPlans.Add(plan);
        var builder = new ResourcePackBuilder();

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failed = false;
            try { builder.BuildFolder(batch, destination); }
            catch (IOException) { failed = true; }
            Expect(failed, "Unreadable pack source must fail the build.");
            Expect(File.ReadAllText(Path.Combine(destination, "previous.txt")) == "previous pack",
                "A failed pack build must preserve the previous pack.");
        }
        builder.BuildFolder(batch, destination);
        Expect(!File.Exists(Path.Combine(destination, "previous.txt")), "Successful build must replace the old pack.");
        Expect(File.ReadAllText(Path.Combine(destination, "assets", "foo", "lang", "en_us.json")) == "{\"message\":\"new\"}",
            "Replacement pack content mismatch.");
        Expect(File.Exists(Path.Combine(destination, "pack.mcmeta")), "Replacement must include pack metadata.");
        Expect(Directory.GetDirectories(temp.Path).Length == 1, "Successful replacement must clean temporary and backup folders.");
    }

    private sealed class InlineDeployProgress(Action<DeployProgressModel> report) : IProgress<DeployProgressModel>
    {
        public void Report(DeployProgressModel value) => report(value);
    }

    private sealed class DeploymentFixture : IDisposable
    {
        private readonly TempDir temp = new();
        public string Target { get; }
        public ModDeploymentService Service { get; }
        public ModTreeNodeModel[] Selected { get; }
        public string ManifestPath => Path.Combine(Target, ModDeploymentService.ManifestFileName);
        public byte[] ManifestBytes { get; }

        public DeploymentFixture(ModDeploymentService? service = null)
        {
            Service = service ?? new ModDeploymentService();
            Target = temp.CreateSub("mods");
            var source = temp.CreateSub("source");
            File.WriteAllText(Path.Combine(Target, "old.jar"), "old managed");
            File.WriteAllText(Path.Combine(Target, "update.jar"), "before");
            File.WriteAllText(Path.Combine(Target, "manual.jar"), "manual");
            File.WriteAllText(Path.Combine(source, "a.jar"), "new addition");
            File.WriteAllText(Path.Combine(source, "update.jar"), "updated content");
            Selected = new[] { "a.jar", "update.jar" }.Select(name => new ModTreeNodeModel
            {
                Name = name,
                FullPath = Path.Combine(source, name)
            }).ToArray();
            File.WriteAllText(ManifestPath, "{\n  \"version\": \"1.0\",\n  \"managed_files\": [\"old.jar\", \"update.jar\"]\n}");
            ManifestBytes = File.ReadAllBytes(ManifestPath);
        }

        public DeployExecutionResult Execute(IProgress<DeployProgressModel>? progress = null, CancellationToken cancellationToken = default)
            => Await(Service.ExecuteAsync(Selected, Target, DeployMode.Sync, false, progress, cancellationToken));

        public void ExpectOriginalState()
        {
            Expect(File.ReadAllText(Path.Combine(Target, "old.jar")) == "old managed", "Deleted original must be restored.");
            Expect(File.ReadAllText(Path.Combine(Target, "update.jar")) == "before", "Updated original must be restored.");
            Expect(File.ReadAllText(Path.Combine(Target, "manual.jar")) == "manual", "Manual MOD must be unchanged.");
            Expect(!File.Exists(Path.Combine(Target, "a.jar")), "Failed deployment must not leave unmanaged additions.");
            Expect(File.ReadAllBytes(ManifestPath).SequenceEqual(ManifestBytes), "Manifest must be unchanged byte for byte.");
        }

        public void ExpectNoTransaction()
            => Expect(Directory.GetDirectories(Target, ".modmanager-transaction-*").Length == 0, "Completed rollback/commit must clean staging.");

        public void Dispose() => temp.Dispose();
    }
}
