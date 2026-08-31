using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSendLeaseTests : IDisposable
{
    readonly string _walletDir = Path.Combine(Path.GetTempPath(), $"rgb-native-send-lease-{Guid.NewGuid():N}");

    [Fact]
    public void FailedWorkerPublicationRollsBackTheNewDurableMarker()
    {
        var original = RgbNativeSendLease.WorkerFileHardener;
        var target = Path.GetFullPath(RgbNativeSendLease.WorkerPathFor(_walletDir));
        RgbNativeSendLease.WorkerFileHardener = path =>
        {
            if (Path.GetFullPath(path) == target) throw new IOException("chmod failed");
            original(path);
        };
        try
        {
            Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));
            Assert.False(RgbNativeSendLease.Exists(_walletDir));
        }
        finally { RgbNativeSendLease.ResetWorkerFileHardener(); }
    }

    [Fact]
    public void RefusedAcquireParentLeavesADurableMarkerItDidNotCreateOnDisk()
    {
        using (RgbNativeSendLease.AcquireParent(_walletDir)) { }
        Assert.True(RgbNativeSendLease.Exists(_walletDir),
            "the seeded state under test is a durable helper marker left on disk with no live handle");

        Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));
        Assert.True(RgbNativeSendLease.Exists(_walletDir),
            "a refused AcquireParent must not unlink the durable helper marker another owner published: "
            + "that marker is the only quarantine gate on the SendBtc path, so consuming it turns the "
            + "operator's next retry into a send that signs and broadcasts over an unresolved wallet");
    }

    [Fact]
    public void RetryingARefusedAcquireParentIsRefusedAgainInsteadOfUnlockingTheQuarantine()
    {
        using (RgbNativeSendLease.AcquireParent(_walletDir)) { }

        Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));
        Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));
        Assert.True(RgbNativeSendLease.Exists(_walletDir),
            "an ordinary operator retry must not discharge a durable quarantine — only reconciliation "
            + "(AcquireRecovery plus ClearActiveMarker) may remove the marker");
    }

    [Fact]
    public void RecoveryStillDischargesAMarkerThatAcquireParentNowRefusesToConsume()
    {
        using (RgbNativeSendLease.AcquireParent(_walletDir)) { }
        Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));

        using (var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir))
            recovery.ClearActiveMarker(_walletDir);

        Assert.False(RgbNativeSendLease.Exists(_walletDir),
            "the refusal must be recoverable without shell access, or a funded wallet is stranded");
        using var reclaimed = RgbNativeSendLease.AcquireParent(_walletDir);
        reclaimed.ClearActiveMarker(_walletDir);
    }

    [Fact]
    public void WorkerAuthorizationFileIsPrivateOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var parent = RgbNativeSendLease.AcquireParent(_walletDir);
        var mode = File.GetUnixFileMode(RgbNativeSendLease.WorkerPathFor(_walletDir));
        var publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        Assert.Equal(0, (int)(mode & publicBits));
        parent.ClearActiveMarker(_walletDir);
    }

    [Fact]
    public void RecoveryCannotEnterWhileParentOrWorkerOwnsTheWallet()
    {
        using var parent = RgbNativeSendLease.AcquireParent(_walletDir);
        using var worker = RgbNativeSendLease.AcquireWorker(
            _walletDir, RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir));

        Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireRecovery(_walletDir));
    }

    [Fact]
    public void RecoveryThatWinsBeforeWorkerStartMakesWorkerFailClosed()
    {
        string workerToken;
        using (var parent = RgbNativeSendLease.AcquireParent(_walletDir))
            workerToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        using (var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir))
            Assert.Throws<IOException>(() =>
                RgbNativeSendLease.AcquireWorker(_walletDir, workerToken));

        RgbNativeSendLease.Delete(_walletDir);
        Assert.Throws<FileNotFoundException>(() =>
            RgbNativeSendLease.AcquireWorker(_walletDir, workerToken));
    }

    [Fact]
    public void JournalOnlyRecoveryPublishesAndOwnsTheCrossProcessMarker()
    {
        using (var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir))
        {
            Assert.True(File.Exists(RgbNativeSendLease.ParentPathFor(_walletDir)));
            Assert.True(RgbNativeSendLease.Exists(_walletDir));
            Assert.True(RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir));
            Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireParent(_walletDir));
            recovery.ClearActiveMarker(_walletDir);
        }

        Assert.False(RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir));
        RgbNativeSendLease.Delete(_walletDir);
        Assert.False(RgbNativeSendLease.Exists(_walletDir));
    }

    [Fact]
    public void HelperClaimsWorkerLeaseBeforeConstructingNativeWallet()
    {
        string workerToken;
        using (RgbNativeSendLease.AcquireParent(_walletDir))
            workerToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        using var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir);
        var request = JsonSerializer.Serialize(new
        {
            DataDir = _walletDir,
            BitcoinNetwork = "testnet",
            ElectrumUrl = "tcp://127.0.0.1:1",
            XpubVanilla = "not-an-xpub",
            XpubColored = "not-an-xpub",
            MasterFingerprint = "00000000",
            LeaseWalletDir = _walletDir,
            LeaseToken = workerToken,
            MaxAllocationsPerUtxo = 1,
            RecipientMapJson = "{}",
            FeeRate = 1f,
            MinConfirmations = 1,
            SignedPsbt = (string?)null
        });

        Assert.Throws<IOException>(() => RgbNativeSend.Invoke("send-begin", request));
    }

    [Fact]
    public async Task ParentLeasePublicationWaitsForWalletConstructionGate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var construction = Task.Run(() => RgbNativeSendLease.WithProcessGate(_walletDir, () =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        }));
        await entered.Task;

        var acquire = Task.Run(() => RgbNativeSendLease.AcquireParent(_walletDir));
        try
        {
            await Task.Delay(50);
            Assert.False(acquire.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True(await construction);
        using var parent = await acquire;
        Assert.True(RgbNativeSendLease.Exists(_walletDir));
    }

    [Fact]
    public void RecoveryCannotEnterWhileParentRemovesTheActiveMarker()
    {
        using (var parent = RgbNativeSendLease.AcquireParent(_walletDir))
        {
            RgbNativeSendLease.Delete(_walletDir);
            Assert.Throws<IOException>(() => RgbNativeSendLease.AcquireRecovery(_walletDir));
        }

        using var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir);
        Assert.True(RgbNativeSendLease.Exists(_walletDir));
        recovery.ClearActiveMarker(_walletDir);
    }

    [Fact]
    public async Task ParentPublicationWaitsForAnOrdinaryNativeCall()
    {
        var access = RgbNativeSendLease.AcquireWalletAccess(_walletDir);
        Task<RgbNativeSendLease> acquire;
        using (ExecutionContext.SuppressFlow())
            acquire = Task.Run(() => RgbNativeSendLease.AcquireParent(_walletDir));
        try
        {
            await Task.Delay(50);
            Assert.False(acquire.IsCompleted);
        }
        finally { access.Dispose(); }

        using var parent = await acquire;
        Assert.True(RgbNativeSendLease.Exists(_walletDir));
        parent.ClearActiveMarker(_walletDir);
    }

    [Fact]
    public async Task OrdinaryNativeCallsSerializeInsteadOfThrowingIoExceptions()
    {
        var first = RgbNativeSendLease.AcquireWalletAccess(_walletDir);
        Task<IDisposable> second;
        using (ExecutionContext.SuppressFlow())
            second = Task.Run(() => RgbNativeSendLease.AcquireWalletAccess(_walletDir));
        try
        {
            await Task.Delay(50);
            Assert.False(second.IsCompleted);
        }
        finally { first.Dispose(); }

        using var acquired = await second;
    }

    [Fact]
    public async Task WalletConstructionGateTimesOutWithTypedQuarantine()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => RgbNativeSendLease.WithProcessGate(_walletDir, () =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        }));
        await entered.Task;

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var error = Record.Exception(() =>
                RgbNativeSendLease.WithProcessGate(
                    _walletDir, () => true, TimeSpan.FromMilliseconds(50)));
            Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
                error?.GetType().FullName);
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
        }
        finally { release.Set(); }
        Assert.True(await holder);
    }

    [Fact]
    public async Task WalletConstructionGateDoesNotCoupleOldStripeCollisions()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var first = Path.Combine(_walletDir, "first");
        var bucket = (int)((uint)comparer.GetHashCode(Path.GetFullPath(first)) % 64);
        var suffix = 0;
        string second;
        do
        {
            second = Path.Combine(_walletDir, $"other-{suffix++}");
        } while ((int)((uint)comparer.GetHashCode(Path.GetFullPath(second)) % 64) != bucket);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => RgbNativeSendLease.WithProcessGate(first, () =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        }));
        await entered.Task;

        try
        {
            Assert.True(RgbNativeSendLease.WithProcessGate(
                second, () => true, TimeSpan.FromMilliseconds(50)));
        }
        finally { release.Set(); }
        Assert.True(await holder);
    }

    [Fact]
    public void NativeAccessGateDoesNotCoupleOldStripeCollisions()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var first = Path.Combine(_walletDir, "access-first");
        var bucket = (int)((uint)comparer.GetHashCode(Path.GetFullPath(first)) % 64);
        var suffix = 0;
        string second;
        do
        {
            second = Path.Combine(_walletDir, $"access-other-{suffix++}");
        } while ((int)((uint)comparer.GetHashCode(Path.GetFullPath(second)) % 64) != bucket);

        using var held = RgbNativeSendLease.AcquireWalletAccess(first);
        using var independent = RgbNativeSendLease.AcquireWalletAccess(
            second, wait: TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void NativeAccessGateTimeoutIsBoundedAndTyped()
    {
        using var held = RgbNativeSendLease.AcquireWalletAccess(_walletDir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() => RgbNativeSendLease.AcquireWalletAccess(
            _walletDir, wait: TimeSpan.FromMilliseconds(50)));

        Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
            error?.GetType().FullName);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task OwnershipFlowsAcrossAwaitsButNotIntoAnUnrelatedExecutionContext()
    {
        using (var parent = RgbNativeSendLease.AcquireParent(_walletDir))
        {
            Assert.True(RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir));
            await Task.Yield();
            Assert.True(RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir));

            Task<(bool Owned, Exception? AccessError)> unrelated;
            using (ExecutionContext.SuppressFlow())
                unrelated = Task.Run(() =>
                {
                    var owned = RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir);
                    Exception? error = Record.Exception(() =>
                    {
                        using var access = RgbNativeSendLease.AcquireWalletAccess(_walletDir);
                    });
                    return (Owned: owned, AccessError: (Exception?)error);
                });
            var foreignResult = await unrelated;
            Assert.False(foreignResult.Owned);
            Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
                foreignResult.AccessError?.GetType().FullName);
            parent.ClearActiveMarker(_walletDir);
        }

        Assert.False(RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir));
    }

    [Fact]
    public async Task CapturedOwnershipExpiresWhenTheLeaseIsDisposed()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> inherited;
        using (var parent = RgbNativeSendLease.AcquireParent(_walletDir))
        {
            inherited = Task.Run(async () =>
            {
                await release.Task;
                return RgbNativeSendLease.IsOwnedByCurrentContext(_walletDir);
            });
            parent.ClearActiveMarker(_walletDir);
        }

        release.SetResult();
        Assert.False(await inherited);
    }

    public void Dispose()
    {
        RgbNativeSendLease.ResetWorkerFileHardener();
        try { if (Directory.Exists(_walletDir)) Directory.Delete(_walletDir, true); } catch { }
    }
}
