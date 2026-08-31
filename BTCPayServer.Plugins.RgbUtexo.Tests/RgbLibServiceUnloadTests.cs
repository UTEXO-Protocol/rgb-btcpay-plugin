using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbLibServiceUnloadTests
{
    sealed class TestHandle : RgbLibWalletHandle
    {
        public TestHandle(TimeSpan disposeTimeout, string? walletDir = null)
            : base("test-wallet", disposeTimeout, walletDir) { }

        int _nativeDisposeCount;
        public bool NativeDisposeCalled => Volatile.Read(ref _nativeDisposeCount) > 0;

        protected override void DisposeNativeWallet() => Interlocked.Increment(ref _nativeDisposeCount);
    }

    static Lazy<RgbLibWalletHandle> Created(out TestHandle handle)
    {
        var h = new TestHandle(TimeSpan.FromMilliseconds(200));
        handle = h;
        var lazy = new Lazy<RgbLibWalletHandle>(() => h);
        _ = lazy.Value;
        return lazy;
    }

    static bool SpinUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void Unload_DoesNotBlock_WhenConstructionInFlight()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        using var factoryEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var lazy = new Lazy<RgbLibWalletHandle>(() =>
        {
            factoryEntered.Set();
            release.Wait();
            return new TestHandle(TimeSpan.FromMilliseconds(200));
        });
        wallets["w"] = lazy;

        var construction = Task.Run(() => { _ = lazy.Value; });
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));

        var sw = Stopwatch.StartNew();
        Assert.False(RgbLibService.UnloadFromCache(wallets, "w", null));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, "UnloadWallet blocked on in-flight construction");
        Assert.True(wallets.ContainsKey("w"), "in-flight entry must stay cached to avoid a second native wallet on the same data dir");

        release.Set();
        construction.Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Unload_BackgroundDisposesAndEvicts_WhenConstructionCompletesLater()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        using var factoryEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        TestHandle? built = null;

        var lazy = new Lazy<RgbLibWalletHandle>(() =>
        {
            factoryEntered.Set();
            release.Wait();
            built = new TestHandle(TimeSpan.FromMilliseconds(200));
            return built;
        });
        wallets["w"] = lazy;

        var construction = Task.Run(() => { _ = lazy.Value; });
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert.False(RgbLibService.UnloadFromCache(wallets, "w", null));

        release.Set();
        construction.Wait(TimeSpan.FromSeconds(2));

        Assert.True(SpinUntil(() => !wallets.ContainsKey("w")), "construction completed but handle was orphaned (never evicted)");
        Assert.NotNull(built);
        Assert.True(SpinUntil(() => built!.NativeDisposeCalled), "construction completed but native wallet was never disposed");
    }

    [Fact]
    public void Unload_DisposesAndRemoves_CreatedHandle()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        wallets["w"] = Created(out var handle);

        Assert.True(RgbLibService.UnloadFromCache(wallets, "w", null));

        Assert.True(handle.IsDisposed);
        Assert.True(handle.NativeDisposeCalled);
        Assert.True(handle.NativeWalletFreed);
        Assert.False(wallets.ContainsKey("w"));
    }

    [Fact]
    public void Unload_RemovesFaultedLazy()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        var lazy = new Lazy<RgbLibWalletHandle>(() => throw new InvalidOperationException("boom"));
        wallets["w"] = lazy;
        try { _ = lazy.Value; } catch (InvalidOperationException) { }

        Assert.False(RgbLibService.UnloadFromCache(wallets, "w", null));

        Assert.True(SpinUntil(() => !wallets.ContainsKey("w")));
    }

    [Fact]
    public async Task Unload_DeferredEvicts_WhenDisposeTimesOutThenOperationCompletes()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        wallets["w"] = Created(out var handle);

        var opStarted = new TaskCompletionSource();
        using var gate = new ManualResetEventSlim(false);
        var opTask = Task.Run(() => handle.ExecuteAsync(_ =>
        {
            opStarted.SetResult();
            gate.Wait();
        }));
        await opStarted.Task;

        Assert.False(RgbLibService.UnloadFromCache(wallets, "w", null));

        Assert.False(handle.NativeWalletFreed);
        Assert.True(wallets.ContainsKey("w"));

        gate.Set();
        await opTask;

        Assert.True(SpinUntil(() => handle.NativeWalletFreed), "timed-out unload did not finish after operation completed");
        Assert.True(handle.NativeDisposeCalled);
        Assert.True(SpinUntil(() => !wallets.ContainsKey("w")), "freed timed-out handle stayed cached");

        var replacement = Created(out var replacementHandle);
        Assert.Same(replacement, wallets.GetOrAdd("w", replacement));
        Assert.NotSame(handle, replacementHandle);
    }

    [Fact]
    public void Unload_MissingWallet_IsNoOp()
    {
        var wallets = new ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>>();
        Assert.True(RgbLibService.UnloadFromCache(wallets, "absent", null));
        Assert.False(wallets.ContainsKey("absent"));
    }

    [Fact]
    public async Task DisposalCanCrossAMarkerOnlyAfterTakingNativeAccessExclusively()
    {
        var walletDir = Path.Combine(Path.GetTempPath(), $"rgb-dispose-lease-{Guid.NewGuid():N}");
        try
        {
            using var parent = RgbNativeSendLease.AcquireParent(walletDir);
            var handle = new TestHandle(TimeSpan.FromMilliseconds(200), walletDir);
            Task<bool> foreignDispose;
            using (ExecutionContext.SuppressFlow())
                foreignDispose = Task.Run(() =>
                {
                    handle.Dispose();
                    return handle.NativeWalletFreed;
                });

            Assert.True(await foreignDispose);
            Assert.True(handle.NativeDisposeCalled);
            parent.ClearActiveMarker(walletDir);
        }
        finally
        {
            try { if (Directory.Exists(walletDir)) Directory.Delete(walletDir, true); } catch { }
        }
    }

    [Fact]
    public void DeferredDisposalIsCoalescedPerHandle()
    {
        var handle = new TestHandle(TimeSpan.FromMilliseconds(200));
        Assert.True(handle.TryStartDeferredDispose());
        Assert.False(handle.TryStartDeferredDispose());
    }
}
