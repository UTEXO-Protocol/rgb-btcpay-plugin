using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class RgbRuntimeLockReclamationTests : IDisposable
{
    readonly string _walletDir = Path.Combine(
        Path.GetTempPath(), $"rgb-runtime-lock-reclamation-{Guid.NewGuid():N}");

    [Fact]
    public void ConstructionReclaimsRuntimeLockWithoutWorkerMarker()
    {
        Directory.CreateDirectory(_walletDir);
        var runtimeLock = RgbNativeSendLease.RgbRuntimeLockPathFor(_walletDir);
        File.WriteAllBytes(runtimeLock, []);

        using (RgbNativeSendLease.AcquireWalletConstructionAccess(_walletDir)) { }

        Assert.False(File.Exists(runtimeLock),
            "construction must reclaim a stale rgb-lib runtime lock after obtaining exclusive wallet access");
    }

    [Fact]
    public void ConstructionRotatesDelayedWorkerAuthorizationBeforeReclamation()
    {
        using var parent = RgbNativeSendLease.AcquireParent(_walletDir);
        var delayedWorkerToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        var runtimeLock = RgbNativeSendLease.RgbRuntimeLockPathFor(_walletDir);
        File.WriteAllBytes(runtimeLock, []);

        using (RgbNativeSendLease.AcquireWalletConstructionAccess(_walletDir)) { }

        var replacementToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        Assert.False(File.Exists(runtimeLock),
            "construction must reclaim a stale runtime lock while its parent lease owns the wallet");
        Assert.NotEqual(delayedWorkerToken, replacementToken);
        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(_walletDir, delayedWorkerToken));
        using (RgbNativeSendLease.AcquireWorker(_walletDir, replacementToken)) { }
        parent.ClearActiveMarker(_walletDir);
    }

    [Fact]
    public void RecoveryHoldingWorkerLeaseCanReclaimRuntimeLock()
    {
        string delayedWorkerToken;
        using (var parent = RgbNativeSendLease.AcquireParent(_walletDir))
            delayedWorkerToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        var recovery = RgbNativeSendLease.AcquireRecovery(_walletDir);
        var runtimeLock = RgbNativeSendLease.RgbRuntimeLockPathFor(_walletDir);
        File.WriteAllBytes(runtimeLock, []);

        using (RgbNativeSendLease.AcquireWalletConstructionAccess(_walletDir)) { }

        Assert.False(File.Exists(runtimeLock),
            "recovery must reclaim a stale runtime lock while it exclusively holds the worker lease");
        recovery.Dispose();
        Assert.Throws<InvalidDataException>(() =>
            RgbNativeSendLease.AcquireWorker(_walletDir, delayedWorkerToken));
        RgbNativeSendLease.Delete(_walletDir);
    }

    [Fact]
    public async Task ActiveWorkerNativeAccessPreventsReclamation()
    {
        using var parent = RgbNativeSendLease.AcquireParent(_walletDir);
        var workerToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(_walletDir);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task workerTask;
        using (ExecutionContext.SuppressFlow())
            workerTask = Task.Run(async () =>
            {
                using var worker = RgbNativeSendLease.AcquireWorker(_walletDir, workerToken);
                using var access = RgbNativeSendLease.AcquireWalletAccess(
                    _walletDir, allowMarked: true);
                entered.SetResult();
                await release.Task;
            });

        await entered.Task;
        var runtimeLock = RgbNativeSendLease.RgbRuntimeLockPathFor(_walletDir);
        File.WriteAllBytes(runtimeLock, []);
        try
        {
            var error = Record.Exception(() =>
            {
                using var access = RgbNativeSendLease.AcquireWalletConstructionAccess(
                    _walletDir, TimeSpan.FromMilliseconds(50));
            });
            Assert.Equal("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
                error?.GetType().FullName);
            Assert.True(File.Exists(runtimeLock),
                "construction must not reclaim while a worker still holds native wallet access");
        }
        finally
        {
            release.SetResult();
            await workerTask;
            parent.ClearActiveMarker(_walletDir);
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_walletDir)) Directory.Delete(_walletDir, true); } catch { }
    }
}
