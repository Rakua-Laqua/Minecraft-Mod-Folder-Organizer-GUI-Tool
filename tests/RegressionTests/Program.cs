using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ModLangOrganizer.Domain;
using ModLangOrganizer.Infrastructure;
using ModLangOrganizer.Models;
using ModLangOrganizer.ViewModels;

namespace RegressionTests;

internal static class Program
{
    private const string ZeroSelectionMessage = "処理対象のModが選択されていません。";

    private static int Main()
    {
        var failed = 0;
        failed += Run("R1.ZeroSelection", TestR1ZeroSelection);
        failed += Run("R1.HiddenSelectionAndSameNameProgress", TestR1HiddenSelectionAndSameNameProgress);
        failed += Run("R1.ThreePathConnection", TestR1ThreePathConnection);
        failed += Run("R2.ValidJsonAndLegacyMerge", TestR2ValidJsonAndLegacyMerge);
        failed += Run("R2.InvalidKeepsExistingBytes", TestR2InvalidKeepsExistingBytes);
        failed += Run("R2.FallbackOverwriteApiKept", TestR2FallbackOverwriteApiKept);
        failed += Run("R3.SameNameCollisionAndSubsetContext", TestR3SameNameCollisionAndSubsetContext);
        failed += Run("R3.MappingLegacyKeepAndOwnershipRefuse", TestR3MappingLegacyKeepAndOwnershipRefuse);
        failed += Run("R3.RegisterMappingRefuseRetarget", TestR3RegisterMappingRefuseRetarget);
        failed += Run("R4.BackupZipRootsAndContents", TestR4BackupZipRootsAndContents);
        failed += Run("R5.DangerousPathsAndResourcePack", TestR5DangerousPathsAndResourcePack);
        failed += Run("JarPathPolicy.RelativeBoundary", TestJarPathPolicyRelativeBoundary);
        Console.WriteLine(failed == 0 ? "ALL PASS" : $"FAILED {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static int Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS " + name);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + name + ": " + ex);
            return 1;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void TestR1ZeroSelection()
    {
        var vm = CreateUninitializedVm();
        AddPair(vm, "a/one.jar", selected: false);
        AddPair(vm, "b/two.jar", selected: false);

        var args = new object?[] { null };
        var ok = InvokeInstance<bool>(vm, "TryBeginSelectedExecution", args);
        Expect(!ok, "zero selection should fail begin");
        Expect(args[0] is List<JarScanResult> list && list.Count == 0, "zero selection list empty");
        Expect(vm.StatusBarText == ZeroSelectionMessage, "zero selection status message");
    }

    private static void TestR1HiddenSelectionAndSameNameProgress()
    {
        var vm = CreateUninitializedVm();
        AddPair(vm, "a/same.jar", selected: true);
        AddPair(vm, "b/same.jar", selected: true);
        AddPair(vm, "c/other.jar", selected: false);

        Expect(vm.GetType().GetProperty("FilteredModsView")!.GetValue(vm) is null,
            "uninitialized FilteredModsView stays null; capture must not use it");

        var captured = InvokeInstance<List<JarScanResult>>(vm, "CaptureExecutionSelection");
        Expect(captured.Count == 2, "hidden/unfiltered selected rows stay selected");
        Expect(captured[0].RelativeJarPath.Replace('\\', '/') == "a/same.jar", "first selected is a/same.jar");
        Expect(captured[1].RelativeJarPath.Replace('\\', '/') == "b/same.jar", "second selected is b/same.jar");

        InvokeInstance(vm, "UpdateExecutionProgress", new ExecutionProgress(
            Index: 0, Current: 1, Total: 2, JarName: "same.jar",
            Stage: ExecutionProgressStage.Started));
        Expect(vm.Mods[0].Status == ModStatus.Processing, "subset index 0 maps to Mods[0]");
        Expect(vm.Mods[1].Status == ModStatus.Pending, "unrelated row stays pending");

        InvokeInstance(vm, "UpdateExecutionProgress", new ExecutionProgress(
            Index: 1, Current: 2, Total: 2, JarName: "same.jar",
            Stage: ExecutionProgressStage.Started));
        Expect(vm.Mods[1].Status == ModStatus.Processing, "subset index 1 maps to Mods[1] same jar name");

        vm.Mods[0].IsSelected = false;
        InvokeInstance(vm, "UpdateExecutionProgress", new ExecutionProgress(
            Index: 0, Current: 1, Total: 2, JarName: "same.jar",
            Stage: ExecutionProgressStage.Completed, FinalStatus: ModStatus.Success));
        Expect(vm.Mods[0].Status == ModStatus.Success, "selection change after start does not retarget progress");
        Expect(vm.Mods[2].Status == ModStatus.Pending, "unselected row is not updated");
    }

