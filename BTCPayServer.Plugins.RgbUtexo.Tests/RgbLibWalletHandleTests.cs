using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbLibWalletHandleTests
{
    sealed class TestHandle : RgbLibWalletHandle
    {
        public TestHandle(TimeSpan disposeTimeout) : base("test-wallet", disposeTimeout) { }

        int _nativeDisposeCount;
        public int NativeDisposeCount => Volatile.Read(ref _nativeDisposeCount);
        public bool NativeDisposeCalled => NativeDisposeCount > 0;

        protected override void DisposeNativeWallet() => Interlocked.Increment(ref _nativeDisposeCount);
    }

    static TestHandle NewHandle() => new(TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task Dispose_DrainsInFlightOp_ThenFrees()
    {
        var handle = NewHandle();
        var opStarted = new TaskCompletionSource();
        using var gate = new ManualResetEventSlim(false);
        int opCompleted = 0;

        var opTask = Task.Run(() => handle.ExecuteAsync(_ =>
        {
            opStarted.SetResult();
            gate.Wait();
            Volatile.Write(ref opCompleted, 1);
        }));

        await opStarted.Task;
        var disposeTask = Task.Run(() => handle.Dispose());
        await Task.Delay(50);
        gate.Set();
        await disposeTask;
        await opTask;

        Assert.Equal(1, Volatile.Read(ref opCompleted));
        Assert.True(handle.IsDisposed);
        Assert.True(handle.NativeDisposeCalled);
        Assert.True(handle.NativeWalletFreed);
    }

    [Fact]
    public async Task Dispose_TimesOut_DoesNotFree_OpStillCompletes()
    {
        var handle = NewHandle();
        var opStarted = new TaskCompletionSource();
        using var gate = new ManualResetEventSlim(false);

        var opTask = Task.Run(() => handle.ExecuteAsync(_ =>
        {
            opStarted.SetResult();
            gate.Wait();
        }));

        await opStarted.Task;
        var sw = Stopwatch.StartNew();
        await Task.Run(() => handle.Dispose());
        sw.Stop();

        Assert.True(handle.IsDisposed);
        Assert.False(handle.NativeDisposeCalled);
        Assert.False(handle.NativeWalletFreed);
        Assert.True(sw.ElapsedMilliseconds >= 150);

        gate.Set();
        await opTask;
    }

    [Fact]
    public async Task ExecuteAsync_AfterDispose_Throws()
    {
        var handle = NewHandle();
        handle.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.ExecuteAsync(_ => 0));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.ExecuteAsync(_ => { }));
    }

    [Fact]
    public Task MultipleQueuedOps_Generic_NoHang_AndReCheckBlocksPostFreeBodies() => CascadeCore(generic: true);

    [Fact]
    public Task MultipleQueuedOps_Void_NoHang_AndReCheckBlocksPostFreeBodies() => CascadeCore(generic: false);

    async Task CascadeCore(bool generic)
    {
        var handle = NewHandle();
        var aHeld = new TaskCompletionSource();
        using var ga = new ManualResetEventSlim(false);
        int ranAfterFree = 0;

        var aTask = Task.Run(() => handle.ExecuteAsync(_ =>
        {
            aHeld.SetResult();
            ga.Wait();
        }));
        await aHeld.Task;

        Task StartQueued() => generic
            ? handle.ExecuteAsync(_ =>
            {
                if (handle.NativeDisposeCount > 0) Volatile.Write(ref ranAfterFree, 1);
                return 0;
            })
            : handle.ExecuteAsync(_ =>
            {
                if (handle.NativeDisposeCount > 0) Volatile.Write(ref ranAfterFree, 1);
            });

        var b = StartQueued();
        var c = StartQueued();

        var disposeTask = Task.Run(() => handle.Dispose());
        ga.Set();

        var all = Task.WhenAll(b, c);
        var done = await Task.WhenAny(all, Task.Delay(5000));
        Assert.Same(all, done);

        await disposeTask;
        await aTask;

        Assert.Equal(0, Volatile.Read(ref ranAfterFree));
        Assert.True(b.IsCompletedSuccessfully || (b.IsFaulted && b.Exception!.InnerException is ObjectDisposedException));
        Assert.True(c.IsCompletedSuccessfully || (c.IsFaulted && c.Exception!.InnerException is ObjectDisposedException));
    }

    [Fact]
    public async Task ConcurrentDispose_IsSafe()
    {
        var handle = NewHandle();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() => handle.Dispose())).ToArray();
        await Task.WhenAll(tasks);
        Assert.True(handle.IsDisposed);
    }

    [Fact]
    public void IdleDispose_IsClean()
    {
        var handle = NewHandle();
        handle.Dispose();
        Assert.True(handle.IsDisposed);
        Assert.True(handle.NativeWalletFreed);
        Assert.Equal(1, handle.NativeDisposeCount);

        handle.Dispose();
        Assert.Equal(1, handle.NativeDisposeCount);
    }

    [Fact]
    public Task UnderLockReCheck_Generic_BlocksOpThatAcquiresAfterDisposal() => UnderLockReCheckCore(generic: true);

    [Fact]
    public Task UnderLockReCheck_Void_BlocksOpThatAcquiresAfterDisposal() => UnderLockReCheckCore(generic: false);

    async Task UnderLockReCheckCore(bool generic)
    {
        var handle = NewHandle();
        var aHeld = new TaskCompletionSource();
        using var ga = new ManualResetEventSlim(false);
        int bRan = 0;

        var aTask = Task.Run(() => handle.ExecuteAsync(_ =>
        {
            aHeld.SetResult();
            ga.Wait();
        }));
        await aHeld.Task;

        Task b = generic
            ? handle.ExecuteAsync(_ =>
            {
                Volatile.Write(ref bRan, 1);
                return 0;
            })
            : handle.ExecuteAsync(_ =>
            {
                Volatile.Write(ref bRan, 1);
            });

        await Task.Run(() => handle.Dispose());
        Assert.True(handle.IsDisposed);
        Assert.Equal(0, handle.NativeDisposeCount);
        Assert.False(handle.NativeWalletFreed);

        ga.Set();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => b);
        Assert.Equal(0, Volatile.Read(ref bRan));
        await aTask;
    }
}
