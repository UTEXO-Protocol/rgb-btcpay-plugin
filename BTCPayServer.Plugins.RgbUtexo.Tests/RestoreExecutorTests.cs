using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreExecutorTests
{
    sealed class FakeRunner : IRestoreProcessRunner
    {
        public RestoreRunResult? Result;
        public Exception? Throw;
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
            => Throw != null ? Task.FromException<RestoreRunResult>(Throw) : Task.FromResult(Result!);
    }

    static (RestoreExecutor exec, FakeRunner runner) Build()
    {
        var runner = new FakeRunner();
        var exec = new RestoreExecutor(runner, new RGBConfiguration(Path.GetTempPath()),
            NullLogger<RestoreExecutor>.Instance);
        return (exec, runner);
    }

    static string StagingWithFile()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-exec-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "state.dat"), "x");
        return d;
    }

    [Fact]
    public async Task Timeout_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: true);
        // RestoreAbortedException, not InvalidOperationException: a supervisor stop is what arms the
        // post-kill cooldown, and xUnit's ThrowsAsync matches the EXACT type, so asserting the base
        // type here would silently stop distinguishing an abort from an ordinary failure.
        await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Timeout_ReapNotConfirmed_Throws_LeavesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<RestoreAbortedException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task KilledDisk_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.KilledDisk, null, "", ChildReaped: true);
        await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task TheStagingDiskCapRefusalNamesTheLimitAndTheVariableThatRaisesIt()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.KilledDisk, null, "", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.True(ex.Message.Contains("RGB_RESTORE_DISK_CAP_BYTES", StringComparison.Ordinal),
            "This cap bounds the wallet directory AFTER decompression, while every gate that admitted "
            + "the archive bounds the compressed, encrypted outer content, so a backup all of them "
            + "passed can still be killed here. A store Owner has no host shell to edit rgb.json with, "
            + "so a refusal that does not name an environment variable is a PERMANENT false REJECT of a "
            + "funded wallet's only recovery route.");
    }

    [Fact]
    public async Task TheStagingDiskCapRefusalReportsTheEnforcedLimitNotAnUnclampedConfigurationValue()
    {
        var runner = new FakeRunner
        {
            Result = new RestoreRunResult(RestoreOutcome.KilledDisk, null, "", ChildReaped: true)
        };
        var exec = new RestoreExecutor(
            runner,
            new RGBConfiguration(Path.GetTempPath()) { RestoreDiskCapBytes = 0 },
            NullLogger<RestoreExecutor>.Instance);
        var dir = StagingWithFile();

        var ex = await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.True(
            ex.Message.Contains(
                $"{RGBConfiguration.RestoreDiskCapMinBytes / (1024 * 1024)} MB", StringComparison.Ordinal),
            $"restore_disk_cap_bytes of 0 is clamped up to the floor before the watchdog ever sees it, "
            + $"so the refusal must quote that floor. It said: {ex.Message}");
        Assert.DoesNotContain("0MB", ex.Message);
    }

    [Fact]
    public async Task TheRestoreTimeoutRefusalNamesTheVariableThatRaisesIt()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.True(ex.Message.Contains("RGB_RESTORE_TIMEOUT_SECONDS", StringComparison.Ordinal),
            "Raising the staging disk cap moves a large wallet's failure from the disk kill to this "
            + "one, so this refusal has to be as actionable without host shell access as that one is.");
    }

    [Fact]
    public async Task KilledRam_ReapConfirmed_Throws_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.KilledRam, null, "", ChildReaped: true);
        var ex = await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.Contains("memory limit", ex.Message);
        Assert.DoesNotContain("timed out", ex.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task NonZeroExit_Throws_WithStderr_DeletesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 1, "native boom", ChildReaped: true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
        Assert.Contains("native boom", ex.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task NonZeroExit_WithStderr_KeepsTheHelperTextAsTheWholeMessage_ByteForByte()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 1, "native boom", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.Equal("Restore failed: native boom", ex.Message);
    }

    [Fact]
    public async Task AnExitTheHelperNeverReturns_LeavesEmptyStderr_AndIsNotAttributedToASignal()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 137, "", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.True(ex.Message != "Restore failed: ",
            "A child killed by a signal (the OOM killer, a container memory limit, or the CPU rlimit "
            + "RestoreProcessRunner applies) writes nothing to stderr, so the redacted stderr is empty and "
            + "this was the entire operator-facing refusal. A store Owner has no host shell, so a message "
            + "with no cause, no exit status and no pointer to the server log is a permanent false REJECT "
            + "of a funded wallet's backup.");
        Assert.Contains("137", ex.Message);
        Assert.True(!ex.Message.Contains("signal", StringComparison.Ordinal),
            $"the refusal read \"{ex.Message}\" and tells the operator the helper was killed by a "
            + "signal. The 128-to-255 range it reads that off is also where the .NET host's own failure "
            + "codes land once the OS masks them to eight bits — FrameworkMissingFailure 0x80008093 "
            + "arrives as 147, InvalidConfigFile 0x80008092 as 146, CoreHostLibMissingFailure "
            + "0x80008083 as 131 — so a BTCPay installation missing its runtime is described as a "
            + "signal death and handed memory and CPU advice that cannot fix it");
        Assert.Contains("RGB_RESTORE_CPU_LIMIT_SECONDS", ex.Message);
        Assert.Contains("server log", ex.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData(147)]
    [InlineData(146)]
    [InlineData(131)]
    [InlineData(130)]
    public async Task ADotnetHostFailureCodeIsNotReportedAsASignalDeath(int hostFailureExitCode)
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, hostFailureExitCode, "", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.Contains($"{hostFailureExitCode}", ex.Message);
        Assert.True(!ex.Message.Contains("signal", StringComparison.Ordinal),
            $"exit status {hostFailureExitCode} was described as a signal death. The documented .NET "
            + "host failure codes are masked to eight bits by the OS and land inside 128-255: "
            + "FrameworkMissingFailure 0x80008093 as 147, InvalidConfigFile 0x80008092 as 146, "
            + "CoreHostLibMissingFailure 0x80008083 as 131, CoreHostLibLoadFailure 0x80008082 as 130. "
            + "A BTCPay installation whose runtime or configuration is broken therefore reaches an "
            + "operator as \"killed by signal 19\", which points them at host memory pressure instead "
            + $"of at the deployment. The refusal read: {ex.Message}");
        Assert.True(
            ex.Message.Contains("not a status this plugin's own helper ever returns", StringComparison.Ordinal),
            $"the refusal read \"{ex.Message}\". RgbRestoreHelper returns 0 to 4 and the send helper 0 "
            + "to 2, so anything else came from something other than the helper's own return path and "
            + "cannot be attributed from the status. Saying so is the only description that is true for "
            + "every member of that open set");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task AnExitStatusTheHelperItselfReturns_StillCarriesItsOwnStderrRatherThanBeingUnattributed()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 4, "rlimit refused", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.Equal("Restore failed: rlimit refused", ex.Message);
    }

    [Fact]
    public async Task NativeErrorTextThatWasEmpty_LeavesWhitespaceOnlyStderr_AndStillGetsTheSameRefusal()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 5, "\n   \r\n", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.True(ex.Message.Contains("exit status 5", StringComparison.Ordinal),
            "RgbRestoreHelper writes the native error text with WriteLine, so a non-zero native return "
            + "carrying an empty error string leaves stderr holding only a line break. Whitespace is as "
            + "unactionable as emptiness and must reach the same fallback, not be pasted in as the refusal.");
        Assert.DoesNotContain("signal", ex.Message);
        Assert.Contains("RGB_RESTORE_CPU_LIMIT_SECONDS", ex.Message);
    }

    [Fact]
    public async Task UnreadExitStatus_WithNoStderr_IsDescribedRatherThanLeftAsAnEmptyGap()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, null, "", ChildReaped: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.Contains("could not read", ex.Message);
        Assert.Contains("server log", ex.Message);
        Assert.DoesNotContain("signal", ex.Message);
    }

    [Fact]
    public async Task RefusalForAStderrlessExit_NamesNoHostPath_BecauseAStoreOwnerSeesItVerbatim()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 137, "", ChildReaped: true,
            HelperDllHandedToTheDotnetHost: "/Users/someone/plugins/RgbRestoreHelper.dll");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.DoesNotContain("/Users/", ex.Message);
        Assert.DoesNotContain(dir, ex.Message);
        Assert.DoesNotContain(".btcpayserver", ex.Message);
    }

    [Fact]
    public async Task Success_ReturnsWithoutThrow_LeavesStagingDirForCaller()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: true);
        try
        {
            await exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None);
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ExitZeroButNotReaped_TreatedAsFailure_Throws_LeavesStagingDir()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task SpawnFailure_PropagatesThrow()
    {
        var (exec, runner) = Build();
        var dir = StagingWithFile();
        runner.Throw = new InvalidOperationException("could not launch helper");
        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.Contains("could not launch helper", ex.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
