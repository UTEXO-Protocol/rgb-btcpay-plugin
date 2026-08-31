using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal sealed class ReplenishCooldownTracker
{
    const int MaxFailureExponent = 32;
    // 30, not 10: 10 equals the listener's sweep period, so a tracker falling back to it would reinstate the
    // cooldown that can never fire. A defensive default that restores the defect is not defensive.
    static readonly TimeSpan DefaultBaseCooldown = TimeSpan.FromMinutes(30);

    readonly TimeSpan _baseCooldown;
    readonly TimeSpan _maxBackoff;
    readonly ConcurrentDictionary<string, DateTimeOffset> _nextEligible = new();
    readonly ConcurrentDictionary<string, int> _failures = new();

    // Both bounds are guarded here, not only at the configuration accessors: a non-positive base would make
    // Settle stamp an instant already in the past and leave Delay's doubling stuck at zero — "always
    // eligible, never backs off", which is the false-ACCEPT direction. The ceiling is raised to the base for
    // the same reason: a ceiling below the base would collapse every backoff step to one base cooldown.
    internal ReplenishCooldownTracker(TimeSpan baseCooldown, TimeSpan maxBackoff)
    {
        _baseCooldown = baseCooldown > TimeSpan.Zero ? baseCooldown : DefaultBaseCooldown;
        _maxBackoff = maxBackoff < _baseCooldown ? _baseCooldown : maxBackoff;
    }

    internal DateTimeOffset? NextEligibleAt(string walletId) =>
        _nextEligible.TryGetValue(walletId, out var at) ? at : null;

    internal void RecordAttemptSucceeded(string walletId, DateTimeOffset now) => Settle(walletId, now);

    internal void RecordNoActionNeeded(string walletId, DateTimeOffset now) => Settle(walletId, now);

    internal void RecordAttemptFailed(string walletId, DateTimeOffset now)
    {
        var failures = _failures.AddOrUpdate(walletId, 1, (_, current) => Math.Min(current + 1, MaxFailureExponent));
        _nextEligible[walletId] = Advance(now, Delay(failures - 1));
    }

    internal void Prune(IReadOnlyCollection<string> activeWalletIds)
    {
        var activeWalletIdsByOrdinalHash = new HashSet<string>(activeWalletIds);

        foreach (var walletId in _nextEligible.Keys)
            if (!activeWalletIdsByOrdinalHash.Contains(walletId))
                _nextEligible.TryRemove(walletId, out _);

        foreach (var walletId in _failures.Keys)
            if (!activeWalletIdsByOrdinalHash.Contains(walletId))
                _failures.TryRemove(walletId, out _);
    }

    void Settle(string walletId, DateTimeOffset now)
    {
        _failures.TryRemove(walletId, out _);
        _nextEligible[walletId] = Advance(now, _baseCooldown);
    }

    // Saturating rather than `now + delay`: DateTimeOffset arithmetic THROWS on overflow, and an exception
    // raised while stamping a backoff would propagate out of the sweep's inner catch, so the very wallet
    // being penalised would escape its stamp. Delay is already bounded, so this only bites for a ceiling far
    // beyond anything the int-minutes configuration can express — but the guarantee belongs here rather than
    // in an argument about what callers can pass.
    // Bounded by BOTH differences, because each is insufficient alone and each direction has bitten:
    // `DateTimeOffset.MaxValue - now` measures UtcTicks while `now + delay` adds to the clock DateTime, so a
    // POSITIVE offset overflows the clock inside the UTC bound; and `DateTime.MaxValue - now.DateTime` bounds
    // only the clock, while the DateTimeOffset constructor also rejects utcTicks > MaxTicks, so a NEGATIVE
    // offset overflows UTC inside the clock bound. Either way the throw lands while stamping a backoff, so
    // the wallet being penalised would escape its stamp. Unreachable in production — every caller passes
    // DateTimeOffset.UtcNow, offset zero, and P-C7 pins that — but the guarantee belongs here.
    static DateTimeOffset Advance(DateTimeOffset now, TimeSpan delay)
    {
        var clockRoom = DateTime.MaxValue - now.DateTime;
        var utcRoom = DateTimeOffset.MaxValue - now;
        var room = clockRoom < utcRoom ? clockRoom : utcRoom;
        return delay > room ? DateTimeOffset.MaxValue : now + delay;
    }

    // WHY saturating doubling rather than base * 2^n: Math.Pow or TimeSpan multiplication wraps at roughly 31
    // consecutive failures — about three days of uptime for an unfunded wallet — and a wrapped delay lands in
    // the past, which would restore the every-sweep retry storm the cooldown exists to stop.
    //
    // The second clause is the overflow guard and MUST be against long.MaxValue, not the ceiling: halving the
    // ceiling would stop a doubling early whenever the ceiling is not an exact power-of-two multiple of the
    // base, so a configured 60-minute ceiling would saturate at 40 and the backoff would come out SHORTER
    // than configured — the permissive direction. It was hidden at the time because the then-default ladder
    // was 10/20/40/80/160, exactly 10 x 16. NonPowerOfTwoCeiling_IsActuallyReached covers the truncation
    // itself with ceilings 60/100/45; DefaultLadder_IsThirtySixtyOneTwentyThenTheCeiling is the one that
    // reads the SHIPPED pair out of RGBConfiguration.
    // Guarding against long.MaxValue >> 1 instead keeps the shift provably overflow-free for any
    // ceiling, including TimeSpan.MaxValue, while still letting `ticks` overshoot the ceiling so Math.Min
    // lands exactly on it.
    TimeSpan Delay(int doublings)
    {
        var ticks = _baseCooldown.Ticks;
        for (var i = 0; i < doublings && ticks < _maxBackoff.Ticks && ticks <= long.MaxValue >> 1; i++) ticks <<= 1;
        return TimeSpan.FromTicks(Math.Min(ticks, _maxBackoff.Ticks));
    }
}
