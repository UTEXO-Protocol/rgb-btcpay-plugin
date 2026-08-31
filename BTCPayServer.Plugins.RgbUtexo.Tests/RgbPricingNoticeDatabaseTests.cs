using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The marker column is added by an EF migration, and RGBPluginMigrationRunner calls MigrateAsync. A
// property added to the entity without the migration compiles, passes every in-memory test, and then
// throws "column does not exist" on the first refused invoice in production — so the column has to be
// exercised against a real relational provider, not against the model snapshot's text.
public sealed class RgbPricingNoticeDatabaseTests
{
    const RgbReplenishmentNoticeCause Pricing = RgbReplenishmentNoticeCause.PricingCodeHasNoRule;

    sealed class CountingNoticeService : RgbReplenishmentNoticeService
    {
        int _sends;

        internal CountingNoticeService(RGBPluginDbContextFactory db)
            : base(db, null!, NullLogger<RgbReplenishmentNoticeService>.Instance) { }

        internal int Sends => Volatile.Read(ref _sends);

        // Holding the send is what makes the concurrency assertion DETERMINISTIC. While a caller is
        // parked here it has not yet stamped the marker or saved, so with the attempt gate removed every
        // other caller reads a missing row and arrives here too. Without the hold the first caller
        // finishes and stamps before the rest even query, and the test passes with the gate deleted —
        // measured, not assumed.
        internal TaskCompletionSource? Hold { get; init; }

        internal Exception? Fault { get; set; }

        internal DateTimeOffset Clock { get; set; } = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        internal override DateTimeOffset UtcNow => Clock;

        internal override Task SendBlockedNotificationAsync(
            string storeId, RgbReplenishmentNoticeCause cause)
        {
            Interlocked.Increment(ref _sends);
            if (Fault != null) return Task.FromException(Fault);
            return Hold?.Task ?? Task.CompletedTask;
        }
    }

    [IntegrationFact]
    public async Task TheMigrationAddsThePricingMarkerColumn()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var at = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        await using (var seeded = harness.Factory.CreateContext())
        {
            seeded.RGBStoreNoticeStates.Add(new RGBStoreNoticeState
            {
                StoreId = "store-1",
                PricingCodeHasNoRuleNoticeSentAt = at
            });
            await seeded.SaveChangesAsync();
        }

        await using var reopened = harness.Factory.CreateContext();
        var row = await reopened.RGBStoreNoticeStates.SingleAsync(r => r.StoreId == "store-1");

