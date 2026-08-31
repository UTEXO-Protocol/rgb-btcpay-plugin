using System.Diagnostics;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class NativeSendProcessRunnerTests
{
    sealed class FakeChild : IChildHandle
    {
        public bool Exited;
        public bool Reaped = true;
        public long Rss;
        public int Kills;
        public int Disposes;
        public string Output = "ok";
        public bool Truncated;
        public bool StdOutTruncated => Truncated;
        public Exception? OutputError;
        public Action<string>? OnInput;
        public IDisposable? InputLease;
        public bool HasExited => Exited;
        public long WorkingSet64 => Rss;
        public int ExitCode => 0;
        public void Kill(bool entireProcessTree) { Kills++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct) => Task.FromResult(Reaped);
        public Task<string> ReadStdOutAsync() => OutputError == null
            ? Task.FromResult(Output)
            : Task.FromException<string>(OutputError);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task WriteStdinLineAndCloseAsync(string line)
        {
            OnInput?.Invoke(line);
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            InputLease?.Dispose();
            if (!Exited) Kill(true);
            Disposes++;
        }
    }

    static NativeSendLimits Fast() => new(
        TimeSpan.FromMilliseconds(80),
        RamCapBytes: 1_000,
        CpuLimit: TimeSpan.FromSeconds(1),
        Poll: TimeSpan.FromMilliseconds(5),
        ReapGrace: TimeSpan.FromMilliseconds(100));

    static string ExistingHelper() => typeof(NativeSendProcessRunnerTests).Assembly.Location;

    static NativeSendProcessRunner Runner(FakeChild child) => new(
        NullLogger<NativeSendProcessRunner>.Instance,
        (_, _) => child,
        ExistingHelper,
        () => "dotnet");

    static async Task<NativeSendRunResult> Run(NativeSendProcessRunner runner, string operation,
        NativeSendLimits limits, string? leaseWalletDir = null)
    {
        var leaseDir = leaseWalletDir ?? Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);
        var result = await runner.RunAsync(operation, "{}", leaseDir,
            () => true, limits, CancellationToken.None);
        if (result.ChildReaped) lease.ClearActiveMarker(leaseDir);
        return result;
    }

    [Fact]
    public void NativeSendConfigurationClampsTheHardMemoryBudgetAtBothEnds()
    {
        Assert.Equal(RGBConfiguration.NativeSendRamMinBytes,
            new RGBConfiguration { NativeSendRamCapBytes = 1 }.ToNativeSendLimits().RamCapBytes);
        Assert.Equal(RGBConfiguration.NativeSendRamMaxBytes,
            new RGBConfiguration { NativeSendRamCapBytes = long.MaxValue }.ToNativeSendLimits().RamCapBytes);
    }

    [Fact]
    public async Task HungWorker_IsKilledAndConfirmedReapedWithinTheDeadline()
    {
        var child = new FakeChild();
        var result = await Run(Runner(child), "send-begin", Fast());

        Assert.Equal(NativeSendOutcome.TimedOut, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(1, child.Kills);
        Assert.Equal(1, child.Disposes);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UnconfirmedReap_IsNeverReportedAsSafe()
    {
        var child = new FakeChild { Reaped = false };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var result = await Run(Runner(child), "send-end", Fast(), leaseDir);

        Assert.False(result.ChildReaped);
        Assert.Equal(1, child.Kills);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
    }

    [Fact]
    public async Task RamBreach_KillsAndReapsTheWorker()
    {
        var child = new FakeChild { Rss = 2_000 };
        var result = await Run(Runner(child), "send-begin", Fast());

        Assert.Equal(NativeSendOutcome.KilledRam, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task CleanExit_TransfersOnlyTheBoundedResultAfterReaping()
    {
        var child = new FakeChild { Exited = true, Output = "{\"batch_transfer_idx\":7}" };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var result = await Run(Runner(child), "send-begin", Fast(), leaseDir);

        Assert.Equal(NativeSendOutcome.Exited, result.Outcome);
        Assert.True(result.ChildReaped);
        Assert.Equal(child.Output, result.StdOut);
        Assert.Equal(0, child.Kills);
        Assert.False(RgbNativeSendLease.Exists(leaseDir));
    }

    [Fact]
    public async Task QuiescenceFailureBeforeChildLaunchIsTypedAndDoesNotClaimAChildIsUnreaped()
    {
        var child = new FakeChild { Exited = true };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        var sawLease = false;
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var error = await Record.ExceptionAsync(() =>
            Runner(child).RunAsync("send-begin", "{}", leaseDir, () =>
            {
                sawLease = RgbNativeSendLease.Exists(leaseDir);
                return false;
            }, Fast(), CancellationToken.None));

        Assert.True(sawLease);
        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
            error?.GetType().FullName);
        Assert.Equal(0, child.Disposes);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task OperationMarkerSpansBothHelperPhases()
    {
        var child = new FakeChild { Exited = true };
        var runner = Runner(child);
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var begin = await runner.RunAsync("send-begin", "{}", leaseDir,
            () => true, Fast(), CancellationToken.None);
        Assert.True(begin.ChildReaped);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));

        var end = await runner.RunAsync("send-end", "{}", leaseDir,
            () => true, Fast(), CancellationToken.None);
        Assert.True(end.ChildReaped);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task RecoveryReplayHandsTheWorkerLeaseToTheAuthorizedChildAndReclaimsIt()
    {
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-replay-{Guid.NewGuid():N}");
        string staleToken;
        using (var parent = RgbNativeSendLease.AcquireParent(leaseDir))
            staleToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(leaseDir);
        using var recovery = RgbNativeSendLease.AcquireRecovery(leaseDir);
        var replayToken = recovery.PrepareWorkerReplay(leaseDir);
        Assert.NotEqual(staleToken, replayToken);

        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, staleToken));

        var child = new FakeChild { Exited = true };
        child.OnInput = json =>
        {
            using var document = JsonDocument.Parse(json);
            child.InputLease = RgbNativeSendLease.AcquireWorker(
                leaseDir, document.RootElement.GetProperty("LeaseToken").GetString()!);
        };
        var request = JsonSerializer.Serialize(new { LeaseToken = replayToken });
        var result = await Runner(child).RunAsync("send-end", request, leaseDir,
            () => true, Fast(), CancellationToken.None);

        Assert.True(result.ChildReaped);
        recovery.ReclaimWorkerAfterReplay(leaseDir);
        Assert.Throws<IOException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, replayToken));
        recovery.Dispose();
        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(leaseDir, replayToken));
        using var cleanup = RgbNativeSendLease.AcquireRecovery(leaseDir);
        cleanup.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task UnexpectedPostLaunchFailureKillsAndRequiresProvenExit()
    {
        var child = new FakeChild
        {
            Exited = true,
            Reaped = false,
            OutputError = new IOException("stdout failed")
        };
        var runner = Runner(child);
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        await Assert.ThrowsAsync<NativeSendChildUnreapedException>(() =>
            runner.RunAsync("send-end", "{}", leaseDir, () => true,
                Fast(), CancellationToken.None));

        Assert.Equal(1, child.Kills);
        Assert.True(RgbNativeSendLease.Exists(leaseDir));
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task TheConfiguredOutputCapReachesTheChildHandleInsteadOfAHardcodedOne()
    {
        var child = new FakeChild { Exited = true };
        var caps = new List<int>();
        var runner = new NativeSendProcessRunner(
            NullLogger<NativeSendProcessRunner>.Instance,
            (_, cap) => { caps.Add(cap); return child; },
            ExistingHelper,
            () => "dotnet");
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        await runner.RunAsync("send-begin", "{}", leaseDir, () => true,
            Fast() with { OutputCapChars = 4_096 }, CancellationToken.None);

        Assert.Equal(new[] { 4_096 }, caps);
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task AnOutputCapOutsideTheUsableRangeIsClampedRatherThanBreakingEverySend()
    {
        var child = new FakeChild { Exited = true };
        var caps = new List<int>();
        var runner = new NativeSendProcessRunner(
            NullLogger<NativeSendProcessRunner>.Instance,
            (_, cap) => { caps.Add(cap); return child; },
            ExistingHelper,
            () => "dotnet");
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        await runner.RunAsync("send-begin", "{}", leaseDir, () => true,
            Fast() with { OutputCapChars = 0 }, CancellationToken.None);
        await runner.RunAsync("send-begin", "{}", leaseDir, () => true,
            Fast() with { OutputCapChars = int.MaxValue }, CancellationToken.None);

        Assert.Equal(
            new[] { NativeSendProcessRunner.MinOutputCapChars, NativeSendProcessRunner.MaxOutputCapChars },
            caps);
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task ATruncatedHelperResultIsRefusedInsteadOfReturnedAsAValue()
    {
        var child = new FakeChild { Exited = true, Truncated = true, Output = "cHNidP8BA" };
        var runner = Runner(child);
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var error = await Record.ExceptionAsync(() => runner.RunAsync(
            "send-end", "{}", leaseDir, () => true, Fast(), CancellationToken.None));

        Assert.True(error != null,
            "a helper result the parent could not read in full must fail the run: returning the prefix "
            + "makes a truncated PSBT or txid look like a value, so a completed send_end can be "
            + "reported to the merchant as a failure");
        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.NativeSendOutputTruncatedException",
            error!.GetType().FullName);
        Assert.DoesNotContain(child.Output, error.Message);
        lease.ClearActiveMarker(leaseDir);
    }

    [Fact]
    public async Task AnUntruncatedHelperResultIsStillReturned()
    {
        var child = new FakeChild { Exited = true, Truncated = false, Output = "{\"batch_transfer_idx\":9}" };
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");

        var result = await Run(Runner(child), "send-begin", Fast(), leaseDir);

        Assert.Equal(NativeSendOutcome.Exited, result.Outcome);
        Assert.Equal(child.Output, result.StdOut);
    }

    [Fact]
    public async Task TheRealChildHandleReportsWhetherItHadToDropAnyStdout()
    {
        if (OperatingSystem.IsWindows()) return;

        using var capped = NewShellChild("awk 'BEGIN{for(i=0;i<5000;i++)printf \"x\"}'", 1_024);
        var cappedText = await capped.ReadStdOutAsync();
        Assert.Equal(1_024, cappedText.Length);
        Assert.True(capped.StdOutTruncated,
            "dropping stdout without saying so is what let a truncated result reach the caller as a value");

        using var whole = NewShellChild("awk 'BEGIN{for(i=0;i<5000;i++)printf \"x\"}'", 1_048_576);
        var wholeText = await whole.ReadStdOutAsync();
        Assert.Equal(5_000, wholeText.Length);
        Assert.False(whole.StdOutTruncated,
            "a result that fits under the cap must not be reported as truncated, or every send fails");
    }

    [Fact]
    public async Task TheProductionDefaultChildFactoryHonoursTheCapItIsGivenRatherThanALiteral()
    {
        if (OperatingSystem.IsWindows()) return;

        using var child = NativeSendProcessRunner.CreateRealChild(ShellStartInfo(
            "awk 'BEGIN{for(i=0;i<5000;i++)printf \"x\"}'"), 1_024);
        var text = await child.ReadStdOutAsync();

        Assert.Equal(1_024, text.Length);
        Assert.True(child.StdOutTruncated,
            "the default factory is the only path production takes, and every other cap observation "
            + "in this class injects a fake factory: a literal reinstated here leaves the whole suite "
            + "green with OutputCapChars dead again");
    }

    [Fact]
    public async Task ARunnerBuiltTheProductionWayRefusesATruncatedResultEndToEnd()
    {
        if (OperatingSystem.IsWindows()) return;

        var loud = Path.Combine(Path.GetTempPath(), $"rgb-loud-helper-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(loud,
            "#!/bin/sh\nawk 'BEGIN{for(i=0;i<5000;i++)printf \"x\"}'\n");
        File.SetUnixFileMode(loud, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var runner = new NativeSendProcessRunner(
                NullLogger<NativeSendProcessRunner>.Instance,
                handleFactory: null,
                ExistingHelper,
                () => loud);
            var limits = Fast() with
            {
                Timeout = TimeSpan.FromSeconds(30),
                Poll = TimeSpan.FromMilliseconds(20),
                RamCapBytes = 1_000_000_000
            };

            var whole = await Run(runner, "send-begin", limits with { OutputCapChars = 1_048_576 });
            Assert.Equal(NativeSendOutcome.Exited, whole.Outcome);
            Assert.Equal(5_000, whole.StdOut.Length);

            var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-lease-{Guid.NewGuid():N}");
            using var lease = RgbNativeSendLease.AcquireParent(leaseDir);
            var error = await Record.ExceptionAsync(() => runner.RunAsync(
                "send-begin", "{}", leaseDir, () => true,
                limits with { OutputCapChars = 1_024 }, CancellationToken.None));

            Assert.True(error != null,
                "built the way production builds it — no injected factory — the runner must still "
                + "refuse a result it could only read a prefix of");
            Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.NativeSendOutputTruncatedException",
                error!.GetType().FullName);
            lease.ClearActiveMarker(leaseDir);
        }
        finally { try { File.Delete(loud); } catch { } }
    }

    static ProcessStartInfo ShellStartInfo(string script) => new()
    {
        FileName = "/bin/sh",
        ArgumentList = { "-c", script },
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    static RestoreProcessRunner.RealChildHandle NewShellChild(string script, int outputCapChars)
        => new(ShellStartInfo(script), outputCapChars);

    [Fact]
    public async Task RealHungProcess_IsKilledAndReapedWithNoWorkerLeftRunning()
    {
        if (OperatingSystem.IsWindows()) return;
        RestoreProcessRunner.RealChildHandle? real = null;
        var runner = new NativeSendProcessRunner(
            NullLogger<NativeSendProcessRunner>.Instance,
            (_, _) => real = new RestoreProcessRunner.RealChildHandle(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "sleep 30" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }),
            ExistingHelper,
            () => "dotnet");

        var result = await Run(runner, "send-end",
            Fast() with { RamCapBytes = 1_000_000_000 });

        Assert.NotNull(real);
        Assert.Equal(NativeSendOutcome.TimedOut, result.Outcome);
        Assert.True(result.ChildReaped);
    }
}
