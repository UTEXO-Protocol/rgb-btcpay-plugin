using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReplenishCooldownTrackerTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Base = TimeSpan.FromMinutes(10);
    static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(160);

    static ReplenishCooldownTracker New() => new(Base, Ceiling);

    [Fact]
    public void UnknownWallet_HasNoNextEligibleInstant()
        => Assert.Null(New().NextEligibleAt("w1"));

    // A non-positive base would stamp an instant already in the past and leave the doubling stuck at zero:
    // "always eligible, never backs off". The configuration accessors clamp too, but the tracker must not
    // depend on a distant guard for a false-ACCEPT this direct.
    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void NonPositiveBaseCooldown_FallsBackToTheSafeDefault(int minutes)
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromMinutes(minutes), Ceiling);
        tracker.RecordAttemptSucceeded("w1", Now);
        Assert.Equal(Now + TimeSpan.FromMinutes(30), tracker.NextEligibleAt("w1"));
    }

    // A ceiling below the base would collapse every backoff step to a single base cooldown.
    [Fact]
    public void MaxBackoffBelowTheBase_IsRaisedToTheBase()
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));
        tracker.RecordAttemptFailed("w1", Now);
        Assert.Equal(Now + TimeSpan.FromMinutes(30), tracker.NextEligibleAt("w1"));
        tracker.RecordAttemptFailed("w1", Now);
        Assert.Equal(Now + TimeSpan.FromMinutes(30), tracker.NextEligibleAt("w1"));
    }

    [Fact]
    public void Success_StampsOneBaseCooldown_AndDoesNotDrift()
    {
        var tracker = New();
        tracker.RecordAttemptSucceeded("w1", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w1"));
        tracker.RecordAttemptSucceeded("w1", Now + Base);
        Assert.Equal(Now + Base + Base, tracker.NextEligibleAt("w1"));
    }

    // Read-before-increment: the FIRST failure costs one base cooldown, so the observable sequence is
    // 10, 20, 40, 80, 160 for THIS base. The shipped pair is 30/160 — see DefaultLadder below, which is
    // the one a live log shows; this test fixes the base so the doubling itself is what is under test.
    [Fact]
    public void ConsecutiveFailures_DoubleAndSaturateAtTheCeiling()
    {
        var tracker = New();
        foreach (var expected in new[] { 10, 20, 40, 80, 160, 160 })
        {
            tracker.RecordAttemptFailed("w1", Now);
            Assert.Equal(Now + TimeSpan.FromMinutes(expected), tracker.NextEligibleAt("w1"));
        }
    }

    [Fact]
    public void SuccessAfterFailures_ResetsToBase()
    {
        var tracker = New();
        tracker.RecordAttemptFailed("w1", Now);
        tracker.RecordAttemptFailed("w1", Now);
        tracker.RecordAttemptSucceeded("w1", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w1"));
        tracker.RecordAttemptFailed("w1", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w1"));
    }

    [Fact]
    public void NoActionNeeded_StampsBaseAndResetsTheExponent()
    {
        var tracker = New();
        tracker.RecordAttemptFailed("w1", Now);
        tracker.RecordAttemptFailed("w1", Now);
        tracker.RecordNoActionNeeded("w1", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w1"));
        tracker.RecordAttemptFailed("w1", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w1"));
    }

    [Fact]
    public void Prune_DropsEntriesForWalletsNoLongerActive()
    {
        var tracker = New();
        tracker.RecordAttemptSucceeded("w1", Now);
        tracker.RecordAttemptFailed("w2", Now);
        tracker.Prune(new[] { "w1" });
        Assert.NotNull(tracker.NextEligibleAt("w1"));
        Assert.Null(tracker.NextEligibleAt("w2"));

        // The failure count must be pruned as well, or deleting Prune's second loop leaves this green while
        // that map grows without bound and a returning wallet inherits a stale exponent.
        tracker.RecordAttemptFailed("w2", Now);
        Assert.Equal(Now + Base, tracker.NextEligibleAt("w2"));
    }

    [Fact]
    public void Prune_TreatsWalletIdsWithTheSameCaseSensitivityAsBefore()
    {
        var tracker = New();
        tracker.RecordAttemptSucceeded("w1", Now);
        tracker.Prune(new[] { "W1" });
        Assert.Null(tracker.NextEligibleAt("w1"));
    }

    // `base * 2^failures` wraps or throws at roughly 31 consecutive failures — about three days of uptime for
    // an unfunded wallet — and a wrapped delay lands in the past, restoring the every-sweep retry storm.
    [Fact]
    public void ThousandConsecutiveFailures_StayAtTheCeilingWithoutOverflow()
    {
        var tracker = New();
        for (var i = 0; i < 1000; i++) tracker.RecordAttemptFailed("w1", Now);
        Assert.Equal(Now + Ceiling, tracker.NextEligibleAt("w1"));
        Assert.True(tracker.NextEligibleAt("w1") > Now);
    }

    // The ctor guards its other two hostile inputs directly rather than trusting a distant caller; this pins
    // that the doubling does too. With a ceiling above half of long.MaxValue ticks, doubling on a comparison
    // against the ceiling itself overflows into a NEGATIVE delay, which stamps eligibility in the PAST and
    // restores the every-sweep retry storm the cooldown exists to stop — the false-ACCEPT direction.
    [Fact]
    public void EnormousCeiling_StillYieldsAForwardDelayAtEveryFailureCount()
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromMinutes(10), TimeSpan.MaxValue);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 200; i++)
        {
            tracker.RecordAttemptFailed("w1", now);
            Assert.True(tracker.NextEligibleAt("w1") > now,
                $"failure {i + 1} produced a non-forward delay of {tracker.NextEligibleAt("w1") - now}");
        }
    }

    // A ceiling that is NOT an exact power-of-two multiple of the base. The default 160 = 10 x 16 is exact,
    // so the 10/20/40/80/160 test passes even when the doubling stops one step early and the backoff
    // saturates BELOW the configured ceiling — shorter than configured, the permissive direction.
    [Theory]
    [InlineData(60, new[] { 10, 20, 40, 60, 60 })]
    [InlineData(100, new[] { 10, 20, 40, 80, 100 })]
    [InlineData(45, new[] { 10, 20, 40, 45, 45 })]
    public void NonPowerOfTwoCeiling_IsActuallyReached(int ceilingMinutes, int[] expectedMinutes)
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(ceilingMinutes));
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var actual = new List<int>();
        foreach (var _ in expectedMinutes)
        {
            tracker.RecordAttemptFailed("w1", now);
            actual.Add((int)(tracker.NextEligibleAt("w1")!.Value - now).TotalMinutes);
        }

        Assert.Equal(expectedMinutes, actual);
    }

    // The saturation bound must be measured against the CLOCK time, not the UTC difference. Here the clock
    // sits one hour below DateTime.MaxValue while UTC sits fifteen hours below it, so a ten-hour delay is
    // inside the UTC bound and outside the clock bound: guarding on `DateTimeOffset.MaxValue - now` lets it
    // through and `now + delay` throws — while stamping a backoff, so the penalised wallet escapes its stamp.
    // The mirror of the case below, and the reason the bound takes the min of both differences: here the
    // clock has 14 hours of room while UTC has none (UtcTicks is already MaxTicks), and the DateTimeOffset
    // constructor rejects utcTicks > MaxTicks. Fixing only the positive direction moves the throw here.
    [Fact]
    public void StampNearTheEndOfTimeWithANegativeOffset_SaturatesInsteadOfThrowing()
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromHours(10), TimeSpan.FromHours(10));
        var now = new DateTimeOffset(DateTime.MaxValue.AddHours(-14), TimeSpan.FromHours(-14));

        tracker.RecordAttemptFailed("w1", now);

        Assert.Equal(DateTimeOffset.MaxValue, tracker.NextEligibleAt("w1"));
    }

    [Fact]
    public void StampNearTheEndOfTimeWithAPositiveOffset_SaturatesInsteadOfThrowing()
    {
        var tracker = new ReplenishCooldownTracker(TimeSpan.FromHours(10), TimeSpan.FromHours(10));
        var now = new DateTimeOffset(DateTime.MaxValue.AddHours(-1), TimeSpan.FromHours(14));

        tracker.RecordAttemptFailed("w1", now);

        Assert.Equal(DateTimeOffset.MaxValue, tracker.NextEligibleAt("w1"));
    }

    // The SHIPPED pair, read from RGBConfiguration rather than from literals. Every other test here fixes its
    // own base, which is the isolation that let the default cooldown equal the sweep period undetected: each
    // component was correct and no test looked at the values actually deployed.
    [Fact]
    public void DefaultLadder_IsThirtySixtyOneTwentyThenTheCeiling()
    {
        var cfg = new RGBConfiguration();
        var tracker = new ReplenishCooldownTracker(
            TimeSpan.FromMinutes(cfg.AutoUtxoCooldownMinutes),
            TimeSpan.FromMinutes(cfg.AutoUtxoMaxBackoffMinutes));

        var actual = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            tracker.RecordAttemptFailed("w1", Now);
            actual.Add((int)(tracker.NextEligibleAt("w1")!.Value - Now).TotalMinutes);
        }

        Assert.Equal(new[] { 30, 60, 120, 160, 160 }, actual);
    }
}
