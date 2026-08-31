using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBoundRefusalsNameTheKnobThatRaisesThemTests
{
    const string SyntheticFingerprint = "00000000";

    sealed class FakeRestoreRunner : IRestoreProcessRunner
    {
        public RestoreRunResult Result = new(RestoreOutcome.TimedOut, null, "", ChildReaped: true);

        public Task<RestoreRunResult> RunAsync(string backupPath, string stagingDir, string password,
            RestoreLimits limits, CancellationToken ct) => Task.FromResult(Result);
    }

    sealed class FakeNativeSendRunner : INativeSendProcessRunner
    {
        public NativeSendOutcome Outcome = NativeSendOutcome.KilledRam;
        public int? ExitCode = 1;
        public string StdErr = "";

        public Task<NativeSendRunResult> RunAsync(string operation, string requestJson,
            string leaseWalletDir, Func<bool> quiesceParent, NativeSendLimits limits,
            CancellationToken ct)
            => Task.FromResult(new NativeSendRunResult(Outcome, ExitCode, "", StdErr,
                ChildReaped: true, TimeSpan.Zero, null));
    }

    sealed class CapturingLogger : ILogger<RGBWalletService>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    static string StagingDirHoldingOneFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rgb-bound-refusal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "state.dat"), "x");
        return dir;
    }

    static async Task<string> RefusalTheStoreOwnerSeesForARestoreStoppedBy(RestoreOutcome outcome)
    {
        var executor = new RestoreExecutor(
            new FakeRestoreRunner { Result = new RestoreRunResult(outcome, null, "", ChildReaped: true) },
            new RGBConfiguration(Path.GetTempPath()),
            NullLogger<RestoreExecutor>.Instance);
        var stagingDir = StagingDirHoldingOneFile();
        try
        {
            var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => executor.ExecuteAsync("backup", stagingDir, "pw", CancellationToken.None));
            return RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                refused, RgbOperatorFacingFailure.EscalateToServerLogs);
        }
        finally { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
    }

    static async Task<string> RefusalTheStoreOwnerSeesForARestoreChildThatEnded(
        int? exitCode, string stdErr)
    {
        var executor = new RestoreExecutor(
            new FakeRestoreRunner
            {
                Result = new RestoreRunResult(RestoreOutcome.Exited, exitCode, stdErr, ChildReaped: true)
            },
            new RGBConfiguration(Path.GetTempPath()),
            NullLogger<RestoreExecutor>.Instance);
        var stagingDir = StagingDirHoldingOneFile();
        try
        {
            var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => executor.ExecuteAsync("backup", stagingDir, "pw", CancellationToken.None));
            return RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                refused, RgbOperatorFacingFailure.EscalateToServerLogs);
        }
        finally { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
    }

    const string TheDiagnosticARustAllocationFailurePrints =
        "memory allocation of 268435456 bytes failed";

    sealed record SendRefusal(string ShownToTheStoreOwner, List<string> LogMessages);

    static async Task<SendRefusal> RefusalTheStoreOwnerSeesForASendStoppedBy(
        NativeSendOutcome outcome, int? exitCode = 1, string stdErr = "")
    {
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-bound-refusal-send-{Guid.NewGuid():N}"));
        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = "store",
            Network = "regtest",
            MasterFingerprint = SyntheticFingerprint,
            XpubVanilla = "v",
            XpubColored = "c"
        };
        var leaseWalletDir = Path.Combine(
            cfg.GetWalletDataDir(wallet.Id, wallet.Network), wallet.MasterFingerprint);
        var log = new CapturingLogger();
        var svc = new RGBWalletService(new FakeRgbLib(cfg), null!, cfg, null!, null!, null!, null!,
            log, null!,
            new FakeNativeSendRunner { Outcome = outcome, ExitCode = exitCode, StdErr = stdErr });
        try
        {
            using var lease = RgbNativeSendLease.AcquireParent(leaseWalletDir);
            var refused = await Assert.ThrowsAsync<NativeSendReapedFailureException>(
                () => svc.RunNativeSendIsolatedAsync(wallet, "send-begin", "{}", 1, 1, null,
                    CancellationToken.None));
            lease.ClearActiveMarker(leaseWalletDir);
            return new SendRefusal(
                RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                    refused, RgbOperatorFacingFailure.EscalateToServerLogs),
                log.Messages);
        }
        finally
        {
            try { if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, true); }
            catch (IOException) { }
        }
    }

    static IEnumerable<string> EnvironmentVariablesNamedIn(string refusal) =>
        Regex.Matches(refusal, "RGB_[A-Z0-9_]+").Select(m => m.Value).Distinct();

    [Theory]
    [InlineData(RestoreOutcome.TimedOut)]
    [InlineData(RestoreOutcome.KilledDisk)]
    [InlineData(RestoreOutcome.KilledRam)]
    [InlineData(RestoreOutcome.KilledEntries)]
    public async Task EveryBoundThatCanStopARestoreNamesAKnobAndANextAction(RestoreOutcome outcome)
    {
        var refusal = await RefusalTheStoreOwnerSeesForARestoreStoppedBy(outcome);

        Assert.True(EnvironmentVariablesNamedIn(refusal).Any(),
            $"the refusal for {outcome} read \"{refusal}\" and names no environment variable. A store "
            + "Owner has no host shell to edit rgb.json with, so a bound whose refusal names only a "
            + "number is a PERMANENT false REJECT of a funded wallet's only recovery route — the "
            + "knob may exist and still be unreachable, which is how RGB_RESTORE_MAX_STAGING_ENTRIES "
            + "and RGB_RESTORE_RAM_CAP_BYTES shipped");
        Assert.Contains("restarting BTCPay", refusal);
        Assert.Contains("retry the restore", refusal);
        Assert.Contains("backup file is undamaged", refusal);
    }

    [Theory]
    [InlineData(RestoreOutcome.TimedOut)]
    [InlineData(RestoreOutcome.KilledDisk)]
    [InlineData(RestoreOutcome.KilledRam)]
    [InlineData(RestoreOutcome.KilledEntries)]
    public async Task EveryRestoreBoundReportsTheQuantityThatWasActuallyEnforced(RestoreOutcome outcome)
    {
        var limits = new RGBConfiguration(Path.GetTempPath()).ToRestoreLimits();
        var expected = outcome switch
        {
            RestoreOutcome.TimedOut => $"{(int)limits.Timeout.TotalSeconds} seconds",
            RestoreOutcome.KilledDisk => $"{limits.DiskCapBytes / (1024 * 1024)} MB",
            RestoreOutcome.KilledRam => $"{limits.RamCapBytes / (1024 * 1024)} MB",
            _ => $"{limits.MaxStagingEntries} staging entry"
        };

        var refusal = await RefusalTheStoreOwnerSeesForARestoreStoppedBy(outcome);

        Assert.Contains(expected, refusal);
    }

    [Fact]
    public async Task TheStagingEntryRefusalSaysTheCountIncludesDirectories_BecauseTheWatchdogCountsThem()
    {
        var refusal = await RefusalTheStoreOwnerSeesForARestoreStoppedBy(RestoreOutcome.KilledEntries);

        Assert.Contains("RGB_RESTORE_MAX_STAGING_ENTRIES", refusal);
        Assert.True(refusal.Contains("directories as well as files", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\". RestoreProcessRunner.MeasureStaging walks "
            + "EnumerateFileSystemInfos and increments its counter before the FileInfo test, so the "
            + "number quoted here counts directories too. Calling it a count of files sends the "
            + "operator to compare it against a file count that can be a small fraction of it");
    }

    [Fact]
    public async Task TheRestoreMemoryRefusalNamesTheKnobAndItsCeiling_NotJustThatAMemoryLimitExisted()
    {
        var refusal = await RefusalTheStoreOwnerSeesForARestoreStoppedBy(RestoreOutcome.KilledRam);

        Assert.Contains("RGB_RESTORE_RAM_CAP_BYTES", refusal);
        Assert.Contains($"maximum {RGBConfiguration.RestoreRamMaxBytes / (1024 * 1024)} MB", refusal);
    }

    [Fact]
    public async Task TheNativeSendMemoryRefusalNamesTheKnobAndItsCeiling()
    {
        var refusal = await RefusalTheStoreOwnerSeesForASendStoppedBy(NativeSendOutcome.KilledRam);

        Assert.Contains("RGB_NATIVE_SEND_RAM_CAP_BYTES", refusal.ShownToTheStoreOwner);
        Assert.Contains($"maximum {RGBConfiguration.NativeSendRamMaxBytes / (1024 * 1024)} MB",
            refusal.ShownToTheStoreOwner);
        Assert.Contains(
            $"{new RGBConfiguration().ToNativeSendLimits().RamCapBytes / (1024 * 1024)} MB native memory",
            refusal.ShownToTheStoreOwner);
        Assert.Contains("retry the send", refusal.ShownToTheStoreOwner);
    }

    [Fact]
    public async Task TheNativeSendDeadlineRefusalNamesTheKnobThatRaisesIt()
    {
        var refusal = await RefusalTheStoreOwnerSeesForASendStoppedBy(NativeSendOutcome.TimedOut);

        Assert.Contains("RGB_NATIVE_SEND_TIMEOUT_SECONDS", refusal.ShownToTheStoreOwner);
        Assert.Contains(
            $"{(int)new RGBConfiguration().ToNativeSendLimits().Timeout.TotalSeconds} second",
            refusal.ShownToTheStoreOwner);
    }

    [Theory]
    [InlineData(134)]
    [InlineData(152)]
    public async Task ARestoreChildThatDiedAfterPrintingAnAllocationFailureStillNamesTheMemoryKnob(
        int exitCode)
    {
        var refusal = await RefusalTheStoreOwnerSeesForARestoreChildThatEnded(
            exitCode, TheDiagnosticARustAllocationFailurePrints);

        Assert.Contains(TheDiagnosticARustAllocationFailurePrints, refusal);
        Assert.True(refusal.Contains("RGB_RESTORE_RAM_CAP_BYTES", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\" and names no memory knob. RgbRestoreHelper applies the "
            + "restore memory budget to ITSELF as an RLIMIT_AS before it opens the backup, so the "
            + "budget refuses the allocation inside the child and the child dies there. The parent "
            + "samples RSS, which never rose above the cap, so the outcome is Exited and not KilledRam "
            + "— the whole knob-bearing memory refusal is bypassed on the path most likely to be taken. "
            + "A store Owner with no host shell is left with an allocation number and no way to raise "
            + "the limit that produced it, which is a PERMANENT false REJECT of a funded wallet's only "
            + "recovery route");
        Assert.Contains($"maximum {RGBConfiguration.RestoreRamMaxBytes / (1024 * 1024)} MB", refusal);
    }

    [Fact]
    public async Task ARestoreChildThatReportedItsOwnFaultKeepsThatTextAlone_WithNoMemoryAdviceAttached()
    {
        var refusal = await RefusalTheStoreOwnerSeesForARestoreChildThatEnded(
            1, "The provided password is incorrect");

        Assert.Equal("Restore failed: The provided password is incorrect", refusal);
    }

    [Fact]
    public async Task ASendChildThatDiedAfterPrintingAnAllocationFailureStillNamesTheMemoryKnob()
    {
        var refusal = await RefusalTheStoreOwnerSeesForASendStoppedBy(
            NativeSendOutcome.Exited, exitCode: 134,
            stdErr: TheDiagnosticARustAllocationFailurePrints);

        Assert.Contains(TheDiagnosticARustAllocationFailurePrints, refusal.ShownToTheStoreOwner);
        Assert.True(
            refusal.ShownToTheStoreOwner.Contains("RGB_NATIVE_SEND_RAM_CAP_BYTES", StringComparison.Ordinal),
            $"the refusal read \"{refusal.ShownToTheStoreOwner}\" and names no memory knob. The send "
            + "helper applies the native send memory budget to ITSELF as an RLIMIT_AS before it builds "
            + "the rgb-lib wallet, so that budget refuses the allocation inside the child and the child "
            + "dies there while the parent's RSS sampling never fires. The outcome is Exited, not "
            + "KilledRam, so a wallet stopped this way every time can never move its assets and the "
            + "operator is never told which limit to raise");
        Assert.Contains($"maximum {RGBConfiguration.NativeSendRamMaxBytes / (1024 * 1024)} MB",
            refusal.ShownToTheStoreOwner);
    }

    [Fact]
    public async Task ASendHelperThatStoppedWithNoStderrIsDescribedRatherThanLeftAsABareExitCode()
    {
        var refusal = await RefusalTheStoreOwnerSeesForASendStoppedBy(
            NativeSendOutcome.Exited, exitCode: 137, stdErr: "   ");

        Assert.Contains("exit status 137", refusal.ShownToTheStoreOwner);
        Assert.True(!refusal.ShownToTheStoreOwner.Contains("signal", StringComparison.Ordinal),
            $"the refusal read \"{refusal.ShownToTheStoreOwner}\". The 128-to-255 range it read that "
            + "off is also where the .NET host's own failure codes land once the OS masks them to "
            + "eight bits, so a broken BTCPay installation is reported as a signal death");
        Assert.Contains("RGB_NATIVE_SEND_RAM_CAP_BYTES", refusal.ShownToTheStoreOwner);
        Assert.True(refusal.LogMessages.Any(m => m.Contains("137", StringComparison.Ordinal)),
            "the OOM killer, an external SIGKILL and the helper's own CPU rlimit all leave stderr "
            + "empty. The refusal points the operator at the server log, so the server log has to "
            + "record the attempt for precisely those kills. It recorded: "
            + string.Join(" | ", refusal.LogMessages));
    }

    [Fact]
    public async Task AnUnreadSendExitStatusIsDescribedRatherThanRenderedAsAnEmptyGap()
    {
        var refusal = await RefusalTheStoreOwnerSeesForASendStoppedBy(
            NativeSendOutcome.Exited, exitCode: null, stdErr: "");

        Assert.Contains("could not read", refusal.ShownToTheStoreOwner);
        Assert.DoesNotContain("signal", refusal.ShownToTheStoreOwner);
    }

    [Fact]
    public async Task NoBoundRefusalNamesAnEnvironmentVariableApplyEnvironmentOverridesDoesNotRead()
    {
        var refusals = new List<string>();
        foreach (var outcome in new[]
                 {
                     RestoreOutcome.TimedOut, RestoreOutcome.KilledDisk, RestoreOutcome.KilledRam,
                     RestoreOutcome.KilledEntries
                 })
            refusals.Add(await RefusalTheStoreOwnerSeesForARestoreStoppedBy(outcome));
        refusals.Add((await RefusalTheStoreOwnerSeesForASendStoppedBy(NativeSendOutcome.KilledRam))
            .ShownToTheStoreOwner);
        refusals.Add((await RefusalTheStoreOwnerSeesForASendStoppedBy(NativeSendOutcome.TimedOut))
            .ShownToTheStoreOwner);
        refusals.Add((await RefusalTheStoreOwnerSeesForASendStoppedBy(
            NativeSendOutcome.Exited, exitCode: 137, stdErr: "")).ShownToTheStoreOwner);

        var pluginSource = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "RGBPlugin.cs"));

        foreach (var name in refusals.SelectMany(EnvironmentVariablesNamedIn).Distinct())
            Assert.True(pluginSource.Contains($"read(\"{name}\")", StringComparison.Ordinal),
                $"a refusal an operator can reach tells them to set {name}, but "
                + "RGBPlugin.ApplyEnvironmentOverrides never reads it, so setting it does nothing and "
                + "the only remaining route is editing rgb.json on the host filesystem. That is the "
                + "dead end this class of finding is about: the knob and the message have to be the "
                + "same knob");
    }

    [Fact]
    public void TheNativeSendMemoryBudgetIsReachableWithoutEditingRgbJson()
    {
        var raised = new RGBConfiguration(Path.GetTempPath());
        RGBPlugin.ApplyEnvironmentOverrides(raised, name =>
            name == "RGB_NATIVE_SEND_RAM_CAP_BYTES" ? "1073741824" : null);

        Assert.Equal(1_073_741_824L, raised.ToNativeSendLimits().RamCapBytes);
    }

    [Theory]
    [InlineData("1", RGBConfiguration.NativeSendRamMinBytes)]
    [InlineData("999999999999", RGBConfiguration.NativeSendRamMaxBytes)]
    public void TheNativeSendMemoryBudgetIsClampedLikeItsTimeoutAndCpuSiblings(
        string raw, long expected)
    {
        var cfg = new RGBConfiguration(Path.GetTempPath());
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_NATIVE_SEND_RAM_CAP_BYTES" ? raw : null);

        Assert.Equal(expected, cfg.ToNativeSendLimits().RamCapBytes);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0")]
    [InlineData("-1")]
    public void AnUnusableNativeSendMemoryValueLeavesTheConfiguredBudgetInPlace(string raw)
    {
        var cfg = new RGBConfiguration(Path.GetTempPath()) { NativeSendRamCapBytes = 1_073_741_824 };
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == "RGB_NATIVE_SEND_RAM_CAP_BYTES" ? raw : null);

        Assert.Equal(1_073_741_824L, cfg.ToNativeSendLimits().RamCapBytes);
    }

    [Fact]
    public void TheNativeSendMemoryBudgetIsOnlyEverReadThroughTheClampingAccessor()
    {
        var readSites = Directory
            .EnumerateFiles(PluginCompilation.RepoRootPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}submodules{Path.DirectorySeparatorChar}")
                && !f.Contains(
                    $"{Path.DirectorySeparatorChar}BTCPayServer.Plugins.RgbUtexo.Tests{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("NativeSendRamCapBytes", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(readSites.SequenceEqual(new[] { "RGBConfiguration.cs", "RGBPlugin.cs" }),
            "NativeSendRamCapBytes is a raw configuration field whose only safe reader is "
            + "ToNativeSendLimits, which clamps it; RGBPlugin writes it through the same clamp. A "
            + $"third file naming it is a read that bypasses both. Files naming it: {string.Join(", ", readSites)}");
    }
}
