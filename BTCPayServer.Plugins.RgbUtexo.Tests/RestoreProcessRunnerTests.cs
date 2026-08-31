using System.Diagnostics;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreProcessRunnerTests
{
    sealed class FakeChild : IChildHandle
    {
        public long Rss;
        public bool Exited;
        public int Code;
        public int KillCount;
        public int DisposeCount;
        public bool ReapWithinGrace = true;
        public bool ThrowOnStdin;
        public TimeSpan StdinDelay;

        public long WorkingSet64 => Rss;
        public bool HasExited => Exited;
        public int ExitCode => Code;
        public void Kill(bool entireProcessTree) { KillCount++; Exited = true; }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
            => Task.FromResult(ReapWithinGrace);
        public Task<string> ReadStdErrAsync() => Task.FromResult("");
        public Task<string> ReadStdOutAsync() => Task.FromResult("");
        public async Task WriteStdinLineAndCloseAsync(string line)
        {
            if (ThrowOnStdin) throw new IOException("broken pipe");
            if (StdinDelay > TimeSpan.Zero) await Task.Delay(StdinDelay);
        }
        // Mirrors RealChildHandle: disposing a still-running child kills it, so an exception
        // escaping the using block can never leak a live restore.
        public void Dispose() { if (!Exited) Kill(true); DisposeCount++; }
    }

    static RestoreLimits Fast(long diskCap = 1000) => new(
        Timeout: TimeSpan.FromMilliseconds(200),
        DiskCapBytes: diskCap,
        RamCapBytes: 1000,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(10),
        ReapGrace: TimeSpan.FromMilliseconds(50));

    static string ExistingHelper() => typeof(RestoreProcessRunnerTests).Assembly.Location;

    static RestoreProcessRunner NewRunner(FakeChild child)
        => new(NullLogger<RestoreProcessRunner>.Instance, _ => child, ExistingHelper, () => "dotnet");

    // ROUND 3: the poll loop awaits a full interval before re-checking, so a child that inflated fast
    // and exited inside that window was never measured at all — the disk and entry caps were tripwires
    // an attacker could step over between samples. A self-exit now gets one final measurement.
    [Fact]
    public async Task AChildThatExitsAfterBreachingTheDiskCapIsStillReportedAsKilled()
    {
        var dir = CreateTempDir();
        File.WriteAllBytes(Path.Combine(dir, "big.dat"), new byte[4000]);
        // Already exited on entry, so the loop body never runs and only the post-exit check can see it.
        var child = new FakeChild { Exited = true, Code = 0 };

        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 1000), CancellationToken.None);

        Assert.Equal(RestoreOutcome.KilledDisk, r.Outcome);
    }

    [Fact]
    public async Task TheRunnerReportsTheHelperItHandedTheHost_SoTheRedactorNeverRecomputesThatPath()
    {
        var dir = CreateTempDir();
        var child = new FakeChild { Exited = true, Code = 1 };

        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 52_428_800), CancellationToken.None);

        Assert.Equal(RestoreOutcome.Exited, r.Outcome);
        Assert.Equal(ExistingHelper(), r.HelperDllHandedToTheDotnetHost);
    }

    [Fact]
    public async Task AKilledRunAlsoReportsTheHelperItHandedTheHost_SoNoOutcomeLosesTheRedaction()
    {
        var dir = CreateTempDir();
        var child = new FakeChild { Exited = false, Rss = 1 };

        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 52_428_800), CancellationToken.None);

        Assert.Equal(RestoreOutcome.TimedOut, r.Outcome);
        Assert.Equal(ExistingHelper(), r.HelperDllHandedToTheDotnetHost);
    }

    [Fact]
    public async Task AChildThatExitsAfterBreachingTheEntryCapIsStillReportedAsKilled()
    {
        var dir = CreateTempDir();
        for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(dir, $"f{i}.dat"), Array.Empty<byte>());
        var child = new FakeChild { Exited = true, Code = 0 };
        var limits = Fast(diskCap: 52_428_800) with { MaxStagingEntries = 10 };

        var r = await NewRunner(child).RunAsync("bk", dir, "pw", limits, CancellationToken.None);

        Assert.Equal(RestoreOutcome.KilledEntries, r.Outcome);
    }

    [Fact]
    public async Task AnHonestSelfExitWithinTheCapsStillSucceeds()
    {
        // The other side of the same check: adding a post-exit measurement must not turn a normal
        // successful restore into a failure, which would be a permanent false-REJECT.
        var dir = CreateTempDir();
        File.WriteAllBytes(Path.Combine(dir, "small.dat"), new byte[10]);
        var child = new FakeChild { Exited = true, Code = 0 };

        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 1000), CancellationToken.None);

        Assert.Equal(RestoreOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public async Task RamBreach_KillsOnce_ReportsKilledRam()
    {
        var child = new FakeChild { Rss = 5000 };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.KilledRam, r.Outcome);
        Assert.True(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
        Assert.Equal(1, child.DisposeCount);
    }

    [Fact]
    public async Task DiskBreach_KillsOnce_ReportsKilledDisk()
    {
        var dir = CreateTempDir();
        File.WriteAllText(Path.Combine(dir, "big.dat"), new string('x', 5000));
        var child = new FakeChild { Rss = 10 };
        var r = await NewRunner(child).RunAsync("bk", dir, "pw", Fast(diskCap: 10), CancellationToken.None);
        Assert.Equal(RestoreOutcome.KilledDisk, r.Outcome);
        Assert.True(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
    }

    [Fact]
    public async Task Timeout_KillsOnce_ReapUnconfirmed_ReportsChildReapedFalse()
    {
        var child = new FakeChild { Rss = 10, ReapWithinGrace = false };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.TimedOut, r.Outcome);
        Assert.False(r.ChildReaped);
        Assert.Equal(1, child.KillCount);
    }

    [Fact]
    public async Task CleanExit_ReportsExitedWithCodeAndReaped()
    {
        var child = new FakeChild { Rss = 10, Exited = true, Code = 0 };
        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);
        Assert.Equal(RestoreOutcome.Exited, r.Outcome);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.ChildReaped);
        Assert.Equal(0, child.KillCount);
    }

    [Fact]
    public async Task ElapsedIncludesProcessStartupAndPasswordDelivery()
    {
        // The helper starts native work as soon as its ReadLine sees the password. Starting the clock
        // only after this await allowed that work, including a quick self-exit, to disappear from both
        // the expensive-attempt classification and the timeout budget.
        var child = new FakeChild { Exited = true, Code = 1, StdinDelay = TimeSpan.FromMilliseconds(80) };

        var r = await NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None);

        Assert.True(r.Elapsed >= TimeSpan.FromMilliseconds(60), $"elapsed was only {r.Elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task MissingHelper_Throws_DoesNotSpawn()
    {
        var child = new FakeChild { Rss = 10, Exited = true };
        var runner = new RestoreProcessRunner(NullLogger<RestoreProcessRunner>.Instance,
            _ => child, resolveHelperDll: () => "/no/such/RgbRestoreHelper.dll", resolveDotnetHost: () => "dotnet");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None));
        Assert.Equal(0, child.DisposeCount);
    }

    [Fact]
    public async Task StdinWriteThrows_KillsChild_DoesNotLeak()
    {
        var child = new FakeChild { Rss = 10, ThrowOnStdin = true };
        await Assert.ThrowsAsync<IOException>(
            () => NewRunner(child).RunAsync("bk", CreateTempDir(), "pw", Fast(), CancellationToken.None));
        Assert.Equal(1, child.KillCount);      // killed on dispose — no live child leaks
        Assert.Equal(1, child.DisposeCount);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("dotnet")]
    [InlineData("/opt/dotnet/dotnet.exe")]
    public void ResolveDotnetHost_UsesProcessPathWhenItIsTheMuxer(string host)
        => Assert.Equal(host, RestoreProcessRunner.ResolveDotnetHost(
            host, runtimeDir: null, dotnetRoot: null, fileExists: _ => false, isWindows: false));

    [Fact]
    public void ResolveDotnetHost_DerivesMuxerFromRuntimeDir_WhenHostIsApphost()
    {
        // Apphost (dotnet run): ProcessPath is BTCPayServer, so derive the muxer from the shared
        // framework dir <root>/shared/Microsoft.NETCore.App/<ver>/ -> <root>/dotnet.
        var runtimeDir = "/opt/dn/shared/Microsoft.NETCore.App/10.0.5/";
        var expected = Path.GetFullPath("/opt/dn/dotnet");
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: runtimeDir, dotnetRoot: null,
            fileExists: p => p == expected, isWindows: false);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveDotnetHost_FallsBackToDotnetRoot()
    {
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: "/nope/shared/x/1.0/",
            dotnetRoot: "/opt/dn", fileExists: p => p == Path.Combine("/opt/dn", "dotnet"), isWindows: false);
        Assert.Equal(Path.Combine("/opt/dn", "dotnet"), resolved);
    }

    [Fact]
    public void ResolveDotnetHost_Windows_UsesDotnetExe()
    {
        var runtimeDir = @"C:\dn\shared\Microsoft.NETCore.App\10.0.5\";
        var expected = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", "dotnet.exe"));
        var resolved = RestoreProcessRunner.ResolveDotnetHost(
            processPath: @"C:\btcpay\BTCPayServer.exe", runtimeDir: runtimeDir, dotnetRoot: null,
            fileExists: p => p == expected, isWindows: true);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveDotnetHost_FailsClosed_WhenMuxerNotFound()
        => Assert.Throws<InvalidOperationException>(() => RestoreProcessRunner.ResolveDotnetHost(
            processPath: "/srv/btcpay/BTCPayServer", runtimeDir: "/nope/shared/x/1.0/",
            dotnetRoot: null, fileExists: _ => false, isWindows: false));

    // The parent-side half of the restore child's self-containment: the child refuses to run without
    // these three, so if the launch stops carrying them every restore stops working — and if the child
    // ever stops enforcing them, an orphan is unbounded again. Bound to the configured RestoreLimits,
    // not to literals, so a launch that hardcoded a budget would fail here.
    [Fact]
    public async Task TheChildIsLaunchedWithTheConfiguredRestoreTimeoutRamAndCpuBudgets()
    {
        ProcessStartInfo? launched = null;
        var child = new FakeChild { Exited = true, Code = 0 };
        var limits = new RestoreLimits(
            Timeout: TimeSpan.FromSeconds(120),
            DiskCapBytes: 52_428_800,
            RamCapBytes: 700_000_000,
            CpuLimit: TimeSpan.FromSeconds(90),
            Poll: TimeSpan.FromMilliseconds(10),
            ReapGrace: TimeSpan.FromMilliseconds(50),
            MaxStagingEntries: 20_000);
        var runner = new RestoreProcessRunner(
            NullLogger<RestoreProcessRunner>.Instance,
            psi => { launched = psi; return child; },
            ExistingHelper,
            () => "dotnet");

        await runner.RunAsync("bk", CreateTempDir(), "pw", limits, CancellationToken.None);

        Assert.NotNull(launched);
        var args = launched!.ArgumentList.ToList();
        Assert.True(args.Count >= 5,
            $"the restore launch must carry the child's own containment budgets, got: {string.Join(" ", args)}");
        Assert.Equal("120000", args[^3]);
        Assert.Equal("700000000", args[^2]);
        Assert.Equal("90", args[^1]);
    }

    static string CreateTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"rgb-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }
}
