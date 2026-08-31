using System.Diagnostics;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSendStderrHostPathRedactionTests
{
    const string SyntheticFingerprint = "00000000";

    const string WalletDataDir =
        "/Users/someone/.btcpayserver/Main/rgb-wallets/11111111-2222-3333-4444-555555555555";

    const string LeaseWalletDir = WalletDataDir + "/" + SyntheticFingerprint;

    const string HelperDll =
        "/Users/someone/.btcpayserver/Plugins/BTCPayServer.Plugins.RgbUtexo/RgbRestoreHelper.dll";

    const string SendFallback = "Failed to send asset. Check server logs for details.";

    static string Redact(string childStdErr) =>
        RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
            childStdErr, WalletDataDir, LeaseWalletDir);

    static string RedactWithTheHelperThePluginExecd(string childStdErr) =>
        RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
            childStdErr, WalletDataDir, LeaseWalletDir, HelperDll);

    static void AssertNamesNoHostLocation(string shown)
    {
        Assert.DoesNotContain("/Users/", shown);
        Assert.DoesNotContain(".btcpayserver", shown);
        Assert.DoesNotContain("rgb-wallets", shown);
        Assert.DoesNotContain(LeaseWalletDir, shown);
        Assert.DoesNotContain(WalletDataDir, shown);
    }

    [Fact]
    public void WorkerLeaseFileNotFound_LosesTheWalletDirectoryButKeepsTheDiagnosis()
    {
        var shown = Redact(
            $"Could not find file '{LeaseWalletDir}/{RgbNativeSendLease.WorkerFileName}'.");

        AssertNamesNoHostLocation(shown);
        Assert.Equal(
            $"Could not find file '{RgbHelperStderrRedaction.WalletKeyedDataDirectoryPlaceholder}"
            + $"/{RgbNativeSendLease.WorkerFileName}'.",
            shown);
    }

    [Fact]
    public void UnauthorizedAccessOnTheWorkerLease_LosesTheWalletDirectoryButKeepsTheDiagnosis()
    {
        var shown = Redact(
            $"Access to the path '{LeaseWalletDir}/{RgbNativeSendLease.WorkerFileName}' is denied.");

        AssertNamesNoHostLocation(shown);
        Assert.Contains("is denied.", shown);
        Assert.Contains(RgbHelperStderrRedaction.WalletKeyedDataDirectoryPlaceholder, shown);
    }

    [Fact]
    public void RgbLibWalletConstructionNamingItsDataDir_LosesTheWalletDataDirectory()
    {
        var shown = Redact($"I/O error: Permission denied (os error 13): {WalletDataDir}/rgb_lib_db");

        AssertNamesNoHostLocation(shown);
        Assert.Contains("Permission denied (os error 13)", shown);
        Assert.Equal(
            "I/O error: Permission denied (os error 13): "
            + RgbHelperStderrRedaction.WalletDataDirectoryPlaceholder + "/rgb_lib_db",
            shown);
    }

    [Fact]
    public void WalletDataRootNamedWithoutTheWalletLeaf_LosesItsPrefix()
    {
        var shown = Redact(
            "I/O error: No space left on device: /Users/someone/.btcpayserver/Main/rgb-wallets");

        AssertNamesNoHostLocation(shown);
        Assert.Contains("No space left on device", shown);
        Assert.Contains(RgbHelperStderrRedaction.WalletDataRootPlaceholder, shown);
    }

    [Fact]
    public void DllNotFoundEnumeratingItsProbedPaths_LosesThePluginInstallDirectory()
    {
        var installDir = Path.GetDirectoryName(HelperDll)!;

        var shown = RedactWithTheHelperThePluginExecd(
            "Unable to load shared library 'rgbverifycffi' or one of its dependencies: tried: "
            + $"'{installDir}/librgbverifycffi.dylib' (no such file)");

        AssertNamesNoHostLocation(shown);
        Assert.DoesNotContain(installDir, shown);
        Assert.Contains("Unable to load shared library 'rgbverifycffi'", shown);
        Assert.Contains(
            RgbHelperStderrRedaction.PluginInstallDirectoryPlaceholder + "/librgbverifycffi.dylib",
            shown);
    }

    [Fact]
    public void DotnetHostBootstrapFailureNamingTheSendHelperAssembly_LosesThatPathButKeepsTheDiagnosis()
    {
        var shown = RedactWithTheHelperThePluginExecd($"The application '{HelperDll}' does not exist.");

        AssertNamesNoHostLocation(shown);
        Assert.Equal(
            $"The application '{RgbHelperStderrRedaction.NativeSendHelperAssemblyPlaceholder}' does not exist.",
            shown);
    }

    public static TheoryData<string> ActionableSendRefusalsRgbLibEmitsWithNoPathAtAll() => new()
    {
        "Insufficient spendable assets",
        "Invalid invoice: expired",
        "Invalid recipient ID: recipient ID already used",
        "Failed broadcast: non-final",
        "Internal error: no uncolorable UTXOs are available",
        "Cannot change online status"
    };

    [Theory]
    [MemberData(nameof(ActionableSendRefusalsRgbLibEmitsWithNoPathAtAll))]
    public void PathFreeChildDiagnostic_ReachesTheOperatorWordForWord_BecauseItIsHisOnlySelfServiceDiagnosis(
        string childStdErr)
    {
        Assert.Equal(childStdErr, Redact(childStdErr));
    }

    [Theory]
    [InlineData("rel/wallet", "I/O error: Permission denied: rel/wallet/rgb_lib_db")]
    [InlineData("wd", "Internal error: wdt watchdog tripped")]
    public void ARelativeWalletDirIsNeverSubstituted_BecauseItNamesNoHostLocationToHide(
        string relativeWalletDataDir, string childStdErr)
    {
        var shown =
            RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
                childStdErr, relativeWalletDataDir,
                Path.Combine(relativeWalletDataDir, SyntheticFingerprint));

        Assert.Equal(childStdErr, shown);
    }

    [Fact]
    public void AFilesystemRootIsNeverSubstituted_SoASeparatorCannotBeReplacedEverywhere()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var shown =
            RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
                "I/O error: No space left on device", root, root, root);

        Assert.Equal("I/O error: No space left on device", shown);
    }

    sealed class FakeNativeSendRunner : INativeSendProcessRunner
    {
        public string StdErr = "";
        public int ExitCode = 1;
        public string? HelperDllItExecd;

        public Task<NativeSendRunResult> RunAsync(string operation, string requestJson,
            string leaseWalletDir, Func<bool> quiesceParent, NativeSendLimits limits,
            CancellationToken ct)
            => Task.FromResult(new NativeSendRunResult(NativeSendOutcome.Exited, ExitCode, "",
                StdErr, ChildReaped: true, TimeSpan.Zero, HelperDllItExecd));
    }

    sealed class CapturingLogger : ILogger<RGBWalletService>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    sealed record FailingSendOutcome(
        string ShownToTheStoreOwner,
        List<string> LogMessages,
        string WalletDataDirTheHelperWasGiven,
        string LeaseWalletDirTheHelperWasGiven);

    static async Task<FailingSendOutcome> RunFailingNativeSend(
        Func<string, string, string> childStdErrNamingTheDirsThePluginHandedTheHelper,
        string? helperDll = null, int exitCode = 1)
    {
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-send-redaction-{Guid.NewGuid():N}"));
        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = "store",
            Network = "regtest",
            MasterFingerprint = SyntheticFingerprint,
            XpubVanilla = "v",
            XpubColored = "c"
        };
        var walletDataDir = cfg.GetWalletDataDir(wallet.Id, wallet.Network);
        var leaseWalletDir = Path.Combine(walletDataDir, wallet.MasterFingerprint);
        var log = new CapturingLogger();
        var svc = new RGBWalletService(new FakeRgbLib(cfg), null!, cfg, null!, null!, null!, null!,
            log, null!,
            new FakeNativeSendRunner
            {
                StdErr = childStdErrNamingTheDirsThePluginHandedTheHelper(walletDataDir, leaseWalletDir),
                ExitCode = exitCode,
                HelperDllItExecd = helperDll
            });
        try
        {
            using var lease = RgbNativeSendLease.AcquireParent(leaseWalletDir);
            var ex = await Assert.ThrowsAsync<NativeSendReapedFailureException>(
                () => svc.RunNativeSendIsolatedAsync(wallet, "send-begin", "{}", 1, 1, null,
                    CancellationToken.None));
            lease.ClearActiveMarker(leaseWalletDir);
            return new FailingSendOutcome(
                RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(ex, SendFallback),
                log.Messages, walletDataDir, leaseWalletDir);
        }
        finally
        {
            try { if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task WalletDataDirBearingHelperStderr_ReachesTheStoreOwnerWithoutTheHostPath_AndStaysInTheServerLog()
    {
        var outcome = await RunFailingNativeSend(
            (walletDataDir, _) =>
                $"I/O error: Permission denied (os error 13): {walletDataDir}{Path.DirectorySeparatorChar}rgb_lib_db");

        Assert.DoesNotContain(outcome.WalletDataDirTheHelperWasGiven, outcome.ShownToTheStoreOwner);
        Assert.Contains("Permission denied (os error 13)", outcome.ShownToTheStoreOwner);
        Assert.Contains(RgbHelperStderrRedaction.WalletDataDirectoryPlaceholder,
            outcome.ShownToTheStoreOwner);
        Assert.Contains(outcome.LogMessages,
            m => m.Contains(outcome.WalletDataDirTheHelperWasGiven, StringComparison.Ordinal)
                && m.Contains("rgb_lib_db", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LeasePathBearingHelperStderr_ReachesTheStoreOwnerWithoutTheHostPath_AndStaysInTheServerLog()
    {
        var outcome = await RunFailingNativeSend(
            (_, leaseWalletDir) =>
                $"Could not find file '{leaseWalletDir}{Path.DirectorySeparatorChar}"
                + $"{RgbNativeSendLease.WorkerFileName}'.");

        Assert.DoesNotContain(outcome.LeaseWalletDirTheHelperWasGiven, outcome.ShownToTheStoreOwner);
        Assert.DoesNotContain(outcome.WalletDataDirTheHelperWasGiven, outcome.ShownToTheStoreOwner);
        Assert.Equal(
            $"Could not find file '{RgbHelperStderrRedaction.WalletKeyedDataDirectoryPlaceholder}"
            + $"{Path.DirectorySeparatorChar}{RgbNativeSendLease.WorkerFileName}'.",
            outcome.ShownToTheStoreOwner);
        Assert.Contains(outcome.LogMessages,
            m => m.Contains(outcome.LeaseWalletDirTheHelperWasGiven, StringComparison.Ordinal)
                && m.Contains(RgbNativeSendLease.WorkerFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostBootstrapStderr_ReachesTheStoreOwnerWithoutThePluginInstallPath_AndStaysInTheServerLog()
    {
        var installDir = Path.Combine(Path.GetTempPath(), $"rgb-send-plugins-{Guid.NewGuid():N}");
        var helperDll = Path.Combine(installDir, "RgbRestoreHelper.dll");

        var outcome = await RunFailingNativeSend(
            (_, _) => $"A fatal error occurred. The required library {installDir}"
                + $"{Path.DirectorySeparatorChar}RgbRestoreHelper.runtimeconfig.json was not found.",
            helperDll);

        Assert.DoesNotContain(installDir, outcome.ShownToTheStoreOwner);
        Assert.DoesNotContain(helperDll, outcome.ShownToTheStoreOwner);
        Assert.Contains("A fatal error occurred.", outcome.ShownToTheStoreOwner);
        Assert.Contains(RgbHelperStderrRedaction.PluginInstallDirectoryPlaceholder,
            outcome.ShownToTheStoreOwner);
        Assert.Contains(outcome.LogMessages, m => m.Contains(installDir, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActionableSendRefusalWithNoPath_StillReachesTheStoreOwnerWordForWord_WithoutShellAccess()
    {
        var outcome = await RunFailingNativeSend((_, _) => "Insufficient spendable assets");

        Assert.Equal("Insufficient spendable assets", outcome.ShownToTheStoreOwner);
        Assert.NotEqual(SendFallback, outcome.ShownToTheStoreOwner);
    }

    [Fact]
    public async Task EmptyHelperStderr_NamesTheExitStatusAndAKnob_AndStillReachesTheServerLog()
    {
        var outcome = await RunFailingNativeSend((_, _) => "   ", exitCode: 139);

        Assert.Contains("exit status 139", outcome.ShownToTheStoreOwner);
        Assert.True(!outcome.ShownToTheStoreOwner.Contains("signal", StringComparison.Ordinal),
            $"the refusal read \"{outcome.ShownToTheStoreOwner}\". A status in 128-255 is not proof of "
            + "a signal: the .NET host's own failure codes land there once masked to eight bits, so a "
            + "broken installation is reported to the operator as a signal death");
        Assert.Contains("RGB_NATIVE_SEND_RAM_CAP_BYTES", outcome.ShownToTheStoreOwner);
        Assert.DoesNotContain(outcome.WalletDataDirTheHelperWasGiven, outcome.ShownToTheStoreOwner);
        Assert.True(
            outcome.LogMessages.Any(m => m.Contains("139", StringComparison.Ordinal)
                && m.Contains("send-begin", StringComparison.Ordinal)),
            "a helper killed by the OOM killer, by an external signal or by its own CPU rlimit writes "
            + "nothing to stderr, and this log call used to be gated on stderr being non-blank. That "
            + "left the operator holding a bare exit code whose only stated remedy is the server log, "
            + "while the server log held nothing at all about the attempt. The log messages were: "
            + string.Join(" | ", outcome.LogMessages));
    }

    sealed class FakeChildThatExitsNonZero : IChildHandle
    {
        public bool HasExited => true;
        public long WorkingSet64 => 0;
        public int ExitCode => 1;
        public bool StdOutTruncated => false;
        public void Kill(bool entireProcessTree) { }
        public Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct) => Task.FromResult(true);
        public Task<string> ReadStdOutAsync() => Task.FromResult("");
        public Task<string> ReadStdErrAsync() => Task.FromResult("boom");
        public Task WriteStdinLineAndCloseAsync(string line) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public async Task TheRunnerReportsTheHelperItHandedTheDotnetHost_SoTheThrowSiteCanRedactThatPath()
    {
        var helperDll = typeof(RgbNativeSendStderrHostPathRedactionTests).Assembly.Location;
        var runner = new NativeSendProcessRunner(
            NullLogger<NativeSendProcessRunner>.Instance,
            (ProcessStartInfo _, int _) => new FakeChildThatExitsNonZero(),
            () => helperDll,
            () => "dotnet");
        var leaseDir = Path.Combine(Path.GetTempPath(), $"rgb-send-helperdll-{Guid.NewGuid():N}");
        using var lease = RgbNativeSendLease.AcquireParent(leaseDir);

        var result = await runner.RunAsync("send-begin", "{}", leaseDir, () => true,
            new NativeSendLimits(TimeSpan.FromSeconds(5), 1_000_000_000,
                TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);
        lease.ClearActiveMarker(leaseDir);

        Assert.Equal(helperDll, result.HelperDllHandedToTheDotnetHost);
    }

    [Fact]
    public async Task AccountKeyMaterialInHelperStderr_IsRedactedFromTheServerLogWhileTheHostPathSurvives()
    {
        var syntheticAccountKey = "tpub" + new string('D', 20) + new string('k', 20) + new string('7', 25);
        var outcome = await RunFailingNativeSend(
            (walletDataDir, _) =>
                $"BDK descriptor mismatch: expected {syntheticAccountKey} at {walletDataDir}");

        Assert.DoesNotContain(syntheticAccountKey,
            string.Join("\n", outcome.LogMessages));
        Assert.Contains(outcome.LogMessages,
            m => m.Contains(RgbNativeMessageSanitizer.RedactionPlaceholder, StringComparison.Ordinal));
        Assert.Contains(outcome.LogMessages,
            m => m.Contains(outcome.WalletDataDirTheHelperWasGiven, StringComparison.Ordinal));
    }
}
