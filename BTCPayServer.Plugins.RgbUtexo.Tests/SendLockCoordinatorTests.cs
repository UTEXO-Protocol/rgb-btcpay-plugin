using System.Collections.Concurrent;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SendLockCoordinatorTests
{
    sealed class Recorder
    {
        public readonly List<string> Events = new();
        public readonly HashSet<string> Marked = new();
        public readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

        // The Marked write stays inside lock (Events): WithSendLock_DifferentWallets_RunConcurrently drives
        // two wallets at once, so an unlocked Add would race the locked Remove.
        public SendLockCoordinator Build(Func<string, CancellationToken, Task>? fsync = null) => new(
            Locks,
            (id, _) =>
            {
                bool added;
                lock (Events) { Events.Add($"mark:{id}"); added = Marked.Add(id); }
                return Task.FromResult(added);
            },
            (id, _) => { lock (Events) { Events.Add($"clear:{id}"); Marked.Remove(id); } return Task.CompletedTask; },
            id => { lock (Events) { Events.Add($"evict:{id}"); } },
            fsync ?? ((id, _) => { lock (Events) { Events.Add($"fsync:{id}"); } return Task.CompletedTask; }));
    }

    [Fact]
    public async Task WithSendLock_MarksBeforeOp_ClearsOnSuccess()
    {
        var r = new Recorder();
        var c = r.Build();
        await c.WithSendLockAsync("w", () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.Equal(new[] { "mark:w", "op:w", "clear:w" }, r.Events);
        Assert.DoesNotContain("evict:w", r.Events);
        Assert.Empty(r.Marked);
    }

    [Fact]
    public async Task WithSendLock_OpThrows_LeavesMarked_Evicts_NoClear()
    {
        var r = new Recorder();
        var c = r.Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.WithSendLockAsync("w", () => throw new InvalidOperationException("boom")));
        Assert.Equal(new[] { "mark:w", "evict:w" }, r.Events);
        Assert.Contains("w", r.Marked);
        Assert.DoesNotContain("clear:w", r.Events);
    }

    [Fact]
    public async Task WithSendLock_InnerRefreshFailurePropagates_LeavesMarked_Evicts_NoClear()
    {
        // Regression (Finding B blocker): a value-adding op (e.g. cleanup's post-op refresh)
        // MUST let a refresh/persist failure propagate out of the WithSendLock op. If the op
        // swallowed the failure, the coordinator would treat it as success and CLEAR the
        // quarantine over a possibly-incomplete Stock -> a later send could sign a burn.
        var r = new Recorder();
        var c = r.Build();
        async Task CleanupOpWhoseRefreshFails()
        {
            lock (r.Events) r.Events.Add("cleanup:w");
            await Task.Yield();
            throw new InvalidOperationException("refresh failed");
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.WithSendLockAsync("w", CleanupOpWhoseRefreshFails));
        Assert.Contains("w", r.Marked);
        Assert.Contains("evict:w", r.Events);
        Assert.DoesNotContain("clear:w", r.Events);
    }

    // T1. The regression test for the audit finding: a successful operation on an ALREADY-quarantined wallet
    // must not discharge it. Pre-populating Marked is the discriminating input — it makes mark report that it
    // did not set the flag, which is the only thing the coordinator reads to make this decision.
    [Fact]
    public async Task WriteAhead_PreexistingMark_OpSucceeds_FsyncsButDoesNotClear()
    {
        var r = new Recorder();
        r.Marked.Add("w");
        var c = r.Build();
        await c.WithSendLockAsync("w", () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.Equal(new[] { "mark:w", "op:w", "fsync:w" }, r.Events);
        Assert.DoesNotContain("clear:w", r.Events);
        Assert.Contains("w", r.Marked);
    }

    // T3. Through the non-blocking entry point, and it additionally pins that "the op ran" and "the quarantine
    // was discharged" are now separate facts — the bool says the first, the events say the second.
    [Fact]
    public async Task TryWithSendLock_PreexistingMark_RunsOpButDoesNotClear()
    {
        var r = new Recorder();
        r.Marked.Add("w");
        var c = r.Build();
        var acquired = await c.TryWithSendLockAsync("w",
            () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.True(acquired);
        Assert.Equal(new[] { "mark:w", "op:w", "fsync:w" }, r.Events);
        Assert.DoesNotContain("clear:w", r.Events);
        Assert.Contains("w", r.Marked);
    }

    // T4. Through the inline entry point, which takes no lock because its callers already hold one.
    [Fact]
    public async Task InlineWriteAhead_PreexistingMark_DoesNotClear()
    {
        var r = new Recorder();
        r.Marked.Add("w");
        var c = r.Build();
        await c.WriteAheadInlineAsync("w",
            () => { lock (r.Events) r.Events.Add("inline:w"); return Task.CompletedTask; });
        Assert.Equal(new[] { "mark:w", "inline:w", "fsync:w" }, r.Events);
        Assert.DoesNotContain("clear:w", r.Events);
        Assert.Contains("w", r.Marked);
    }

    // T10. A failing durability barrier must propagate rather than be swallowed. No eviction: the fsync call
    // sits after the try/catch, exactly as the clear does, so this matches the existing behaviour for _clear.
    [Fact]
    public async Task WriteAhead_FsyncThrows_Propagates_LeavesMarked()
    {
        var r = new Recorder();
        r.Marked.Add("w");
        var c = r.Build(fsync: (_, _) => throw new IOException("fsync failed"));
        await Assert.ThrowsAsync<IOException>(() =>
            c.WithSendLockAsync("w", () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; }));
        // The exact list, not just the absences: asserting only "no clear, still marked" would stay green if
        // the fsync were moved BEFORE the op, since Marked is pre-seeded and only clear removes from it.
        Assert.Equal(new[] { "mark:w", "op:w" }, r.Events);
        Assert.Contains("w", r.Marked);
    }

    [Fact]
    public async Task WithSendLock_SameWallet_IsMutuallyExclusive()
    {
        var r = new Recorder();
        var c = r.Build();
        int active = 0, maxActive = 0;
        async Task Op()
        {
            var now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(30);
            Interlocked.Decrement(ref active);
        }
        await Task.WhenAll(
            c.WithSendLockAsync("w", Op),
            c.WithSendLockAsync("w", Op),
            c.WithSendLockAsync("w", Op));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task WithSendLock_DifferentWallets_RunConcurrently()
    {
        var r = new Recorder();
        var c = r.Build();
        var gate = new TaskCompletionSource();
        using var started = new CountdownEvent(2);
        Task Op()
        {
            started.Signal();
            return gate.Task;
        }
        var t1 = c.WithSendLockAsync("a", Op);
        var t2 = c.WithSendLockAsync("b", Op);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        gate.SetResult();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task InlineWriteAhead_UnderHeldLock_DoesNotSelfDeadlock()
    {
        var r = new Recorder();
        var c = r.Build();
        // Simulate a send that already holds _sendLocks (acquired directly), then performs an
        // inline write-ahead for the same wallet — must not re-acquire and deadlock.
        var sendLock = r.Locks.GetOrAdd("w", _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync();
        try
        {
            var completed = c.WriteAheadInlineAsync("w",
                () => { lock (r.Events) r.Events.Add("inline:w"); return Task.CompletedTask; });
            var done = await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(completed, done);
        }
        finally { sendLock.Release(); }
        Assert.Equal(new[] { "mark:w", "inline:w", "clear:w" }, r.Events);
    }

    [Fact]
    public async Task TryWithSendLock_WhenHeld_SkipsWithoutRunning()
    {
        var r = new Recorder();
        var c = r.Build();
        var sendLock = r.Locks.GetOrAdd("w", _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync();
        try
        {
            var ran = false;
            var acquired = await c.TryWithSendLockAsync("w", () => { ran = true; return Task.CompletedTask; });
            Assert.False(acquired);
            Assert.False(ran);
            Assert.Empty(r.Events);
        }
        finally { sendLock.Release(); }
    }

    [Fact]
    public async Task TryWithSendLock_WhenFree_RunsWithWriteAhead()
    {
        var r = new Recorder();
        var c = r.Build();
        var acquired = await c.TryWithSendLockAsync("w",
            () => { lock (r.Events) r.Events.Add("op:w"); return Task.CompletedTask; });
        Assert.True(acquired);
        Assert.Equal(new[] { "mark:w", "op:w", "clear:w" }, r.Events);
    }

    [Fact]
    public async Task TryWithSendLock_ReportsWhetherThisCallCreatedTheWriteAhead()
    {
        var r = new Recorder();
        var c = r.Build();
        bool? firstMarked = null;
        Assert.True(await c.TryWithSendLockAsync("w", marked =>
        {
            firstMarked = marked;
            return Task.CompletedTask;
        }));
        Assert.True(firstMarked);

        r.Marked.Add("w");
        bool? secondMarked = null;
        Assert.True(await c.TryWithSendLockAsync("w", marked =>
        {
            secondMarked = marked;
            return Task.CompletedTask;
        }));
        Assert.False(secondMarked);
    }

    [Fact]
    public async Task RecoveryRetainsOnlyTheAffectedWalletLockWhenChildExitIsUnproven()
    {
        var r = new Recorder();
        var c = r.Build();

        await Assert.ThrowsAsync<NativeSendChildUnreapedException>(() =>
            c.TryWithSendLockAsync("stuck", _ =>
                throw new NativeSendChildUnreapedException()));

        var stuckRan = false;
        Assert.False(await c.TryWithSendLockAsync("stuck", () =>
        {
            stuckRan = true;
            return Task.CompletedTask;
        }));
        Assert.False(stuckRan);

        var otherRan = false;
        Assert.True(await c.TryWithSendLockAsync("other", () =>
        {
            otherRan = true;
            return Task.CompletedTask;
        }));
        Assert.True(otherRan);
    }
}
