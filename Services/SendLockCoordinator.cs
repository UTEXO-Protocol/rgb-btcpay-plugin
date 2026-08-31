using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class SendLockCoordinator
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> _locks;
    readonly Func<string, CancellationToken, Task<bool>> _mark;
    readonly Func<string, CancellationToken, Task> _clear;
    readonly Action<string> _evict;
    readonly Func<string, CancellationToken, Task> _fsync;

    // evict sits between clear and fsync deliberately: those two are the only mutually-substitutable
    // parameters, so separating them means a swap of neighbours does not compile. That is friction, not a
    // guarantee — mark still converts into the clear slot by Task<bool>->Task return covariance — so the
    // construction site is pinned by argument identity in RgbQuarantineDischargeSourcePinTests.
    public SendLockCoordinator(
        ConcurrentDictionary<string, SemaphoreSlim> locks,
        Func<string, CancellationToken, Task<bool>> mark,
        Func<string, CancellationToken, Task> clear,
        Action<string> evict,
        Func<string, CancellationToken, Task> fsync)
    {
        _locks = locks;
        _mark = mark;
        _clear = clear;
        _evict = evict;
        _fsync = fsync;
    }

    public async Task<T> WithSendLockAsync<T>(string walletId, Func<Task<T>> op, CancellationToken ct = default)
    {
        var sendLock = _locks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
            return await WriteAheadAsync(walletId, _ => op(), ct);
        }
        finally { sendLock.Release(); }
    }

    public Task WithSendLockAsync(string walletId, Func<Task> op, CancellationToken ct = default)
        => WithSendLockAsync<object?>(walletId, async () => { await op(); return null; }, ct);

    public async Task<bool> TryWithSendLockAsync(string walletId, Func<Task> op, CancellationToken ct = default)
    {
        var sendLock = _locks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        if (!await sendLock.WaitAsync(0, ct))
            return false;
        try
        {
            await WriteAheadAsync<object?>(walletId, async _ => { await op(); return null; }, ct);
            return true;
        }
        finally { sendLock.Release(); }
    }

    public async Task<bool> TryWithSendLockAsync(
        string walletId, Func<bool, Task> op, CancellationToken ct = default)
    {
        var sendLock = _locks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        if (!await sendLock.WaitAsync(0, ct))
            return false;
        var releaseLock = true;
        try
        {
            await WriteAheadAsync<object?>(walletId, async marked =>
            {
                await op(marked);
                return null;
            }, ct);
            return true;
        }
        catch (NativeSendChildUnreapedException)
        {
            // Recovery cannot prove the authorized helper is gone. The durable worker marker blocks
            // cross-process access; retaining this wallet's in-process semaphore supplies the matching
            // guarantee for every background operation until restart. Other wallets remain independent.
            releaseLock = false;
            throw;
        }
        finally { if (releaseLock) sendLock.Release(); }
    }

    // Write-ahead WITHOUT acquiring the send lock: callers that already hold it (in-send
    // refreshes, send_end, setup/restore reconciliation) use this to avoid self-deadlock.
    public async Task WriteAheadInlineAsync(string walletId, Func<Task> op, CancellationToken ct = default)
        => await WriteAheadAsync<object?>(walletId, async _ => { await op(); return null; }, ct);

    async Task<T> WriteAheadAsync<T>(string walletId, Func<bool, Task<T>> op, CancellationToken ct)
    {
        var marked = await _mark(walletId, ct);
        T result;
        try
        {
            result = await op(marked);
        }
        catch
        {
            try { _evict(walletId); } catch { }
            throw;
        }
        // Restore only the state this write-ahead changed. A quarantine that predates it records an
        // incompleteness this operation did not reconcile, and no fsync can supply state that was never
        // written — only a reconciling refresh can, and that is RefreshWalletAsync's job, not the
        // coordinator's. Either way the Stock is left durable: the clear fsyncs before committing.
        if (marked) await _clear(walletId, ct);
        else await _fsync(walletId, ct);
        return result;
    }
}
