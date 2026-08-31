using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using BTCPayServer.Plugins.RgbUtexo;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreLimitClampTests
{
    [Fact]
    public void AZeroRestoreCpuLimitFromTheConfigurationFileCannotReachPrlimit()
    {
        var limits = new RGBConfiguration { RestoreCpuLimitSeconds = 0 }.ToRestoreLimits();

        Assert.True(limits.CpuLimit >= TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin),
            $"a restore_cpu_limit_seconds of 0 reached the child as {limits.CpuLimit}, which is "
            + "prlimit --cpu=0: it refuses every backup restore, and restore is the recovery path");
    }

    [Fact]
    public void EveryRestoreLimitReadFromTheConfigurationFileIsFlooredAtItsUsableMinimum()
    {
        var limits = new RGBConfiguration
        {
            RestoreTimeoutSeconds = 0,
            RestoreDiskCapBytes = 0,
            RestoreRamCapBytes = 0,
            RestoreCpuLimitSeconds = 0,
            RestorePollMs = 0,
            RestoreReapGraceSeconds = 0,
            RestoreMaxStagingEntries = 0
        }.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin), limits.Timeout);
        Assert.Equal(RGBConfiguration.RestoreDiskCapMinBytes, limits.DiskCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMinBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMin), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(RGBConfiguration.RestorePollMsMin), limits.Poll);
        Assert.Equal(
            TimeSpan.FromSeconds(RGBConfiguration.RestoreReapGraceSecondsMin), limits.ReapGrace);
        Assert.Equal(RGBConfiguration.RestoreMinStagingEntries, limits.MaxStagingEntries);
    }

    [Fact]
    public void EveryRestoreLimitReadFromTheConfigurationFileIsCappedAtItsCeiling()
    {
        var limits = new RGBConfiguration
        {
            RestoreTimeoutSeconds = int.MaxValue,
            RestoreDiskCapBytes = long.MaxValue,
            RestoreRamCapBytes = long.MaxValue,
            RestoreCpuLimitSeconds = int.MaxValue,
            RestorePollMs = int.MaxValue,
            RestoreReapGraceSeconds = int.MaxValue,
            RestoreMaxStagingEntries = int.MaxValue
        }.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMax), limits.Timeout);
        Assert.Equal(RGBConfiguration.RestoreDiskCapMaxBytes, limits.DiskCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMaxBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(RGBConfiguration.RestoreSecondsMax), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(RGBConfiguration.RestorePollMsMax), limits.Poll);
        Assert.Equal(
            TimeSpan.FromSeconds(RGBConfiguration.RestoreReapGraceSecondsMax), limits.ReapGrace);
        Assert.Equal(int.MaxValue, limits.MaxStagingEntries);
    }

    [Fact]
    public void TheShippedRestoreDefaultsPassThroughTheClampUnchanged()
    {
        var cfg = new RGBConfiguration();
        var limits = cfg.ToRestoreLimits();

        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreTimeoutSeconds), limits.Timeout);
        Assert.Equal(cfg.RestoreDiskCapBytes, limits.DiskCapBytes);
        Assert.Equal(cfg.RestoreRamCapBytes, limits.RamCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreCpuLimitSeconds), limits.CpuLimit);
        Assert.Equal(TimeSpan.FromMilliseconds(cfg.RestorePollMs), limits.Poll);
        Assert.Equal(TimeSpan.FromSeconds(cfg.RestoreReapGraceSeconds), limits.ReapGrace);
        Assert.Equal(cfg.RestoreMaxStagingEntries, limits.MaxStagingEntries);
    }

    const long ResidentSetMeasuredOutsideAScryptArenaOfTwoHundredAndFiftySixMegabytes =
        290L * 1024 * 1024 - 128L * 8 * (1L << 18);

    [Fact]
    public void TheShippedRestoreRamCapAdmitsTheWholeProcessARestoreAtTheScryptCeilingNeeds()
    {
        var limits = new RGBConfiguration().ToRestoreLimits();
        var residentSetOfABackupAdmittedAtTheGuardsCeiling =
            Services.RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes
            + ResidentSetMeasuredOutsideAScryptArenaOfTwoHundredAndFiftySixMegabytes;

        Assert.Equal(
            Services.RestoreKillReason.None,
            Services.RestoreWatchdog.ShouldKill(
                dirSizeBytes: 0,
                rssBytes: residentSetOfABackupAdmittedAtTheGuardsCeiling,
                limits));
    }

    [Fact]
    public void TheRestoreRamFloorAdmitsTheScryptCeilingPlusTheResidentSetItIsNotMeasuredWith()
    {
        Assert.True(
            RGBConfiguration.RestoreRamMinBytes
                >= Services.RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes
                    + ResidentSetMeasuredOutsideAScryptArenaOfTwoHundredAndFiftySixMegabytes,
            "the pre-flight guard bounds the scrypt ARENA alone, computed arithmetically from the "
            + "backup's own log_n and r, while RestoreWatchdog.ShouldKill compares this cap against "
            + "the helper process's TOTAL resident set — the arena PLUS the CLR, the helper "
            + "assemblies, librgblibcffi and the decrypt/inflate buffers. Total resident set is "
            + "strictly greater than the arena, so a floor EQUAL to the guard's ceiling kills a "
            + "backup the guard just admitted, and because this constant is the clamp's floor an "
            + "operator cannot compensate by lowering it. Restore is the only recovery route for a "
            + "funded wallet, so that is a permanent false REJECT.");
        Assert.True(
            RGBConfiguration.RestoreDiskCapMinBytes
                >= Services.RgbBackupValidator.MaxTotalUncompressedBytes,
            "the decompressed wallet directory is never smaller than the compressed, encrypted archive "
            + "RgbBackupValidator measured, so this is the NECESSARY minimum for the staging cap and "
            + "never a sufficient one; covering the expansion is what the shipped default is for");
    }

    [Fact]
    public void TheRestoreRamCapIsReachableFromTheEnvironmentSoAFalseRejectIsRecoverable()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "1073741824" : null);

        Assert.Equal(1_073_741_824, cfg.ToRestoreLimits().RamCapBytes);
    }

    [Fact]
    public void TheRestoreStagingDiskCapIsReachableFromTheEnvironmentSoAFalseRejectIsRecoverable()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_DISK_CAP_BYTES" ? "1073741824" : null);

        Assert.True(cfg.ToRestoreLimits().DiskCapBytes == 1_073_741_824,
            "The staging disk cap is measured on the FULLY DECOMPRESSED wallet directory, while the "
            + "upload bound, RgbBackupValidator and its measured-inflation pass all measure the "
            + "compressed, encrypted outer archive. Those differ by the compression ratio, so this is "
            + "the restore cap most likely to refuse a real funded wallet, and it was the only one an "
            + "operator could not raise without host filesystem access to rgb.json. RGB stock is "
            + "client-side: that archive is the only recovery route for the assets.");
    }

    [Fact]
    public void AnEnvironmentRestoreStagingDiskCapBelowTheFloorIsClampedUpNotIgnored()
    {
        var cfg = new RGBConfiguration { RestoreDiskCapBytes = 3_221_225_472 };
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_DISK_CAP_BYTES" ? "1" : null);

        Assert.Equal(RGBConfiguration.RestoreDiskCapMinBytes, cfg.RestoreDiskCapBytes);
    }

    [Fact]
    public void AnEnvironmentRestoreStagingDiskCapAboveTheCeilingIsClampedDownNotIgnored()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_DISK_CAP_BYTES" ? "99999999999999" : null);

        Assert.Equal(RGBConfiguration.RestoreDiskCapMaxBytes, cfg.RestoreDiskCapBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("12.5")]
    public void ANonPositiveOrUnparseableStagingDiskCapLeavesTheConfiguredValue(string raw)
    {
        var cfg = new RGBConfiguration { RestoreDiskCapBytes = 3_221_225_472 };
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_RESTORE_DISK_CAP_BYTES" ? raw : null);

        Assert.Equal(3_221_225_472, cfg.RestoreDiskCapBytes);
    }

    [Fact]
    public void TheShippedStagingDiskCapCoversTheDecompressedFormOfEveryArchiveTheEarlierGatesAdmit()
    {
        var shipped = new RGBConfiguration().RestoreDiskCapBytes;

        Assert.True(shipped >= Services.RgbBackupValidator.MaxTotalUncompressedBytes * 10,
            $"The shipped staging cap is {shipped / (1024 * 1024)} MB, measured on the wallet directory "
            + "AFTER rgb-lib decompresses it. RgbBackupValidator admits "
            + $"{Services.RgbBackupValidator.MaxTotalUncompressedBytes / (1024 * 1024)} MB of OUTER "
            + "archive content, and that content is the zstd-compressed, encrypted wallet zip, so an "
            + "admitted archive expands by its compression ratio. SQLite and the RGB stock compress "
            + "well past 10:1, so a cap that does not clear that multiple kills restores that passed "
            + "the upload bound, validation, the measured-inflation pass and the scrypt guard — a "
            + "permanent false REJECT of a funded wallet, which is fund loss.");
    }

    [Fact]
    public void AnEnvironmentRestoreRamCapOutsideTheRangeIsClampedNotIgnored()
    {
        var low = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(low, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "1" : null);
        var high = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(high, name =>
            name == "RGB_RESTORE_RAM_CAP_BYTES" ? "99999999999999" : null);

        Assert.Equal(RGBConfiguration.RestoreRamMinBytes, low.RestoreRamCapBytes);
        Assert.Equal(RGBConfiguration.RestoreRamMaxBytes, high.RestoreRamCapBytes);
    }

    [Fact]
    public void NoRestoreDiskCapReadBypassesTheClampThatTheChildWatchdogAlreadyApplies()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(Path.Combine("Services", "RGBWalletService.cs"));

        var unclampedReads = tree.GetRoot()
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.Text == nameof(RGBConfiguration.RestoreDiskCapBytes))
            .Where(access => access.Expression is IdentifierNameSyntax { Identifier.Text: "_cfg" })
            .Select(access => tree.GetLineSpan(access.Span).StartLinePosition.Line + 1)
            .ToList();

        Assert.True(unclampedReads.Count == 0,
            "Services/RGBWalletService.cs reads _cfg.RestoreDiskCapBytes directly at line(s) "
            + string.Join(", ", unclampedReads)
            + ", bypassing the Math.Clamp that ToRestoreLimits() applies. The child watchdog enforces the "
            + "CLAMPED cap, so a direct read makes the post-restore gate refuse at a different number than "
            + "the one actually enforced: restore_disk_cap_bytes below the floor then refuses every restore "
            + "of a funded wallet while quoting a limit no operator can act on. Read it through "
            + "_cfg.ToRestoreLimits().DiskCapBytes instead.");
    }
}
