using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRgbNoticeRaiser
{
    Task RaiseOncePerCauseAsync(
        string storeId, RgbReplenishmentNoticeCause cause, CancellationToken ct = default);
}

internal sealed class RgbNoticeAttemptGate
{
    internal static readonly TimeSpan RetryAfterSendFailure = TimeSpan.FromMinutes(5);

    sealed class StoreEntry
    {
        internal SemaphoreSlim InFlight { get; } = new(1, 1);

        internal ConcurrentDictionary<RgbReplenishmentNoticeCause, bool> Raised { get; } = new();

        internal ConcurrentDictionary<RgbReplenishmentNoticeCause, DateTimeOffset> RetryNotBefore { get; } = new();
    }

    sealed class AttemptLease : IDisposable
    {
        readonly SemaphoreSlim _inFlight;
        int _released;

        internal AttemptLease(SemaphoreSlim inFlight) => _inFlight = inFlight;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _inFlight.Release();
        }
    }

    readonly ConcurrentDictionary<string, StoreEntry> _byStore = new();

    StoreEntry Entry(string storeId) => _byStore.GetOrAdd(storeId, _ => new StoreEntry());

    internal bool TryBeginAttempt(
        string storeId, RgbReplenishmentNoticeCause cause, DateTimeOffset now, out IDisposable? lease)
    {
        lease = null;
        var entry = Entry(storeId);

        if (entry.Raised.ContainsKey(cause)) return false;
        if (entry.RetryNotBefore.TryGetValue(cause, out var notBefore) && now < notBefore) return false;

        if (!entry.InFlight.Wait(0)) return false;

        if (entry.Raised.ContainsKey(cause))
        {
            entry.InFlight.Release();
            return false;
        }

        lease = new AttemptLease(entry.InFlight);
        return true;
    }

    internal void MarkRaised(string storeId, RgbReplenishmentNoticeCause cause)
    {
        var entry = Entry(storeId);
        entry.Raised[cause] = true;
        entry.RetryNotBefore.TryRemove(cause, out _);
    }

    internal void MarkSendFailed(string storeId, RgbReplenishmentNoticeCause cause, DateTimeOffset now) =>
        Entry(storeId).RetryNotBefore[cause] = now + RetryAfterSendFailure;

    internal bool IsRaised(string storeId, RgbReplenishmentNoticeCause cause) =>
        Entry(storeId).Raised.ContainsKey(cause);
}
