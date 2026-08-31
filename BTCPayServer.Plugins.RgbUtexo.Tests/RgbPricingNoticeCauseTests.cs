using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingNoticeCauseTests
{
    const RgbReplenishmentNoticeCause Pricing = RgbReplenishmentNoticeCause.PricingCodeHasNoRule;

    // A cause that falls through StampMarker's switch is never recorded, so the notice re-fires on every
    // attempt forever — the self-inflicted DoS on the merchant's notification feed. MarkerOf falling
    // through is worse still: it returns MinValue, which is non-null, so the notice never fires at all.
    [Fact]
    public void PricingNoticeCause_HasItsOwnPersistedMarker()
    {
        var row = new RGBStoreNoticeState { StoreId = "s1" };
        var at = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(RgbReplenishmentNoticeService.MarkerOf(row, Pricing));

        RgbReplenishmentNoticeService.StampMarker(row, Pricing, at);

        Assert.Equal(at, RgbReplenishmentNoticeService.MarkerOf(row, Pricing));
        Assert.Null(row.NotAuthorizedNoticeSentAt);
        Assert.Null(row.CapDisabledNoticeSentAt);
        Assert.Null(row.ConfigOutOfBoundsNoticeSentAt);
    }

    [Fact]
    public void EveryCause_StampsADistinctMarker()
    {
        foreach (var cause in Enum.GetValues<RgbReplenishmentNoticeCause>()
                     .Where(c => c != RgbReplenishmentNoticeCause.None))
        {
            var row = new RGBStoreNoticeState { StoreId = "s1" };
            RgbReplenishmentNoticeService.StampMarker(row, cause, DateTimeOffset.UnixEpoch);

            var stamped = typeof(RGBStoreNoticeState).GetProperties()
                .Where(p => p.PropertyType == typeof(DateTimeOffset?))
                .Count(p => p.GetValue(row) != null);

            Assert.True(stamped == 1,
                $"StampMarker for {cause} set {stamped} marker(s); exactly one is expected. A cause with "
                + "no case falls through and is never recorded, so its notice re-fires on every attempt.");
        }
    }

    [Fact]
    public void PricingNoticeCause_HasANonEmptyMessageNamingThePricingCode()
    {
        var message = RgbReplenishmentNotice.MessageFor(Pricing);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("pricing code", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PricingNoticeCause_DoesNotInviteAReplenishmentGrant()
    {
        Assert.False(RgbReplenishmentNotice.InvitesGrant(Pricing));
        Assert.False(RgbReplenishmentNotice.LogsPerSweep(Pricing));
    }

    // The replenishment predicate must never produce the pricing cause: it is raised from the payment
    // handler, and the settings page renders whatever Evaluate returns.
    [Fact]
    public void TheReplenishmentPredicate_NeverProducesThePricingCause()
    {
        foreach (var paymentMethodEnabled in new[] { true, false })
        foreach (var hasStoredConfig in new[] { true, false })
        foreach (var configValuesValid in new[] { true, false })
        foreach (var cap in new[] { -1, 0, 1, 50 })
        foreach (var granted in new[] { true, false })
            Assert.NotEqual(Pricing, RgbReplenishmentNotice.Evaluate(
                paymentMethodEnabled, hasStoredConfig, configValuesValid, cap, granted));
    }

    // Both enums are appended to, never reordered: RgbReplenishmentNoticeCause is persisted inside the
    // notification blob and read back by MessageFor, so a moved ordinal would silently re-label every
    // stored notification.
    [Fact]
    public void ExistingCauseOrdinalsAreUnchanged()
    {
        Assert.Equal(0, (int)RgbReplenishmentNoticeCause.None);
        Assert.Equal(1, (int)RgbReplenishmentNoticeCause.ConfigOutOfBounds);
        Assert.Equal(2, (int)RgbReplenishmentNoticeCause.CapDisabledDeploymentWide);
        Assert.Equal(3, (int)RgbReplenishmentNoticeCause.NotAuthorized);
        Assert.Equal(4, (int)Pricing);

        Assert.Equal(0, (int)RgbRateFailure.None);
        Assert.Equal(1, (int)RgbRateFailure.NoRate);
        Assert.Equal(2, (int)RgbRateFailure.Timeout);
        Assert.Equal(3, (int)RgbRateFailure.Error);
        Assert.Equal(4, (int)RgbRateFailure.NoRule);
    }
}

public class RgbNoticeAttemptGateTests
{
    const RgbReplenishmentNoticeCause Pricing = RgbReplenishmentNoticeCause.PricingCodeHasNoRule;
    const RgbReplenishmentNoticeCause Other = RgbReplenishmentNoticeCause.NotAuthorized;

    static readonly DateTimeOffset T0 = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    static RgbNoticeAttemptGate Gate() => new();

    // The durable marker alone cannot de-duplicate a cause raised once per invoice: two concurrent
    // callers both read `row == null`, both send, and one insert then violates the primary key. This is
    // the primitive that collapses the burst, so it is the one that has to be proven under contention.
    [Fact]
    public void ConcurrentAttempts_AdmitExactlyOneCaller()
    {
        var gate = Gate();
        var admitted = 0;
        var leases = new System.Collections.Concurrent.ConcurrentBag<IDisposable>();

        // Leases are held, not released inside the loop: the property is "at most one attempt AT A
        // TIME". Releasing as we go would let the next caller in legitimately and measure nothing.
        Parallel.For(0, 64, _ =>
        {
            if (gate.TryBeginAttempt("s1", Pricing, T0, out var lease))
            {
                Interlocked.Increment(ref admitted);
                leases.Add(lease!);
            }
        });

        Assert.Equal(1, admitted);
        foreach (var lease in leases) lease.Dispose();
    }

    // IN-FLIGHT is a held lock, not a timer. The previous design used the failure backoff as the
    // in-flight lease too, so an attempt still running after five minutes let a second public invoice
    // admit another one, and two slow sends then duplicated the notification. There is no duration a
    // clock can advance past any more, and this is the test that says so — the concurrency test above
    // cannot, because it releases immediately.
    [Fact]
    public void AnAttemptStillRunning_IsNeverSupersededHoweverLongItTakes()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));

        foreach (var elapsed in new[] { TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1),
                     TimeSpan.FromDays(3650) })
            Assert.False(gate.TryBeginAttempt("s1", Pricing, T0 + elapsed, out _),
                $"a second attempt was admitted {elapsed} after the first began while that first attempt "
                + "is still holding its lease. In-flight must not expire: both would send, and the "
                + "notification commits before the plugin's own marker is saved.");

        lease!.Dispose();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0 + TimeSpan.FromDays(3650), out _));
    }

    // The per-store lock also serialises DIFFERENT causes, which is what stops two of them racing to
    // insert the same new RGB_StoreNoticeState row and one losing on the primary key.
    [Fact]
    public void TwoCausesForOneStore_DoNotRunConcurrently()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));

        Assert.False(gate.TryBeginAttempt("s1", Other, T0, out _));

        lease!.Dispose();
        Assert.True(gate.TryBeginAttempt("s1", Other, T0, out _));
    }

    [Fact]
    public void ADifferentStore_IsNeverBlockedByAnother()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out _));

        Assert.True(gate.TryBeginAttempt("s2", Pricing, T0, out _));
    }

    [Fact]
    public void AfterMarkRaised_NoLaterAttemptIsAdmitted()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));
        gate.MarkRaised("s1", Pricing);
        lease!.Dispose();

        Assert.False(gate.TryBeginAttempt("s1", Pricing, T0 + TimeSpan.FromDays(3650), out _));
        Assert.True(gate.IsRaised("s1", Pricing));
    }

    // RETRY-AFTER is entered only by an actual send failure, and it is a distinct state from in-flight:
    // the lease is already released, so the only thing holding the caller off is the clock.
    [Fact]
    public void AFailedSend_IsRetryableOnlyAfterTheRetryWindow()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));
        gate.MarkSendFailed("s1", Pricing, T0);
        lease!.Dispose();

        var window = RgbNoticeAttemptGate.RetryAfterSendFailure;
        Assert.False(gate.TryBeginAttempt("s1", Pricing, T0 + window - TimeSpan.FromSeconds(1), out _));
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0 + window, out _));
    }

    // Raised must win over a pending retry window, or a cause that succeeded on a retry would be
    // re-attempted once more when the stale window elapsed.
    [Fact]
    public void MarkRaisedAfterAFailure_ClearsTheRetryWindow()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));
        gate.MarkSendFailed("s1", Pricing, T0);
        gate.MarkRaised("s1", Pricing);
        lease!.Dispose();

        Assert.False(gate.TryBeginAttempt(
            "s1", Pricing, T0 + RgbNoticeAttemptGate.RetryAfterSendFailure, out _));
    }

    [Fact]
    public void ALeaseIsIdempotentOnDispose()
    {
        var gate = Gate();
        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var lease));
        lease!.Dispose();
        lease.Dispose();

        Assert.True(gate.TryBeginAttempt("s1", Pricing, T0, out var second));
        Assert.False(gate.TryBeginAttempt("s1", Pricing, T0, out _),
            "a double Dispose released the in-flight lock twice, so two attempts can now run at once");
        second!.Dispose();
    }

    // The three sweep-driven causes retried on the next sweep before this gate existed. A retry window
    // at or above the sweep period would make a failed notice skip a sweep.
    [Fact]
    public void TheRetryWindow_IsShorterThanTheSweepPeriod()
    {
        var sweep = TimeSpan.FromMinutes(RGBInvoiceListener.UtxoCheckMinutes);

        Assert.True(RgbNoticeAttemptGate.RetryAfterSendFailure < sweep,
            $"the retry window is {RgbNoticeAttemptGate.RetryAfterSendFailure} and the replenishment "
            + $"sweep runs every {sweep}. A window at or above the sweep period makes a failed notice "
            + "skip the next sweep, so the three sweep-driven causes would warn later than they did "
            + "before this gate was introduced.");
    }
}

