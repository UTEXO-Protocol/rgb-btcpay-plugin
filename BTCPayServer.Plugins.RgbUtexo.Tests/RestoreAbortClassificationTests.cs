using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Supervisor stops retain a distinct exception type for accurate operator-facing errors and reap-gated
// cleanup. The process-wide cooldown itself is deliberately outcome-independent and is covered at the
// RGBWalletService boundary in RestoreGateTests.
public class RestoreAbortClassificationTests
{
    sealed class FakeRunner : IRestoreProcessRunner
    {
        public RestoreRunResult? Result;
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
            => Task.FromResult(Result!);
    }

    static (RestoreExecutor exec, FakeRunner runner) Build()
    {
        var runner = new FakeRunner();
        return (new RestoreExecutor(runner, new RGBConfiguration(Path.GetTempPath()),
            NullLogger<RestoreExecutor>.Instance), runner);
    }

    static string Staging()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-abort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "state.dat"), "x");
        return d;
    }

    [Theory]
    [InlineData(RestoreOutcome.TimedOut)]
    [InlineData(RestoreOutcome.KilledDisk)]
    [InlineData(RestoreOutcome.KilledRam)]
    [InlineData(RestoreOutcome.KilledEntries)]
    public async Task EverySupervisorStopIsClassifiedAsAborted(RestoreOutcome outcome)
    {
        var (exec, runner) = Build();
        var dir = Staging();
        runner.Result = new RestoreRunResult(outcome, null, "", ChildReaped: true);

        await Assert.ThrowsAsync<RestoreAbortedException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
    }

    [Fact]
    public async Task ACHEAPChildThatRanAndFailedIsNotAnAbort()
    {
        // The genuine wrong-password case: it must still throw, still be an InvalidOperationException so
        // existing callers behave identically, and NOT arm the cooldown — an operator who mistypes has
        // to be able to retry at once.
        var (exec, runner) = Build();
        var dir = Staging();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 1, "restore_backup failed",
            ChildReaped: true, Elapsed: TimeSpan.FromMilliseconds(190));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));

        Assert.IsNotType<RestoreAbortedException>(ex);
        Assert.Contains("restore_backup failed", ex.Message);
    }

    [Fact]
    public async Task AnUnreapedChildIsStillAnAbortAndKeepsItsStagingDir()
    {
        // Reap-gated cleanup is the pre-existing contract; the abort classification must not have
        // quietly changed which side of it a stopped-but-unreaped child lands on.
        var (exec, runner) = Build();
        var dir = Staging();
        runner.Result = new RestoreRunResult(RestoreOutcome.TimedOut, null, "", ChildReaped: false);
        try
        {
            await Assert.ThrowsAsync<RestoreAbortedException>(
                () => exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None));
            Assert.True(Directory.Exists(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task SuccessRequiresExitedZeroAndReaped()
    {
        var (exec, runner) = Build();
        var dir = Staging();
        runner.Result = new RestoreRunResult(RestoreOutcome.Exited, 0, "", ChildReaped: true);
        try
        {
            await exec.ExecuteAsync("bk", dir, "pw", CancellationToken.None);
            Assert.True(Directory.Exists(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void AbortedExceptionRemainsCatchableAsInvalidOperation()
    {
        // Load-bearing for the blast radius of this change: the controller and several existing tests
        // catch InvalidOperationException, so the new type must be a subtype, not a sibling.
        Assert.IsAssignableFrom<InvalidOperationException>(new RestoreAbortedException("x"));
    }
}