    private static void TestR1ThreePathConnection()
    {
        var vm = CreateUninitializedVm();
        AddPair(vm, "a/one.jar", selected: false);
        AddPair(vm, "b/two.jar", selected: false);
        var marker = Path.Combine(Path.GetTempPath(), "mlo-r1-marker-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(marker, "untouched");
        try
        {
            foreach (var method in new[] { "ExecuteAsync", "ImportAsync", "ExportResourcePackAsync" })
            {
                vm.StatusBarText = "before";
                AwaitInstance(vm, method);
                Expect(vm.StatusBarText == ZeroSelectionMessage, method + " must exit on zero selection");
                Expect(File.ReadAllText(marker) == "untouched", method + " must not change files");
            }
        }
        finally
        {
            TryDeleteFile(marker);
        }
    }

    private static void TestR2ValidJsonAndLegacyMerge()
    {
        using var temp = new TempDir();
        var merger = new LangFileMerger();

        var jsonSrc = temp.File("src.json");
        var jsonDst = temp.File("dst.json");
        File.WriteAllText(jsonSrc, "{\"a\":\"en\",\"c\":\"new\"}", Encoding.UTF8);
        File.WriteAllText(jsonDst, "{\"a\":\"ja\",\"b\":\"old\"}", Encoding.UTF8);
        var jsonResult = merger.MergeTargetFromJar(jsonSrc, jsonDst);
        Expect(jsonResult.WasMerged, "json merged");
        Expect(!jsonResult.UsedFallbackOverwrite, "json did not fallback");
        Expect(jsonResult.PreservedKeys == 1, "json preserved");
        Expect(jsonResult.AddedKeys == 1, "json added");
        Expect(jsonResult.RemovedKeys == 1, "json removed");
        var jsonText = File.ReadAllText(jsonDst, Encoding.UTF8);
        Expect(jsonText.Contains("\"a\":\"ja\"", StringComparison.Ordinal), "json kept existing value");
        Expect(jsonText.Contains("\"c\":\"new\"", StringComparison.Ordinal), "json added source key");
        Expect(!jsonText.Contains("\"b\"", StringComparison.Ordinal), "json dropped extra dest key");

        var langSrc = temp.File("src.lang");
        var langDst = temp.File("dst.lang");
        File.WriteAllText(langSrc, "a=en\nc=new\n", Encoding.UTF8);
        File.WriteAllText(langDst, "a=ja\nb=old\n", Encoding.UTF8);
        var langResult = merger.MergeTargetFromJar(langSrc, langDst);
        Expect(langResult.WasMerged, "legacy merged");
        Expect(langResult.PreservedKeys == 1, "legacy preserved");
        var langText = File.ReadAllText(langDst, Encoding.UTF8);
        Expect(langText.Contains("a=ja", StringComparison.Ordinal), "legacy kept existing value");
        Expect(langText.Contains("c=new", StringComparison.Ordinal), "legacy added source key");
        Expect(!langText.Contains("b=old", StringComparison.Ordinal), "legacy dropped extra dest key");
    }

    private static void TestR2InvalidKeepsExistingBytes()
    {
        using var temp = new TempDir();
        var merger = new LangFileMerger();

        var valid = "{\"a\":\"keep\"}";
        var invalid = "{not-json";

        var src = temp.File("invalid-src.json");
        var dst = temp.File("valid-dst.json");
        File.WriteAllText(src, invalid, Encoding.UTF8);
        File.WriteAllBytes(dst, Encoding.UTF8.GetBytes(valid));
        var before = File.ReadAllBytes(dst);
        ExpectThrowsInvalidData(() => merger.MergeTargetFromJar(src, dst), "invalid source");
        Expect(before.SequenceEqual(File.ReadAllBytes(dst)), "invalid source must not rewrite dest");

        File.WriteAllText(src, valid, Encoding.UTF8);
        File.WriteAllBytes(dst, Encoding.UTF8.GetBytes(invalid));
        before = File.ReadAllBytes(dst);
        ExpectThrowsInvalidData(() => merger.MergeTargetFromJar(src, dst), "invalid dest");
        Expect(before.SequenceEqual(File.ReadAllBytes(dst)), "invalid dest must not rewrite dest");

        var badUtf8 = new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D };
        File.WriteAllBytes(src, badUtf8);
        File.WriteAllBytes(dst, Encoding.UTF8.GetBytes(valid));
        before = File.ReadAllBytes(dst);
        ExpectThrowsInvalidData(() => merger.MergeTargetFromJar(src, dst), "invalid utf8 source");
        Expect(before.SequenceEqual(File.ReadAllBytes(dst)), "invalid utf8 source must not rewrite dest");

        File.WriteAllText(src, valid, Encoding.UTF8);
        File.WriteAllBytes(dst, badUtf8);
        before = File.ReadAllBytes(dst);
        ExpectThrowsInvalidData(() => merger.MergeTargetFromJar(src, dst), "invalid utf8 dest");
        Expect(before.SequenceEqual(File.ReadAllBytes(dst)), "invalid utf8 dest must not rewrite dest");
    }