// A deterministic companion to the concurrency test: the race window in a real database round trip is
// small enough that a timing-based test can pass with the gate deleted, so the ORDER is pinned too.
public class RgbNoticeGateOrderingPinTests
{
    const string ServiceFile = "Services/RgbReplenishmentNoticeService.cs";
    const string ServiceType = "RgbReplenishmentNoticeService";
    const string Raise = "RaiseOncePerCauseAsync";

    static List<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax> Calls(
        Microsoft.CodeAnalysis.SyntaxNode scope, string name) =>
        scope.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.Expression switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m =>
                    m.Name.Identifier.ValueText == name,
                Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id =>
                    id.Identifier.ValueText == name,
                _ => false
            })
            .ToList();

    [Fact]
    public void TheAttemptIsClaimedBeforeAnyDatabaseWorkAndTheSendIsGuardedByIt()
    {
        var method = RoslynPins.Method(
            PluginCompilation.Shared.Tree(ServiceFile), ServiceType, Raise);
        var body = RoslynPins.BodyOf(method);

        var claim = Calls(body, "TryBeginAttempt");
        Assert.True(claim.Count == 1,
            $"{Raise} invokes TryBeginAttempt {claim.Count} time(s); exactly one is expected. Without it "
            + "every concurrent caller reads a missing row, sends its own notification, and one insert "
            + "then violates the primary key — and a timing-based test can miss that.");

        var context = Calls(body, "CreateContext");
        Assert.True(context.Count == 1, $"{Raise} invokes CreateContext {context.Count} time(s)");
        Assert.True(claim[0].SpanStart < context[0].SpanStart,
            $"{Raise} opens a database context before claiming the attempt, so a burst of invoice "
            + "attempts each pays a query even when the notice has already been raised.");

        var send = Calls(body, "SendBlockedNotificationAsync");
        Assert.True(send.Count == 1, $"{Raise} invokes the send {send.Count} time(s)");
        Assert.True(claim[0].SpanStart < send[0].SpanStart,
            $"{Raise} sends before claiming the attempt, which defeats the gate entirely.");

        // The state TRANSITIONS are not pinned syntactically. They were, and a reviewer showed the
        // clause was a blacklist of branch shapes with a hole in it. They are bound by measurement
        // instead, in RgbPricingNoticeDatabaseTests: AFailedSendForAStoreWithAnExistingNoticeRow_
        // StaysRetryableInProcess covers the send-failed branch, and ANotificationThatCommittedButWas
        // NotRecorded_IsNotResentByThisProcess covers the committed-then-unrecorded branch. What is
        // pinned here is only the ordering no test can observe: that the attempt is claimed before any
        // database or notification work happens at all.
        var markRaised = Calls(body, "MarkRaised");
        var markFailed = Calls(body, "MarkSendFailed");
        Assert.True(markRaised.Count + markFailed.Count > 0,
            $"{Raise} never resolves the attempt it claimed, so the in-flight lease is the only thing "
            + "that ever changes and the cause can never reach a terminal state.");
        Assert.True(markRaised.All(m => m.SpanStart > claim[0].SpanStart)
                    && markFailed.All(m => m.SpanStart > claim[0].SpanStart),
            $"{Raise} resolves the attempt before claiming it");
        Assert.True(markFailed.Count == 1,
            $"{Raise} invokes MarkSendFailed {markFailed.Count} time(s); exactly one is expected — the "
            + "branch where nothing was sent. More than one, or none, means the retry-after-failure "
            + "state is reachable from a path that did send, which resends to a notified merchant.");
        Assert.True(markFailed[0].SpanStart > send[0].SpanStart,
            $"{Raise} can enter the retry-after-failure state before the send has even been attempted.");
    }
}
