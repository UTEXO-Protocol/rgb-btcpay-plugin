using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreUploadStagingDirectoryTests
{
    const string ControllerFile = "Controllers/RGBController.cs";

    static RGBConfiguration ConfigurationUnderAFreshBaseDir() =>
        new(Path.Combine(Path.GetTempPath(), $"rgb-upload-staging-tests-{Guid.NewGuid():N}"));

    [Fact]
    public void UploadDirectory_IsDeletedWithTheScratchDirectoryRgbLibCreatesBesideTheBackupFile()
    {
        var cfg = ConfigurationUnderAFreshBaseDir();
        try
        {
            var uploadDir = RgbRestoreUploadStaging.CreateDirectoryForAttempt(cfg, "regtest");
            var backupFile = Path.Combine(uploadDir, RgbRestoreUploadStaging.UploadedBackupFileName);
            File.WriteAllBytes(backupFile, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

            var rgbLibScratch = Path.Combine(uploadDir, ".tmpAbCdEf");
            Directory.CreateDirectory(rgbLibScratch);
            File.WriteAllText(Path.Combine(rgbLibScratch, "backup.zip"), "decrypted inner archive");
            File.WriteAllText(Path.Combine(rgbLibScratch, "backup.enc"), "outer archive payload");

            RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(
                uploadDir, NullLogger.Instance);

            Assert.False(Directory.Exists(rgbLibScratch),
                "the rgb-lib scratch directory created beside the backup file must not survive the attempt: "
                + "it holds the DECRYPTED inner archive and nothing else ever sweeps it");
            Assert.False(Directory.Exists(uploadDir),
                "the per-attempt upload directory must not survive the attempt");
        }
        finally
        {
            if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, true);
        }
    }

    [Fact]
    public void UploadDirectory_LandsWhereTheStartupSweepAlreadyLooks()
    {
        var cfg = ConfigurationUnderAFreshBaseDir();
        foreach (var network in NetworkSettings.AvailableNetworks)
        {
            var attempt = Guid.NewGuid();
            var resolved = RgbRestoreUploadStaging.ResolveDirectoryForAttempt(cfg, network, attempt);

            var sweptWalletsDir = Path.Combine(
                cfg.RgbBaseDir, RGBConfiguration.MapNetworkFolder(network), "rgb-wallets");
            Assert.Equal(sweptWalletsDir, Path.GetDirectoryName(resolved));
            Assert.StartsWith(RGBWalletService.RestoreStagingPrefix, Path.GetFileName(resolved), StringComparison.Ordinal);
            Assert.Contains(attempt.ToString("N"), Path.GetFileName(resolved), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StartupSweep_ReclaimsAnUploadDirectoryAKilledProcessLeftBehind()
    {
        var cfg = ConfigurationUnderAFreshBaseDir();
        try
        {
            var uploadDir = RgbRestoreUploadStaging.CreateDirectoryForAttempt(cfg, "regtest");
            var rgbLibScratch = Path.Combine(uploadDir, ".tmpStranded");
            Directory.CreateDirectory(rgbLibScratch);
            await File.WriteAllTextAsync(Path.Combine(rgbLibScratch, "backup.zip"), "stranded");

            var stale = DateTime.UtcNow - TimeSpan.FromHours(4);
            Directory.SetCreationTimeUtc(uploadDir, stale);
            Directory.SetLastWriteTimeUtc(uploadDir, stale);

            var runner = new RGBPluginMigrationRunner(
                null!, null!, null!, cfg, NullLogger<RGBPluginMigrationRunner>.Instance);
            runner.CleanupStaleStagingDirs();

            Assert.False(Directory.Exists(uploadDir),
                "a BTCPay-process kill leaves the upload directory behind, so the existing startup sweep "
                + "must be able to reclaim it without host shell access");
        }
        finally
        {
            if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, true);
        }
    }

    [Fact]
    public void DeleteDirectoryForAttempt_NeverThrows_ForNullAbsentOrAlreadyRemovedDirectories()
    {
        var absent = Path.Combine(Path.GetTempPath(), $"rgb-upload-staging-absent-{Guid.NewGuid():N}");

        RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(null, NullLogger.Instance);
        RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside("", NullLogger.Instance);
        RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(absent, NullLogger.Instance);

        Assert.False(Directory.Exists(absent));
    }

    [Fact]
    public void DeleteDirectoryForAttempt_RemovesASymlinkInsideItWithoutTouchingTheTarget()
    {
        var cfg = ConfigurationUnderAFreshBaseDir();
        var outside = Path.Combine(Path.GetTempPath(), $"rgb-upload-staging-outside-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outside);
            var outsideFile = Path.Combine(outside, "wallet.sqlite");
            File.WriteAllText(outsideFile, "must survive");

            var uploadDir = RgbRestoreUploadStaging.CreateDirectoryForAttempt(cfg, "regtest");
            Directory.CreateSymbolicLink(Path.Combine(uploadDir, "escape"), outside);

            RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(
                uploadDir, NullLogger.Instance);

            Assert.False(Directory.Exists(uploadDir));
            Assert.True(File.Exists(outsideFile),
                "the recursive delete may only remove what the plugin created for this attempt: a symlink "
                + "planted inside the upload directory must be unlinked, never followed");
        }
        finally
        {
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
            if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, true);
        }
    }

    [Fact]
    public void RestoreFromBackupSource_StagesIntoASweptDirectoryAndDeletesTheDirectoryNotJustTheFile()
    {
        var tree = PluginCompilation.Shared.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, "RGBController", "RestoreFromBackup");
        var body = RoslynPins.BodyOf(method);

        var tempPathCalls = body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => InvokedName(invocation) == "GetTempPath");
        Assert.True(tempPathCalls == 0,
            $"RestoreFromBackup must not stage the uploaded backup in the system temp directory; found "
            + $"{tempPathCalls} GetTempPath call(s). rgb-lib creates its scratch directory as a SIBLING of "
            + "the backup file and that scratch survives every SIGKILLed restore, so staging in a directory "
            + "no sweep can reach strands the decrypted archive forever");

        var finallyBlock = method.DescendantNodes().OfType<FinallyClauseSyntax>().Single().Block;

        var directoryDeletes = finallyBlock.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => InvokedName(invocation)
                == "DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside");
        Assert.True(directoryDeletes == 1,
            $"the restore finally must delete the whole per-attempt upload DIRECTORY exactly once; found "
            + $"{directoryDeletes} such call(s)");

        var fileDeletes = finallyBlock.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => InvokedName(invocation) == "Delete" || InvokedName(invocation) == "Exists");
        Assert.True(fileDeletes == 0,
            $"the restore finally must not fall back to deleting the backup FILE alone, which leaves "
            + $"rgb-lib's sibling scratch directory behind; found {fileDeletes} File.Delete/File.Exists call(s)");
    }

    static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty
    };
}