    private static void TestR2FallbackOverwriteApiKept()
    {
        var result = LangFileMergeResult.FallbackOverwrite("kept");
        Expect(result.UsedFallbackOverwrite, "UsedFallbackOverwrite remains");
        Expect(result.Warning == "kept", "fallback warning remains");
    }

    private static void TestR3SameNameCollisionAndSubsetContext()
    {
        using var temp = new TempDir();
        var cand = new LangCandidate { ModId = "mymod", ArchiveLangPath = "assets/mymod/lang", Files = ["en_us.json"] };
        var scanA = CreateScan("a/same.jar", "mymod");
        var scanB = CreateScan("b/same.jar", "mymod");
        var all = new List<JarScanResult> { scanA, scanB };

        var dirA = LangPathResolver.ResolveEditDirectory(temp.Path, scanA, cand, null, all);
        var dirB = LangPathResolver.ResolveEditDirectory(temp.Path, scanB, cand, null, all);
        var dirAAgain = LangPathResolver.ResolveEditDirectory(temp.Path, scanA, cand, null, all);

        Expect(dirA == dirAAgain, "output path is deterministic");
        Expect(!dirA.Equals(dirB, StringComparison.OrdinalIgnoreCase), "a/same.jar and b/same.jar get different dirs by category");
        Expect(dirA.Replace('\\', '/').EndsWith("/a/same", StringComparison.OrdinalIgnoreCase), "dirA keeps category a/same");
        Expect(dirB.Replace('\\', '/').EndsWith("/b/same", StringComparison.OrdinalIgnoreCase), "dirB keeps category b/same");

        // 同一カテゴリ・同一JAR名の別スキャンが存在する場合（衝突時のハッシュトークン付加検証）
        var scanCollidingA = CreateScan("same_cat/same.jar", "mymod");
        var scanCollidingB = new JarScanResult
        {
            JarFileName = "same.jar",
            JarFilePath = Path.Combine("C:\\dummy-other", "same_cat", "same.jar"),
            RelativeJarPath = Path.Combine("same_cat", "same.jar"),
            Integrity = JarIntegrity.OK,
            Strategy = ProcessingStrategy.LangFound,
            LangCandidates = [cand]
        };
        var collisionList = new List<JarScanResult> { scanCollidingA, scanCollidingB };

        var dirColA = LangPathResolver.ResolveEditDirectory(temp.Path, scanCollidingA, cand, null, collisionList);
        var dirSubsetNoCollision = LangPathResolver.ResolveEditDirectory(temp.Path, scanCollidingA, cand, null, [scanCollidingA]);

        Expect(dirColA.Contains("__"), "colliding jar path uses hash discriminator");
        Expect(!dirSubsetNoCollision.Contains("__"), "subset-only context without collision keeps clean jar path");
        Expect(!dirColA.Equals(dirSubsetNoCollision, StringComparison.OrdinalIgnoreCase),
            "full scan context must be passed for stable collision results");
    }

