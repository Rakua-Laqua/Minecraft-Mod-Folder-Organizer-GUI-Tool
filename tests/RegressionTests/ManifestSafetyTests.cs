using System.IO;
using ModLangOrganizer.Infrastructure;

namespace RegressionTests;

internal static partial class Program
{
    private static void TestDeployManifestReplacementFailure(bool renameOriginal)
    {
        var failNextReplacement = true;
        var service = new ModDeploymentService((staged, target) =>
        {
            if (!failNextReplacement)
            {
                File.Replace(staged, target, null, ignoreMetadataErrors: true);
                return;
            }

            failNextReplacement = false;
            // ReplaceFileW errors 1176/1177 can remove the original destination name.
            // Inject those documented side effects instead of relying on a file lock.
            if (renameOriginal)
                File.Move(target, staged + ".displaced");
            else
                File.Delete(target);
            Expect(!File.Exists(target) && File.Exists(staged), "置換失敗時のファイル状態を再現できていません。");
            throw new IOException("Injected ReplaceFileW failure", unchecked((int)0x80070000) | (renameOriginal ? 1177 : 1176));
        });
        using var fixture = new DeploymentFixture(service);

        var result = fixture.Execute();
        Expect(!failNextReplacement, "置換失敗の注入箇所に到達していません。");
        Expect(!result.Success, "管理情報の置換失敗が成功扱いになっています。");
        Expect(result.AddedCount == 0 && result.UpdatedCount == 0 && result.DeletedCount == 0, "復元後の変更件数が残っています。");
        Expect(File.Exists(fixture.ManifestPath), "置換失敗で消えた旧管理情報が復元されていません。");
        fixture.ExpectOriginalState();
        fixture.ExpectNoTransaction();

        var retry = fixture.Execute();
        Expect(retry.Success && retry.AddedCount == 1 && retry.UpdatedCount == 1 && retry.DeletedCount == 1, "管理情報を復元した後に再実行できません。");
        Expect(!File.Exists(Path.Combine(fixture.Target, "old.jar")), "再実行で旧管理対象が削除されていません。");
        Expect(File.ReadAllText(Path.Combine(fixture.Target, "update.jar")) == "updated content", "再実行で更新対象が反映されていません。");
        Expect(service.LoadManifest(fixture.Target).SetEquals(new[] { "a.jar", "update.jar" }), "再実行後の管理対象が不正です。");
        fixture.ExpectNoTransaction();
    }

    private static void TestDeployManifestRecoveryData()
    {
        FileStream? manifestLock = null;
        var reachedReplacement = false;
        var service = new ModDeploymentService((staged, target) =>
        {
            reachedReplacement = true;
            File.Delete(target);
            // A new exclusive handle prevents rollback after the old manifest is lost.
            manifestLock = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            throw new IOException("Injected destructive replacement failure", unchecked((int)0x80070498));
        });
        using var fixture = new DeploymentFixture(service);
        try
        {
            var result = fixture.Execute();
            Expect(reachedReplacement && !result.Success, "管理情報の破壊的な置換失敗を検出していません。");
            Expect(result.ErrorMessage?.Contains("自動復元に失敗") == true, "管理情報の復元失敗が通知されていません。");
            Expect(result.BackupPath != null && Directory.Exists(result.BackupPath), "管理情報の復旧データが保持されていません。");
            Expect(result.ErrorMessage!.Contains(result.BackupPath!), "復旧データの場所がエラーに含まれていません。");
            var savedManifest = Path.Combine(result.BackupPath!, "originals", ModDeploymentService.ManifestFileName);
            Expect(File.ReadAllBytes(savedManifest).SequenceEqual(fixture.ManifestBytes), "手動復旧用の旧管理情報が完全には保持されていません。");
            Expect(!result.Logs.Any(log => log.Contains("配置先と管理情報は変更前の状態です")), "復元できていないのに復元済みと報告しています。");
            Expect(File.ReadAllText(Path.Combine(fixture.Target, "old.jar")) == "old managed", "管理情報の復元失敗時に削除済みMODが戻っていません。");
            Expect(File.ReadAllText(Path.Combine(fixture.Target, "update.jar")) == "before", "管理情報の復元失敗時に更新済みMODが戻っていません。");
            Expect(!File.Exists(Path.Combine(fixture.Target, "a.jar")), "管理情報の復元失敗時に新規MODが残っています。");
        }
        finally
        {
            manifestLock?.Dispose();
        }
    }
}