        Assert.Equal(at, row.PricingCodeHasNoRuleNoticeSentAt);
        Assert.Null(row.NotAuthorizedNoticeSentAt);
    }

    // The payment handler raises this cause once per refused invoice, and a public checkout page can
    // drive that concurrently. The durable marker alone cannot de-duplicate it: every concurrent caller
    // reads `row == null`, so every one of them sends and one insert then violates the primary key.
    //
    // The release gate is load-bearing. A plain Task.WhenAll over 32 short DB round trips does NOT
    // contend — the first caller finishes and stamps the marker before most of the others start, so the
    // test passed with the attempt gate deleted. Every task must be parked and released together.
    [IntegrationFact]
    public async Task ConcurrentRaisesForOneStore_SendExactlyOneNotification()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CountingNoticeService(harness.Factory) { Hold = hold };
        const int callers = 32;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The barrier is fully asynchronous and no thread is ever blocked on it. A CountdownEvent.Wait
        // here occupies a thread-pool thread while these 32 Task.Run racers are queued for the SAME
        // pool, and the pool injects extra threads only slowly, so when other test classes run in
        // parallel the racers can miss any fixed timeout and fail the barrier assertion rather than the
        // property. That is a test-only flake with no production cause, and this shape removes it.
        var arrived = Enumerable.Range(0, callers)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();

        var racers = arrived.Select(gate => Task.Run(async () =>
        {
            gate.SetResult();
            await release.Task;
            await service.RaiseOncePerCauseAsync("store-1", Pricing);
        })).ToArray();

        await Task.WhenAll(arrived.Select(a => a.Task));
        release.SetResult();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (service.Sends == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(service.Sends >= 1, "no caller ever reached the send");
        await Task.Delay(TimeSpan.FromSeconds(3));
        var duringTheHold = service.Sends;

        hold.SetResult();
        await Task.WhenAll(racers);

        Assert.True(duringTheHold == 1,
            $"{duringTheHold} callers reached the send while the first was still parked inside it. "
            + "Concurrent callers all read a missing row, so the durable marker cannot de-duplicate on "
            + "its own; the in-process attempt gate is what has to admit exactly one of them.");
        Assert.True(service.Sends == 1,
            $"{service.Sends} notifications were sent for one store and one cause. Concurrent callers "
            + "all read a missing row, so the durable marker cannot de-duplicate on its own; the "
            + "in-process attempt gate is what has to admit exactly one of them.");

        await using var ctx = harness.Factory.CreateContext();
        var rows = await ctx.RGBStoreNoticeStates.Where(r => r.StoreId == "store-1").ToListAsync();
        Assert.Single(rows);
        Assert.NotNull(rows[0].PricingCodeHasNoRuleNoticeSentAt);
    }

    // Restart semantics: a fresh service instance has an empty in-process gate, so the durable marker is
    // the only thing standing between the merchant and a second notification for the same cause.
    [IntegrationFact]
    public async Task AFreshServiceInstance_HonoursTheDurableMarker()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var first = new CountingNoticeService(harness.Factory);
        await first.RaiseOncePerCauseAsync("store-1", Pricing);
        Assert.Equal(1, first.Sends);

        var second = new CountingNoticeService(harness.Factory);
        await second.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.True(second.Sends == 0,
            "a restarted process re-sent the pricing notice. The in-process gate is gone after a "
            + "restart, so RaiseOncePerCauseAsync must read the persisted marker before sending.");
    }

    // A send that fails must leave the cause fully retryable: no durable marker, and no in-process
    // AlreadyRaised sentinel. Stamping either one would turn a transient notification outage into a
    // permanently missing warning, which is the failure mode the whole once-per-cause design exists to
    // avoid making worse.
    [IntegrationFact]
    public async Task AFailedSend_StampsNoDurableMarkerAndStaysRetryable()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var failing = new CountingNoticeService(harness.Factory)
        {
            Fault = new InvalidOperationException("notification subsystem is down")
        };

        await failing.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.Equal(1, failing.Sends);

        await using (var ctx = harness.Factory.CreateContext())
        {
            var row = await ctx.RGBStoreNoticeStates
                .SingleOrDefaultAsync(r => r.StoreId == "store-1");
            Assert.True(row?.PricingCodeHasNoRuleNoticeSentAt == null,
                "a failed send stamped the durable marker, so the notice will never be raised again "
                + "even though the merchant was never told.");
        }

        var healthy = new CountingNoticeService(harness.Factory);
        await healthy.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.Equal(1, healthy.Sends);
        await using var reopened = harness.Factory.CreateContext();
        var stamped = await reopened.RGBStoreNoticeStates.SingleAsync(r => r.StoreId == "store-1");
        Assert.NotNull(stamped.PricingCodeHasNoRuleNoticeSentAt);
    }

    // The behavioural binding for "a failed send must not set the in-process AlreadyRaised sentinel".
    // The store already HAS an RGB_StoreNoticeState row — every store that ever got a NotAuthorized,
    // CapDisabled or ConfigOutOfBounds notice does — so the row-null branch is not taken, and a
    // MarkRaised placed anywhere before the send runs for this store. If it does, the failed send is
    // permanently swallowed: no durable marker is written, the sentinel is DateTimeOffset.MaxValue, and
    // the pricing notice never fires again for the life of the process. A source pin cannot bound this
    // property — the set of branch shapes that reach a pre-send stamp is open — so it is measured.
    [IntegrationFact]
    public async Task AFailedSendForAStoreWithAnExistingNoticeRow_StaysRetryableInProcess()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        await using (var seeded = harness.Factory.CreateContext())
        {
            seeded.RGBStoreNoticeStates.Add(new RGBStoreNoticeState
            {
                StoreId = "store-1",
                NotAuthorizedNoticeSentAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)
            });
            await seeded.SaveChangesAsync();
        }

        var service = new CountingNoticeService(harness.Factory)
        {
            Fault = new InvalidOperationException("notification subsystem is down")
        };

        await service.RaiseOncePerCauseAsync("store-1", Pricing);
        Assert.Equal(1, service.Sends);

        await using (var afterFailure = harness.Factory.CreateContext())
        {
            var row = await afterFailure.RGBStoreNoticeStates.SingleAsync(r => r.StoreId == "store-1");
            Assert.Null(row.PricingCodeHasNoRuleNoticeSentAt);
        }

        // Same service instance, so the in-process gate is the only thing that can block the retry.
        service.Fault = null;
        service.Clock += RgbNoticeAttemptGate.RetryAfterSendFailure;
        await service.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.True(service.Sends == 2,
            $"after the backoff elapsed the same service sent {service.Sends} notification(s) in total; "
            + "2 are expected. A failed send that marks the cause raised in-process suppresses it for "
            + "the whole process lifetime, and because no durable marker was written nothing else will "
            + "ever raise it either.");

        await using var ctx = harness.Factory.CreateContext();
        var stamped = await ctx.RGBStoreNoticeStates.SingleAsync(r => r.StoreId == "store-1");
        Assert.NotNull(stamped.PricingCodeHasNoRuleNoticeSentAt);
        Assert.NotNull(stamped.NotAuthorizedNoticeSentAt);
    }

    // R2's middle case, made deterministic. NotificationSender commits the notification in its own
    // context BEFORE this plugin saves its marker, so "sent but not recorded" is reachable. Treating it
    // as a failure would resend to a merchant who was already notified, once per retry window, forever.
    // The column is dropped while the send is held open, so the save that follows genuinely throws.
    [IntegrationFact]
    public async Task ANotificationThatCommittedButWasNotRecorded_IsNotResentByThisProcess()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CountingNoticeService(harness.Factory) { Hold = hold };

        var raising = service.RaiseOncePerCauseAsync("store-1", Pricing);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (service.Sends == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(1, service.Sends);

        await using (var surgery = harness.Factory.CreateContext())
        {
            await surgery.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"RGB_StoreNoticeState\" DROP COLUMN \"PricingCodeHasNoRuleNoticeSentAt\"");
        }

        hold.SetResult();
        await raising;

        // The column has to come BACK before the second attempt, or this test proves nothing: with it
        // still missing, the second attempt throws on the SELECT and never reaches the send, so it stays
        // green even when a save failure is misclassified as a send failure. Measured — that mutation
        // was GREEN until this restore was added.
        await using (var repair = harness.Factory.CreateContext())
        {
            await repair.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"RGB_StoreNoticeState\" ADD COLUMN \"PricingCodeHasNoRuleNoticeSentAt\" "
                + "timestamp with time zone NULL");
        }

        // Same instance, clock advanced well past the retry window: only the in-process state can
        // suppress a resend, because no durable marker was ever written.
        service.Clock += RgbNoticeAttemptGate.RetryAfterSendFailure + TimeSpan.FromMinutes(1);
        await service.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.True(service.Sends == 1,
            $"the notification was sent {service.Sends} times. The first one COMMITTED — only recording "
            + "it failed — so resending is a duplicate to a merchant who was already told. A save "
            + "failure must not be treated as a send failure.");
    }

    [IntegrationFact]
    public async Task EachCauseIsDeDuplicatedIndependently()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        var service = new CountingNoticeService(harness.Factory);
        await service.RaiseOncePerCauseAsync("store-1", Pricing);
        await service.RaiseOncePerCauseAsync("store-1", RgbReplenishmentNoticeCause.NotAuthorized);
        await service.RaiseOncePerCauseAsync("store-1", Pricing);

        Assert.Equal(2, service.Sends);

        await using var ctx = harness.Factory.CreateContext();
        var row = await ctx.RGBStoreNoticeStates.SingleAsync(r => r.StoreId == "store-1");
        Assert.NotNull(row.PricingCodeHasNoRuleNoticeSentAt);
        Assert.NotNull(row.NotAuthorizedNoticeSentAt);
        Assert.Null(row.CapDisabledNoticeSentAt);
    }
}