    private static void TestR3MappingLegacyKeepAndOwnershipRefuse()
    {
        using var temp = new TempDir();
        var cand = new LangCandidate { ModId = "mymod", ArchiveLangPath = "assets/mymod/lang", Files = ["en_us.json"] };
        var scanA = CreateScan("a/same.jar", "mymod");
        var scanB = CreateScan("b/same.jar", "mymod");
        var all = new List<JarScanResult> { scanA, scanB };

        var mapping = new WorkspaceMapping();
        mapping.Entries.Add(new TranslationMappingEntry
        {
            EditPath = "kept_custom/en_us.json",
            JarRelativePath = scanA.RelativeJarPath,
            ModId = "mymod",
            ArchivePath = "assets/mymod/lang/en_us.json"
        });
        var mapped = LangPathResolver.ResolveEditDirectory(temp.Path, scanA, cand, mapping, all);
        Expect(Path.GetFileName(mapped) == "kept_custom", "unique mapping edit path is kept");

        var legacy = LangPathResolver.GetLegacyExternalLangDirectory(temp.Path, scanB, cand);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "en_us.json"), "LEGACY");
        var legacyResolved = LangPathResolver.ResolveEditDirectory(temp.Path, scanB, cand, null, all);
        Expect(Path.GetFullPath(legacyResolved) == Path.GetFullPath(legacy), "unique legacy path is kept");
        Expect(File.ReadAllText(Path.Combine(legacy, "en_us.json")) == "LEGACY", "legacy files remain");

        var owned = Path.Combine(temp.Path, "other", "same");
        Directory.CreateDirectory(owned);
        var ownedFile = Path.Combine(owned, "en_us.json");
        File.WriteAllText(ownedFile, "KEEP");
        var ownerMapping = new WorkspaceMapping();
        ownerMapping.Entries.Add(new TranslationMappingEntry
        {
            EditPath = "other/same/en_us.json",
            JarRelativePath = "other/same.jar",
            ModId = "othermod",
            ArchivePath = "assets/othermod/lang/en_us.json"
        });
        var colliding = CreateScan("other/same.jar", "newmod");
        var mappingJson = ownerMapping.Entries[0].JarRelativePath;
        ExpectThrowsInvalidData(
            () => LangPathResolver.ResolveEditDirectory(temp.Path, colliding, colliding.LangCandidates[0], ownerMapping, [colliding]),
            "ownership collision");
        Expect(ownerMapping.Entries.Count == 1, "mapping not repaired");
        Expect(ownerMapping.Entries[0].JarRelativePath == mappingJson, "owner mapping remains");
        Expect(File.ReadAllText(ownedFile) == "KEEP", "owned translation remains");
    }

    private static void TestR3RegisterMappingRefuseRetarget()
    {
        using var temp = new TempDir();
        var output = temp.CreateSub("out");
        var filePath = Path.Combine(output, "foo", "en_us.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, "{}");

        var mapping = new WorkspaceMapping();
        mapping.Entries.Add(new TranslationMappingEntry
        {
            EditPath = "foo/en_us.json",
            JarRelativePath = "owner/owner.jar",
            ModId = "owner",
            ArchivePath = "assets/owner/lang/en_us.json"
        });

        ExpectThrowsInvalidData(
            () => InvokeStatic(typeof(Executor), "RegisterMappingEntry",
                mapping, output, filePath, "other/other.jar", "other", "assets/other/lang/en_us.json"),
            "retarget refuse");
        Expect(mapping.Entries.Count == 1, "mapping count unchanged");
        Expect(mapping.Entries[0].JarRelativePath == "owner/owner.jar", "owner not stolen");

        InvokeStatic(typeof(Executor), "RegisterMappingEntry",
            mapping, output, filePath, "owner/owner.jar", "owner", "assets/owner/lang/en_us.json");
        Expect(mapping.Entries.Count == 1, "same owner updates in place");
    }

    private static void TestR4BackupZipRootsAndContents()
    {
        using var temp = new TempDir();
        var executor = new Executor(new Logger());

        var extCase = temp.CreateSub("r4-ext");
        var target = Directory.CreateDirectory(Path.Combine(extCase, "target")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(extCase, "external")).FullName;
        File.WriteAllText(Path.Combine(target, "t.txt"), "T");
        File.WriteAllText(Path.Combine(external, "ja_jp.json"), "JA");
        Await(executor.CreateExtractionBackupAsync(target, external, CancellationToken.None));
        var extZips = FindBackupZips(extCase);
        Expect(extZips.Length == 2, "external output backs up both roots");
        var targetZip = RequireZipContaining(extZips, "t.txt");
        var outputZip = RequireZipContaining(extZips, "ja_jp.json");
        Expect(targetZip != outputZip, "external roots produce distinct zips");
        Expect(!ZipContains(targetZip, "ja_jp.json"), "target zip does not include external translations");
        Expect(!JarPathPolicy.IsSameOrUnder(targetZip, target), "target zip is not self-contained");
        Expect(!JarPathPolicy.IsSameOrUnder(outputZip, external), "output zip is not self-contained");

        var sameCase = temp.CreateSub("r4-same");
        var same = Directory.CreateDirectory(Path.Combine(sameCase, "mods")).FullName;
        File.WriteAllText(Path.Combine(same, "s.txt"), "S");
        Await(executor.CreateExtractionBackupAsync(same, same, CancellationToken.None));
        Expect(FindBackupZips(sameCase).Length == 1, "same root backs up once");
        Expect(ZipContains(FindBackupZips(sameCase)[0], "s.txt"), "same-root zip has files");

        var nestedCase = temp.CreateSub("r4-nested");
        var nestedRoot = Directory.CreateDirectory(Path.Combine(nestedCase, "nested")).FullName;
        File.WriteAllText(Path.Combine(nestedRoot, "root.txt"), "R");
        var nestedOut = Path.Combine(nestedRoot, "out");
        Directory.CreateDirectory(nestedOut);
        File.WriteAllText(Path.Combine(nestedOut, "trans.txt"), "TR");
        Await(executor.CreateExtractionBackupAsync(nestedRoot, nestedOut, CancellationToken.None));
        var nestedZips = FindBackupZips(nestedCase);
        Expect(nestedZips.Length == 1, "contained output backs up larger root once");
        Expect(ZipContains(nestedZips[0], "root.txt"), "contained zip includes target");
        Expect(ZipContains(nestedZips[0], "out/trans.txt"), "contained zip includes nested translations");

        var missingCase = temp.CreateSub("r4-missing");
        var onlyTarget = Directory.CreateDirectory(Path.Combine(missingCase, "mods")).FullName;
        File.WriteAllText(Path.Combine(onlyTarget, "o.txt"), "O");
        var missingOut = Path.Combine(missingCase, "missing-out");
        Await(executor.CreateExtractionBackupAsync(onlyTarget, missingOut, CancellationToken.None));
        Expect(FindBackupZips(missingCase).Length == 1, "missing output root is not backed up");
        Expect(!Directory.Exists(missingOut), "missing output is not created");

        var uniqCase = temp.CreateSub("r4-unique");
        var uniq = Directory.CreateDirectory(Path.Combine(uniqCase, "mods")).FullName;
        File.WriteAllText(Path.Combine(uniq, "u.txt"), "U");
        Await(executor.CreateBackupAsync(uniq, CancellationToken.None));
        Await(executor.CreateBackupAsync(uniq, CancellationToken.None));
        Expect(FindBackupZips(uniqCase).Length == 2, "guid unique names avoid same-root zip name collision");

        var importCase = temp.CreateSub("r4-import");
        var importTarget = Directory.CreateDirectory(Path.Combine(importCase, "mods")).FullName;
        var importOut = Directory.CreateDirectory(Path.Combine(importCase, "langout")).FullName;
        File.WriteAllText(Path.Combine(importTarget, "jar-side.txt"), "J");
        File.WriteAllText(Path.Combine(importOut, "trans-only.txt"), "X");
        Await(executor.CreateBackupAsync(importTarget, CancellationToken.None));
        var importZips = FindBackupZips(importCase);
        Expect(importZips.Length == 1, "JAR backup still targets TargetDir only");
        Expect(ZipContains(importZips[0], "jar-side.txt"), "JAR backup contains target");
        Expect(!ZipContains(importZips[0], "trans-only.txt"), "JAR backup does not include external output");
    }

    private static void TestR5DangerousPathsAndResourcePack()
    {
        using var temp = new TempDir();
        var output = temp.CreateSub("out");
        var safeEdit = Path.Combine(output, "foo", "en_us.json");
        var langEdit = Path.Combine(output, "legacy", "en_us.json");
        Directory.CreateDirectory(Path.GetDirectoryName(safeEdit)!);
        Directory.CreateDirectory(Path.GetDirectoryName(langEdit)!);
        File.WriteAllText(safeEdit, "{\"k\":\"v\"}");
        File.WriteAllText(langEdit, "{\"k\":\"v\"}");

        var scan = new JarScanResult
        {
            JarFileName = "mod.jar",
            JarFilePath = Path.Combine(temp.Path, "mod.jar"),
            RelativeJarPath = "mod.jar",
            Integrity = JarIntegrity.OK,
            Strategy = ProcessingStrategy.LangFound,
            LangCandidates =
            [
                new LangCandidate { ModId = "foo", ArchiveLangPath = "assets/foo/lang", Files = ["en_us.json"] },
                new LangCandidate { ModId = "legacy", ArchiveLangPath = "lang", Files = ["en_us.json"] }
            ]
        };

        var mapping = new WorkspaceMapping();
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", "assets/foo/lang/en_us.json"));
        mapping.Entries.Add(Entry("legacy/en_us.json", "mod.jar", "legacy", "lang/en_us.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", "assets/foo/lang/../secret.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", "assets/foo/lang/./en_us.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", "/assets/foo/lang/en_us.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", @"assets\foo\lang\en_us.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", @"C:\assets\foo\lang\en_us.json"));
        mapping.Entries.Add(Entry("foo/en_us.json", "mod.jar", "foo", "assets/foo/lang/en_us.json:stream"));

        var plan = new JarLangImporter(new Logger()).CreatePlan([scan], output, mapping);
        Expect(plan.SourceFileCount == 2, "safe assets + non-assets lang imported; dangerous skipped");
        Expect(plan.JarPlans[0].Files.Any(f => f.ArchivePath == "assets/foo/lang/en_us.json"), "standard lang kept for JAR");
        Expect(plan.JarPlans[0].Files.Any(f => f.ArchivePath == "lang/en_us.json"), "assets-less lang kept for JAR");
        Expect(plan.JarPlans[0].Files.All(f =>
                f.ArchivePath is "assets/foo/lang/en_us.json" or "lang/en_us.json"),
            "dangerous archive paths never reach JAR/RP plan");

        Expect(ResourcePackBuilder.IsStandardLangPath("assets/foo/lang/en_us.json"), "safe standard");
        Expect(!ResourcePackBuilder.IsStandardLangPath("lang/en_us.json"), "non-standard");
        Expect(!ResourcePackBuilder.IsStandardLangPath("assets/foo/lang/../x.json"), "traversal");
        Expect(!ResourcePackBuilder.IsStandardLangPath("assets/foo/lang/./en_us.json"), "dot segment");
        Expect(!ResourcePackBuilder.IsStandardLangPath("/assets/foo/lang/en_us.json"), "absolute");
        Expect(!ResourcePackBuilder.IsStandardLangPath(@"assets\foo\lang\en_us.json"), "backslash");
        Expect(!ResourcePackBuilder.IsStandardLangPath("C:/assets/foo/lang/en_us.json"), "drive");
        Expect(!ResourcePackBuilder.IsStandardLangPath("assets/foo/lang/en_us.json:stream"), "ads");

        var outside = Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(outside, "KEEP");
        var src = temp.File("pack-src.json");
        File.WriteAllText(src, "{\"a\":\"1\"}");

        var crafted = new JarImportBatchPlan();
        crafted.JarPlans.Add(new JarImportPlan { ScanResult = scan });
        crafted.JarPlans[0].Files.Add(new JarImportFile(src, "assets/foo/lang/en_us.json"));
        crafted.JarPlans[0].Files.Add(new JarImportFile(src, "lang/en_us.json"));
        crafted.JarPlans[0].Files.Add(new JarImportFile(src, "assets/foo/lang/../../../outside.txt"));

        var folderDest = Path.Combine(temp.Path, "rp-folder");
        var folderResult = new ResourcePackBuilder().BuildFolder(crafted, folderDest);
        Expect(File.Exists(Path.Combine(folderDest, "assets", "foo", "lang", "en_us.json")), "safe folder write");
        Expect(!File.Exists(Path.Combine(folderDest, "lang", "en_us.json")), "non-standard skipped in folder");
        Expect(folderResult.SkippedNonStandardPaths.Count >= 2, "non-standard and dangerous skipped");
        Expect(File.ReadAllText(outside) == "KEEP", "external file unchanged by folder build");

        var zipDest = Path.Combine(temp.Path, "rp.zip");
        var zipResult = new ResourcePackBuilder().BuildZip(crafted, zipDest);
        using (var zip = ZipFile.OpenRead(zipDest))
        {
            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            Expect(names.Contains("assets/foo/lang/en_us.json"), "safe zip entry");
            Expect(!names.Contains("lang/en_us.json"), "non-standard skipped in zip");
            Expect(!names.Any(n => n.Contains("outside", StringComparison.OrdinalIgnoreCase)), "dangerous skipped in zip");
        }
        Expect(zipResult.SkippedNonStandardPaths.Count >= 2, "zip skip info");
        Expect(File.ReadAllText(outside) == "KEEP", "external file unchanged by zip build");
    }

    private static void TestJarPathPolicyRelativeBoundary()
    {
        using var temp = new TempDir();
        var insideJar = Path.Combine(temp.Path, "inside.jar");
        File.WriteAllText(insideJar, "x");
        var relative = JarPathPolicy.GetRelativeJarPath(temp.Path, insideJar);
        Expect(relative.Replace('\\', '/') == "inside.jar", "root jar returns relative path");

        var outsideJar = Path.Combine(Path.GetDirectoryName(temp.Path)!, "outside-" + Guid.NewGuid().ToString("N") + ".jar");
        ExpectThrowsInvalidData(
            () => JarPathPolicy.GetRelativeJarPath(temp.Path, outsideJar),
            "outside jar");
    }

    private static TranslationMappingEntry Entry(string edit, string jar, string modId, string archive) =>
        new()
        {
            EditPath = edit,
            JarRelativePath = jar,
            ModId = modId,
            ArchivePath = archive
        };

    private static MainViewModel CreateUninitializedVm()
    {
        var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        var type = typeof(MainViewModel);
        FindField(type, "<Mods>k__BackingField").SetValue(vm, new ObservableCollection<ModItemViewModel>());
        FindField(type, "_scanResults").SetValue(vm, new List<JarScanResult>());
        FindField(type, "_executionSubsetToModsIndex").SetValue(vm, new List<int>());
        FindField(type, "_activeActionLabel").SetValue(vm, "実行");
        FindField(type, "_statusBarText").SetValue(vm, string.Empty);
        return vm;
    }

    private static void AddPair(MainViewModel vm, string relative, bool selected)
    {
        GetScanResults(vm).Add(CreateScan(relative));
        vm.Mods.Add(new ModItemViewModel
        {
            JarFileName = relative.Replace('\\', '/'),
            Integrity = JarIntegrity.OK,
            LangCount = 1,
            Strategy = ProcessingStrategy.LangFound,
            ExtractCount = 1,
            CreateDirCount = 0,
            CopyCount = 0,
            ConflictCopyCount = 0,
            CleanupCount = 0,
            SkipCount = 0,
            IsSelected = selected
        });
    }

    private static List<JarScanResult> GetScanResults(MainViewModel vm) =>
        (List<JarScanResult>)FindField(typeof(MainViewModel), "_scanResults").GetValue(vm)!;

    private static JarScanResult CreateScan(string relative, string modId = "foo")
    {
        var fileName = Path.GetFileName(relative.Replace('\\', '/'));
        return new JarScanResult
        {
            JarFileName = fileName,
            JarFilePath = Path.Combine("C:\\dummy-scan", relative.Replace('/', Path.DirectorySeparatorChar)),
            RelativeJarPath = relative.Replace('/', Path.DirectorySeparatorChar),
            Integrity = JarIntegrity.OK,
            Strategy = ProcessingStrategy.LangFound,
            LangCandidates =
            [
                new LangCandidate
                {
                    ModId = modId,
                    ArchiveLangPath = "assets/" + modId + "/lang",
                    Files = ["en_us.json"]
                }
            ]
        };
    }

    private static FieldInfo FindField(Type type, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        return type.GetField(name, flags)
            ?? throw new Exception("field not found: " + name);
    }

    private static T InvokeInstance<T>(object target, string name, params object?[] args)
    {
        var result = InvokeInstance(target, name, args);
        return (T)result!;
    }

    private static object? InvokeInstance(object target, string name, params object?[] args)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length)
            ?? throw new Exception("method not found: " + name);
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void InvokeStatic(Type type, string name, params object?[] args)
    {
        var method = type
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args.Length)
            ?? throw new Exception("static method not found: " + name);
        try
        {
            method.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void AwaitInstance(object target, string name)
    {
        var result = InvokeInstance(target, name);
        if (result is Task task)
            task.GetAwaiter().GetResult();
    }

    private static void Await(Task task) => task.GetAwaiter().GetResult();

    private static void ExpectThrowsInvalidData(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidDataException ex)
        {
            Expect(ex.Message.Length > 0, label + " has message");
            return;
        }

        throw new Exception(label + " expected InvalidDataException");
    }

    private static string[] FindBackupZips(string parent) =>
        Directory.GetFiles(parent, "*_backup_*.zip");

    private static bool ZipContains(string zipPath, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.Any(e => e.FullName.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string RequireZipContaining(string[] zips, string relative)
    {
        var match = zips.FirstOrDefault(z => ZipContains(z, relative));
        Expect(match != null, "zip containing " + relative);
        return match!;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore temp cleanup
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "mlo-reg-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public string CreateSub(string name)
        {
            var dir = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ignore temp cleanup
            }
        }
    }
}
