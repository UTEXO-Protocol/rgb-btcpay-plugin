using System.Linq.Expressions;
using System.Diagnostics;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RGBInvoiceListener : IHostedService
{
    static readonly Newtonsoft.Json.JsonSerializer _blobSerializer = BlobSerializer.CreateSerializer().Serializer;
    readonly InvoiceRepository _invoices;
    readonly RGBPaymentMethodHandler _handler;
    readonly RGBWalletService _wallets;
    readonly RGBPluginDbContextFactory _db;
    readonly EventAggregator _events;
    readonly PaymentService _payments;
    readonly StoreRepository _stores;
    readonly RGBConfiguration _cfg;
    readonly ReplenishCooldownTracker _cooldowns;
    readonly RgbAutoReplenishmentAuthorizationStore _authorizations;
    readonly RgbReplenishmentNoticeService _notices;
    readonly ILogger<RGBInvoiceListener> _log;

    // The channel is a latency hint only; RGBInvoices + each wallet's rgb-lib database are the durable
    // source of settlement work. Overflow requests a full wallet sweep instead of retaining more memory.
    internal const int InvoiceQueueCapacity = 256;
    internal const int InvoiceDrainBatchSize = 64;
    internal const int DurableInvoicePageSize = 64;
    internal const int DurableInvoiceNewestSliceSize = 8;
    internal const int DurableInvoiceHotPageSize = 24;
    internal const int DurableInvoiceColdPageSize =
        DurableInvoicePageSize - DurableInvoiceNewestSliceSize - DurableInvoiceHotPageSize;
    internal const int DurableWalletPageSize = 64;
    internal const int DurableAssetPageSize = 64;
    internal static readonly TimeSpan InvoiceDrainBudget = TimeSpan.FromSeconds(1);
    readonly BoundedInvoiceWorkQueue _queue = new(InvoiceQueueCapacity);
    CompositeDisposable _subs = new();
    CancellationTokenSource? _cts;
    Task? _worker;

    const int PollSeconds = 10;
    // internal so ReplenishConfigurationTests can pin the invariant that the base cooldown exceeds it.
    internal const int UtxoCheckMinutes = 10;
    DateTimeOffset _lastUtxoCheck = DateTimeOffset.MinValue;

    public RGBInvoiceListener(IMemoryCache cache, InvoiceRepository invoices, RGBPaymentMethodHandler handler,
        RGBWalletService wallets, RGBPluginDbContextFactory db,
        EventAggregator events, PaymentService payments, StoreRepository stores, RGBConfiguration cfg,
        RgbAutoReplenishmentAuthorizationStore authorizations, RgbReplenishmentNoticeService notices,
        ILogger<RGBInvoiceListener> log)
    {
        _ = cache; // Kept in the public constructor for plugin/ABI compatibility; invoice entities are no longer cached.
        _invoices = invoices; _handler = handler; _wallets = wallets;
        _db = db; _events = events; _payments = payments; _stores = stores; _cfg = cfg; _log = log;
        _authorizations = authorizations; _notices = notices;
        _cooldowns = new ReplenishCooldownTracker(
            baseCooldown: TimeSpan.FromMinutes(_cfg.AutoUtxoCooldownMinutes),
            maxBackoff: TimeSpan.FromMinutes(_cfg.AutoUtxoMaxBackoffMinutes));
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Subscribe first. Events racing the backlog query can be duplicated, which is harmless; subscribing
        // second permanently missed invoices created between the query and the subscription. A durable sweep
        // replaces the old all-at-once backlog materialization, which itself was unbounded.
        // Subscribe synchronously: SubscribeAsync inserts an unbounded EventAggregator channel ahead
        // of this bounded hint queue. OnInvoice is non-blocking and overflow requests a durable sweep.
        _subs.Add(_events.Subscribe<InvoiceEvent>(OnInvoice));
        _queue.RequestRecovery();
        _worker = PollLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _subs.Dispose();
        _subs = new CompositeDisposable();
        if (_worker != null) await _worker;
    }

    void OnInvoice(InvoiceEvent e)
    {
        if (e.Name != InvoiceEvent.Created) return;
        if (ShouldEnqueue(e.Invoice)) _queue.TryWrite(e.Invoice.Id);
        return;
    }

    async Task PollLoop(CancellationToken ct)
    {
        var lastPoll = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var recovery = _queue.TryClaimRecovery();
                if (recovery.HasValue || DateTimeOffset.UtcNow - lastPoll > TimeSpan.FromSeconds(PollSeconds))
                {
                    var recovered = await RefreshAllWallets(ct);
                    if (recovery.HasValue) _queue.CompleteRecovery(recovery.Value, recovered);
                    lastPoll = DateTimeOffset.UtcNow;
                }
                if (DateTimeOffset.UtcNow - _lastUtxoCheck > TimeSpan.FromMinutes(UtxoCheckMinutes))
                {
                    await ReplenishUtxosAsync(ct);
                    _lastUtxoCheck = DateTimeOffset.UtcNow;
                }
                var drainClock = Stopwatch.StartNew();
                var drained = 0;
                while (drained < InvoiceDrainBatchSize
                       && drainClock.Elapsed < InvoiceDrainBudget
                       && _queue.TryDequeue(out var id))
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckSingleInvoice(id, ct);
                    drained++;
                }
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "poll loop hiccup");
                await Task.Delay(10000, ct);
            }
        }
    }

    async Task<bool> RefreshAllWallets(CancellationToken ct)
    {
        _log.LogInformation("RefreshAllWallets starting...");
        await using var ctx = _db.CreateContext();
        var allRecovered = true;
        string? walletCursor = null;
        while (true)
        {
            var walletPage = await ctx.RGBWallets.AsNoTracking()
                .Where(w => (w.IsActive || w.NeedsRecovery)
                            && (walletCursor == null || string.Compare(w.Id, walletCursor) > 0))
                .OrderBy(w => w.Id)
                .Take(DurableWalletPageSize)
                .ToListAsync(ct);
            if (walletPage.Count == 0) break;
            walletCursor = walletPage[^1].Id;
            foreach (var w in walletPage)
            {
                try
                {
                    _log.LogInformation("Refreshing wallet {WalletId}...", w.Id);
                    if (!await _wallets.RefreshWalletAsync(w.Id, ct))
                    {
                        allRecovered = false;
                        continue;
                    }
                    // Inactive rows are included solely so a staged send cannot become permanently
                    // undiscoverable. Do not settle invoices or replenish state for a disabled wallet.
                    if (!w.IsActive) continue;
                    if (!await CleanupExpiredTransfers(w, ct))
                    {
                        allRecovered = false;
                        continue;
                    }
                    _log.LogInformation("Wallet {WalletId} refreshed, processing transfers...", w.Id);
                    if (!await ProcessTransfers(w.Id, w.StoreId, ct)) allRecovered = false;
                    if (!await ProcessAssetDiscoveryInvoices(w.Id, ct)) allRecovered = false;
                }
                catch (Exception ex)
                {
                    allRecovered = false;
                    _log.LogWarning(ex, "Failed to refresh wallet {WalletId}", w.Id);
                }
            }
        }
        _log.LogInformation("RefreshAllWallets completed");
        return allRecovered;
    }

    async Task<bool> CleanupExpiredTransfers(RGBWallet wallet, CancellationToken ct)
    {
        try
        {
            return await _wallets.CleanupExpiredTransfersAsync(wallet.Id, wallet.Network, wallet.MasterFingerprint, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to cleanup expired transfers for wallet {WalletId}", wallet.Id);
            return false;
        }
    }

    async Task ReplenishUtxosAsync(CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        // Ids only: with no wallet entity in scope there is no stale snapshot for a later refactor to read.
        var walletIds = await ctx.RGBWallets.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct);
        _cooldowns.Prune(walletIds);

        foreach (var id in walletIds)
        {
            try
            {
                // Per wallet, not per sweep: a sweep spending minutes in rgb-lib per wallet would otherwise
                // judge later wallets' invoices against the sweep's start, counting rows that expired while
                // it ran. This instant is the DECISION clock — eligibility and the invoice predicate. The
                // cooldown stamps read the clock again at the moment they stamp, because the wallet's own
                // rgb-lib work (CreateColorableUtxosAsync blocks on the per-wallet send lock) can outlast the
                // cooldown itself, and stamping an already-elapsed instant would leave the wallet instantly eligible
                // again. Both drifts run in the permissive direction, the one this change exists to close.
                var now = DateTimeOffset.UtcNow;
                var nowUnix = now.ToUnixTimeSeconds();

                // Re-read per wallet: a concurrent send can quarantine, and an admin can deactivate, between
                // the query above and the decision below.
                var w = await ctx.RGBWallets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (w == null) continue;

                var store = await _stores.FindStore(w.StoreId);
                if (store == null) continue;

                var configs = store.GetPaymentMethodConfigs(onlyEnabled: true);
                var enabled = configs.TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok);
                // `tok is not null` is required: Roslyn does not carry TryGetValue's [MaybeNullWhen(false)]
                // state through the `enabled` local, so without it ToObject is a CS8602 under nullable-enable.
                var config = enabled && tok is not null
                    ? tok.ToObject<RGBPaymentMethodConfig>(_blobSerializer)
                    : null;

                var standingAuthorizationGranted =
                    await _authorizations.IsGrantedForWalletAsync(w.StoreId, w.Id, ct);
                var noticeCause = RgbReplenishmentNotice.Evaluate(
                    paymentMethodEnabled: enabled,
                    hasStoredConfig: config != null,
                    configValuesValid: config != null && RgbConfigBounds.ArePaymentMethodValuesValid(
                        config.UtxoCount, config.UtxoSize, config.MinConfirmations),
                    maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos,
                    standingAuthorizationGranted: standingAuthorizationGranted);
                if (noticeCause != RgbReplenishmentNoticeCause.None)
                {
                    await _notices.RaiseOncePerCauseAsync(w.StoreId, noticeCause, ct);
                    if (RgbReplenishmentNotice.LogsPerSweep(noticeCause))
                        _log.LogWarning("Wallet {WalletId}: {NoticeMessage}",
                            w.Id, RgbReplenishmentNotice.MessageFor(noticeCause));
                }

                // Validation on Greenfield writes cannot repair values persisted by an older vulnerable
                // build. Refuse those values again at the signing sink, before any rgb-lib or UTXO work.
                if (config != null && !RgbConfigBounds.ArePaymentMethodValuesValid(
                        config.UtxoCount, config.UtxoSize, config.MinConfirmations))
                {
                    _log.LogWarning(
                        "Wallet {WalletId}: refusing automatic UTXO creation from out-of-range stored RGB configuration",
                        w.Id);
                    continue;
                }

                var skip = EvaluateReplenishEligibility(
                    walletId: w.Id,
                    isActive: w.IsActive,
                    needsRecovery: w.NeedsRecovery,
                    maxAllocationsPerUtxo: w.MaxAllocationsPerUtxo,
                    paymentMethodEnabled: enabled,
                    // The wallet row's StoreId is authoritative. RGBPaymentMethodHandler uses the same
                    // mapping, so a replacement-style Greenfield PUT that omits walletId cannot disable
                    // either invoice creation or automatic replenishment.
                    configuredWalletId: w.Id,
                    now: now,
                    nextEligibleAt: _cooldowns.NextEligibleAt(w.Id));
                if (skip.HasValue)
                {
                    // Every refusal logs at Debug, per spec 3.6. An earlier revision warned on
                    // SkipInvalidWalletConfig, on the belief that it meant a store pointing at a different
                    // wallet — it does not; that is SkipWalletNotConfigured, which is also the ordinary state
                    // of every store that has not set RGB up. SkipInvalidWalletConfig is a non-positive
                    // MaxAllocationsPerUtxo, which RGBPluginMigrationRunner repairs at startup. Neither
                    // deserves a line every ten minutes forever.
                    _log.LogDebug("Wallet {WalletId}: skipping UTXO replenishment ({Outcome})", w.Id, skip.Value);
                    continue;
                }

                // Eligibility already refused a null config; this narrows the reference for the demand call
                // without a null-forgiving `!`, which would compile while still being able to throw.
                if (config is null) continue;

                var utxos = await _wallets.ListUnspentsAsync(w.Id, ct);
                var colorable = utxos.Where(u => u.Utxo.Colorable).ToList();
                var colorableCount = colorable.Count;
                var usedByColorings = colorable.Sum(u => u.RgbAllocations.Count);
                var activePendingInvoices = await ctx.RGBInvoices.CountAsync(
                    ActivePendingInvoicePredicate(w.Id, nowUnix), ct);

                var decision = EvaluateReplenishDemand(
                    colorableCount: colorableCount,
                    usedByColorings: usedByColorings,
                    activePendingInvoices: activePendingInvoices,
                    maxAllocationsPerUtxo: w.MaxAllocationsPerUtxo,
                    minFreeSlots: config.UtxoCount,
                    utxoSize: config.UtxoSize,
                    maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos,
                    standingAuthorizationGranted: standingAuthorizationGranted);

                if (decision.Outcome != ReplenishOutcome.Create)
                {
                    // Debug, per spec §3.6: the cap gate precedes the free-slots gate, so a healthy wallet
                    // sitting at the cap with ample free slots would log this on every eligible sweep,
                    // forever — and so would a deployment that sets the cap to 0 to disable automatic
                    // creation outright. (Reaching here stamps the cooldown, so it is roughly 48 times a day
                    // at the 30-minute default rather than once per 10-minute sweep; still noise, forever.)
                    // Diagnosing a stopped replenishment is what the Debug level is for.
                    _log.LogDebug(
                        "Wallet {WalletId}: {Outcome} ({Colorings} colorings + {Pending} active pending, {Colorable}/{Cap} colorable UTXOs)",
                        w.Id, decision.Outcome, usedByColorings, activePendingInvoices, colorableCount,
                        _cfg.MaxAutoColorableUtxos);
                    _cooldowns.RecordNoActionNeeded(w.Id, DateTimeOffset.UtcNow);
                    continue;
                }

                _log.LogInformation(
                    "Wallet {WalletId}: {Outcome} — requesting {Request} new colorable UTXOs ({Colorings} colorings + {Pending} active pending, {Colorable}/{Cap} standing)",
                    w.Id, decision.Outcome, decision.RequestCount, usedByColorings, activePendingInvoices,
                    colorableCount, _cfg.MaxAutoColorableUtxos);

                try
                {
                    await _wallets.CreateColorableUtxosAutomaticallyAsync(
                        walletId: w.Id,
                        count: decision.RequestCount,
                        size: decision.UtxoSize,
                        authorize: authorizationCt => RecheckAutomaticReplenishmentAuthorizationAsync(
                            w.Id, w.StoreId, config, decision.RequestCount, decision.UtxoSize,
                            authorizationCt),
                        ct: ct);
                    _cooldowns.RecordAttemptSucceeded(w.Id, DateTimeOffset.UtcNow);
                }
                catch (RgbAutomaticReplenishmentNotAuthorizedException ex)
                {
                    // State changed while this wallet waited for its send lock. This is an authorization
                    // refusal, not a failed signing attempt, so do not stamp success or failure cooldown.
                    _log.LogDebug(ex,
                        "Wallet {WalletId}: automatic UTXO replenishment authorization changed before signing",
                        w.Id);
                }
                catch (RgbWalletQuarantinedException ex)
                {
                    // Stamp nothing and do not rethrow, so this agrees exactly with the SkipQuarantined
                    // pre-filter above. A quarantine normally clears on the next refresh, so treating it as a
                    // failed attempt would replace a one-cycle wait with a 30-minute doubling backoff.
                    _log.LogDebug(ex, "Wallet {WalletId}: skipping UTXO replenishment (quarantined)", w.Id);
                }
                catch
                {
                    // Scoped to the creation call, so the failures the sweep does before it — the store
                    // lookup, the config parse, ListUnspentsAsync, CountAsync — reach the outer catch
                    // unstamped and a healthy wallet is not backed off by one of those. Note this DOES
                    // include failures raised inside CreateColorableUtxosAsync itself (it opens its own
                    // DbContext and waits on the per-wallet send lock), which is the intended reading: if
                    // the creation attempt did not succeed, the wallet waits longer before the next one.
                    _cooldowns.RecordAttemptFailed(w.Id, DateTimeOffset.UtcNow);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to replenish UTXOs for wallet {WalletId}", id);
            }
        }
    }

    async Task<bool> RecheckAutomaticReplenishmentAuthorizationAsync(
        string walletId,
        string expectedStoreId,
        RGBPaymentMethodConfig expectedConfig,
        int expectedRequestCount,
        int expectedUtxoSize,
        CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        // This callback runs after the per-wallet send lock and cross-process native lease are held.
        // Re-read both halves of demand here: a preceding send can change the colored UTXO set while
        // this attempt waits, and invoice rows can expire or settle during the same wait. Any changed
        // request is refused and left for the next sweep instead of signing a stale request.
        var utxos = await _wallets.ListUnspentsAsync(walletId, ct);
        var colorable = utxos.Where(u => u.Utxo.Colorable).ToList();
        var freshNowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var activePendingInvoices = await ctx.RGBInvoices.CountAsync(
            ActivePendingInvoicePredicate(walletId, freshNowUnix), ct);

        var current = await ctx.RGBWallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == walletId, ct);
        if (current == null) return false;
        var store = await _stores.FindStore(expectedStoreId);
        if (store == null) return false;
        if (store.Archived)
            _log.LogWarning(
                "Wallet {WalletId}: automatic UTXO replenishment is paused because store {StoreId} is archived; unarchive the store to resume it",
                walletId, expectedStoreId);
        var configs = store.GetPaymentMethodConfigs(onlyEnabled: true);
        var enabled = configs.TryGetValue(RGBPlugin.RGBPaymentMethodId, out var token);
        RGBPaymentMethodConfig? currentConfig = null;
        if (enabled && token is not null)
        {
            try { currentConfig = token.ToObject<RGBPaymentMethodConfig>(_blobSerializer); }
            catch { return false; }
        }
        if (!IsAutomaticReplenishmentAuthorized(
                current, expectedStoreId, enabled, store.Archived, currentConfig, expectedConfig)
            || currentConfig == null)
            return false;

        var standingAuthorizationGranted =
            await _authorizations.IsGrantedForWalletAsync(expectedStoreId, walletId, ct);

        var currentDecision = EvaluateReplenishDemand(
            colorableCount: colorable.Count,
            usedByColorings: colorable.Sum(u => u.RgbAllocations.Count),
            activePendingInvoices: activePendingInvoices,
            maxAllocationsPerUtxo: current.MaxAllocationsPerUtxo,
            minFreeSlots: currentConfig.UtxoCount,
            utxoSize: currentConfig.UtxoSize,
            maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos,
            standingAuthorizationGranted: standingAuthorizationGranted);

        return IsCurrentReplenishmentRequestAuthorized(
            currentDecision: currentDecision,
            expectedRequestCount: expectedRequestCount,
            expectedUtxoSize: expectedUtxoSize);
    }

    internal static bool IsAutomaticReplenishmentAuthorized(
        RGBWallet? current,
        string expectedStoreId,
        bool paymentMethodEnabled,
        bool storeArchived,
        RGBPaymentMethodConfig? currentConfig,
        RGBPaymentMethodConfig expectedConfig)
    {
        if (current == null
            || !current.IsActive
            || current.NeedsRecovery
            || current.MaxAllocationsPerUtxo <= 0
            || !RGBPaymentMethodHandler.WalletBelongsToStore(current.StoreId, expectedStoreId)
            || !paymentMethodEnabled
            || storeArchived
            || currentConfig == null
            || !RgbConfigBounds.ArePaymentMethodValuesValid(
                currentConfig.UtxoCount, currentConfig.UtxoSize, currentConfig.MinConfirmations))
            return false;

        // A settings edit that keeps RGB enabled can still invalidate the already-computed request.
        // Refuse this cycle and let the next sweep recompute count and size from the new configuration.
        return currentConfig.UtxoCount == expectedConfig.UtxoCount
            && currentConfig.UtxoSize == expectedConfig.UtxoSize
            && currentConfig.MinConfirmations == expectedConfig.MinConfirmations;
    }

    internal static bool IsCurrentReplenishmentRequestAuthorized(
        ReplenishDecision currentDecision, int expectedRequestCount, int expectedUtxoSize)
        => currentDecision.Outcome == ReplenishOutcome.Create
           && currentDecision.RequestCount == expectedRequestCount
           && currentDecision.UtxoSize == expectedUtxoSize;

    async Task<long> ResolveHotScanFloorUnixAsync(string storeId)
    {
        var storeMonitoringWindow = TimeSpan.Zero;
        try
        {
            var store = await _stores.FindStore(storeId);
            if (store != null) storeMonitoringWindow = store.GetStoreBlob().MonitoringExpiration;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Could not read the monitoring window of store {StoreId}; using the configured hot-scan window alone",
                storeId);
        }
        var window = ResolveHotScanWindow(
            storeMonitoringWindow, _cfg.CheckoutInvoiceHotScanWindowHours);
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)window.TotalSeconds;
    }

    internal static TimeSpan ResolveHotScanWindow(
        TimeSpan storeMonitoringWindow, int configuredWindowHours)
    {
        var configured = TimeSpan.FromHours(Math.Clamp(
            configuredWindowHours,
            RGBConfiguration.MinCheckoutInvoiceHotScanWindowHours,
            RGBConfiguration.MaxCheckoutInvoiceHotScanWindowHours));
        var monitoring = storeMonitoringWindow > TimeSpan.Zero ? storeMonitoringWindow : TimeSpan.Zero;
        var beyondMonitoring = monitoring
            + TimeSpan.FromHours(RGBConfiguration.CheckoutInvoiceMonitoringSafetyMarginHours);
        return configured > beyondMonitoring ? configured : beyondMonitoring;
    }

    internal static Expression<Func<RGBInvoice, bool>> CheckoutSettlementScanPredicate(string walletId)
        => i => i.WalletId == walletId
                && i.AssetId != null
                && (i.Status == RGBInvoiceStatus.Pending
                    || i.Status == RGBInvoiceStatus.WaitingConfirmations
                    || i.Status == RGBInvoiceStatus.Underpaid);

    internal static long MonitoringFloorUnix(long nowUnix)
        => nowUnix - (long)TimeSpan
            .FromHours(RGBConfiguration.CheckoutInvoiceMonitoringSafetyMarginHours).TotalSeconds;

    internal static Expression<Func<RGBInvoice, bool>> StillPayableCheckoutPredicate(
        long hotScanFloorUnix, long monitoringFloorUnix)
        => i => (i.MonitoringExpirationTimestamp != null
                    && i.MonitoringExpirationTimestamp > monitoringFloorUnix)
                || (i.MonitoringExpirationTimestamp == null
                    && i.ExpirationTimestamp != null
                    && i.ExpirationTimestamp > hotScanFloorUnix);

    internal static Expression<Func<RGBInvoice, bool>> BeyondPayableCheckoutPredicate(
        long hotScanFloorUnix, long monitoringFloorUnix)
        => i => (i.MonitoringExpirationTimestamp != null
                    && i.MonitoringExpirationTimestamp <= monitoringFloorUnix)
                || (i.MonitoringExpirationTimestamp == null
                    && (i.ExpirationTimestamp == null
                        || i.ExpirationTimestamp <= hotScanFloorUnix));

    internal static IQueryable<RGBInvoice> NewestCheckoutInvoiceSlice(IQueryable<RGBInvoice> scanSet)
        => scanSet
            .OrderByDescending(i => i.CreatedAt)
            .ThenByDescending(i => i.Id)
            .Take(DurableInvoiceNewestSliceSize);

    internal static IQueryable<RGBInvoice> HotCheckoutInvoicePage(
        IQueryable<RGBInvoice> scanSet, long hotScanFloorUnix, long monitoringFloorUnix, string? cursor)
        => AfterCursor(
                scanSet.Where(StillPayableCheckoutPredicate(hotScanFloorUnix, monitoringFloorUnix)),
                cursor)
            .OrderBy(i => i.Id)
            .Take(DurableInvoiceHotPageSize);

    internal static IQueryable<RGBInvoice> ColdCheckoutInvoicePage(
        IQueryable<RGBInvoice> scanSet, long hotScanFloorUnix, long monitoringFloorUnix, string? cursor)
        => AfterCursor(
                scanSet.Where(BeyondPayableCheckoutPredicate(hotScanFloorUnix, monitoringFloorUnix)),
                cursor)
            .OrderBy(i => i.Id)
            .Take(DurableInvoiceColdPageSize);

    static IQueryable<RGBInvoice> AfterCursor(IQueryable<RGBInvoice> tier, string? cursor)
        => string.IsNullOrEmpty(cursor)
            ? tier
            : tier.Where(i => string.Compare(i.Id, cursor) > 0);

    async Task<bool> ProcessTransfers(string walletId, string expectedStoreId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();

        var wallet = await ctx.RGBWallets.FindAsync(walletId);
        if (wallet == null) return true;

        if (!RGBPaymentMethodHandler.WalletBelongsToStore(wallet.StoreId, expectedStoreId))
        {
            _log.LogWarning("ProcessTransfers: wallet {WalletId} (store {WalletStoreId}) does not belong to expected store {ExpectedStoreId}; skipping",
                walletId, wallet.StoreId, expectedStoreId);
            return true;
        }

        var scanSet = ctx.RGBInvoices.Where(CheckoutSettlementScanPredicate(walletId));
        var hotScanFloorUnix = await ResolveHotScanFloorUnixAsync(expectedStoreId);
        var monitoringFloorUnix = MonitoringFloorUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var newest = await NewestCheckoutInvoiceSlice(scanSet).ToListAsync(ct);
        var hot = await HotCheckoutInvoicePage(
            scanSet, hotScanFloorUnix, monitoringFloorUnix, wallet.HotInvoiceScanCursor).ToListAsync(ct);
        var cold = await ColdCheckoutInvoicePage(
            scanSet, hotScanFloorUnix, monitoringFloorUnix, wallet.InvoiceScanCursor).ToListAsync(ct);
        var pending = newest.Concat(hot).Concat(cold)
            .DistinctBy(i => i.Id, StringComparer.Ordinal).ToList();
        _log.LogInformation(
            "ProcessTransfers: {Count} pending/waiting invoices for wallet {WalletId} ({Newest} newest, {Hot} still-payable, {Cold} from the cold tail)",
            pending.Count, walletId, newest.Count, hot.Count, cold.Count);
        if (pending.Count == 0)
        {
            if (wallet.HotInvoiceScanCursor != null || wallet.InvoiceScanCursor != null)
            {
                wallet.HotInvoiceScanCursor = null;
                wallet.InvoiceScanCursor = null;
                await ctx.SaveChangesAsync(ct);
            }
            return true;
        }
        wallet.HotInvoiceScanCursor = DurableInvoiceScan.NextCursor(
            hot.Select(i => i.Id).ToList(), DurableInvoiceHotPageSize);
        wallet.InvoiceScanCursor = DurableInvoiceScan.NextCursor(
            cold.Select(i => i.Id).ToList(), DurableInvoiceColdPageSize);

        var recipientIdsByAsset = pending
            .Where(i => !string.IsNullOrEmpty(i.AssetId) && !string.IsNullOrEmpty(i.RecipientId))
            .GroupBy(i => i.AssetId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyCollection<string>)g.Select(i => i.RecipientId)
                    .Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        _log.LogInformation("ProcessTransfers: Checking {Count} bounded asset/recipient groups",
            recipientIdsByAsset.Count);
        if (recipientIdsByAsset.Count == 0)
        {
            await ctx.SaveChangesAsync(ct);
            return true;
        }

        var pageSucceeded = true;
        var incomingTransfers = new List<(RgbTransfer Transfer, string AssetId)>();
        foreach (var (aid, recipientIds) in recipientIdsByAsset)
        {
            try
            {
                var matches = await _wallets.GetIncomingTransfersForRecipientsAsync(
                    walletId, recipientIds, aid, ct);
                incomingTransfers.AddRange(matches
                    .Where(m => m.Transfer.Kind is 1 or 2 && m.Transfer.Status is 2 or 3)
                    .Select(m => (m.Transfer, m.AssetId)));
            }
            catch (Exception ex)
            {
                pageSucceeded = false;
                _log.LogWarning(ex, "Failed to get transfers for asset {AssetId}", aid);
            }
        }
        _log.LogInformation("ProcessTransfers: Found {Count} incoming transfers to process", incomingTransfers.Count);

        foreach (var inv in pending)
        {
            var matchingTransfers = incomingTransfers
                .Where(t => t.Transfer.RecipientId == inv.RecipientId && IsAssetMatch(inv.AssetId, t.AssetId))
                .Select(t => t.Transfer)
                .ToList();
            if (matchingTransfers.Count == 0) continue;

            var settledTransfers = matchingTransfers.Where(t => t.Status == 3).ToList();
            var result = EvaluateInvoiceState(inv, matchingTransfers);
            if (result.Decision == SettlementDecision.RejectZeroAmount)
            {
                var latestIdx = settledTransfers.OrderByDescending(t => t.Idx).First().Idx;
                _log.LogCritical("Settled transfers for invoice {Id} sum to zero — cannot verify payment. Manual review required.", inv.Id);
                _events.Publish(new RgbAmountVerificationFailedEvent(inv.BtcPayInvoiceId ?? inv.Id, inv.WalletId, latestIdx));
            }
            else if (result.NewStatus.HasValue)
            {
                var registrationFailed = false;
                var unregisterable = false;
                if (result.PaymentStatus.HasValue && !string.IsNullOrEmpty(inv.BtcPayInvoiceId))
                {
                    foreach (var t in result.PaymentsToRecord)
                    {
                        try
                        {
                            var registration = await RecordOrUpdatePayment(
                                inv, t, result.PaymentStatus.Value, wallet.StoreId, ct);
                            if (registration == PaymentRegistration.Unregisterable)
                            {
                                unregisterable = true;
                            }
                            else if (registration == PaymentRegistration.Failed)
                            {
                                registrationFailed = true;
                                pageSucceeded = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            // WHY caught rather than propagated: one invoice's registration failure must
                            // not abort the sweep for every other invoice in this wallet. WHY it sets the
                            // flag: the status advance below would otherwise commit Settled and close the
                            // only door through which this payment can ever be retried.
                            _log.LogWarning(ex, "Failed to record payment for invoice {Id} transfer {Idx}", inv.BtcPayInvoiceId, t.Idx);
                            registrationFailed = true;
                            pageSucceeded = false;
                        }
                    }
                }

                if (unregisterable)
                {
                    inv.Status = RGBInvoiceStatus.Failed;
                    inv.Txid = result.Txid;
                    inv.ReceivedAmount = result.ReceivedAmount;
                    _log.LogCritical(
                        "invoice {Id} marked Failed: the BTCPay invoice cannot register this RGB payment; the asset arrived (txid {Txid}, amount {Amount}); before crediting anything by hand, check the BTCPay invoice for a payment already recorded against it, which a legacy prompt can leave standing and unadvanced",
                        inv.Id, inv.Txid, inv.ReceivedAmount);
                    continue;
                }

                // The condition is CALLED, not inlined, so it can be tested as a pure function and
                // pinned as a call site.
                if (!ShouldCommitAdvance(result.NewStatus, registrationFailed))
                {
                    // LogCritical, not LogWarning, and deliberately repeated every sweep. A held invoice
                    // is a paid customer whose invoice will not settle — the same customer-visible
                    // symptom as the bug this change fixes, and a warning logged once per poll is not an
                    // alarm. A refusal known to be deterministic no longer reaches here: it classifies
                    // Unregisterable and closes the row terminally. This matches the existing
                    // zero-amount escalation in this same method.
                    _log.LogCritical("invoice {Id} held at {Status}: payment registration failed, will be retried on a later sweep",
                        inv.Id, inv.Status);
                    continue;
                }

                // The entity writes come AFTER registration so a blocked advance leaves the row
                // entirely untouched — no half-written Txid on a status that did not move.
                inv.Status = result.NewStatus.Value;
                inv.Txid = result.Txid;
                inv.ReceivedAmount = result.ReceivedAmount;
                if (result.NewStatus == RGBInvoiceStatus.Settled)
                    inv.SettledAt = DateTimeOffset.UtcNow;

                _log.LogInformation("invoice {Id} → {Status} (amount={Amount})", inv.Id, result.NewStatus, result.ReceivedAmount);
            }
        }
        await ctx.SaveChangesAsync(ct);
        return pageSucceeded;
    }

    async Task<bool> ProcessAssetDiscoveryInvoices(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var wallet = await ctx.RGBWallets.FindAsync([walletId], ct);
        if (wallet == null) return true;
        var pending = await ctx.RGBInvoices
            .Where(i => i.WalletId == walletId
                        && i.AssetId == null
                        && i.BtcPayInvoiceId == null
                        && (i.Status == RGBInvoiceStatus.Pending
                            || i.Status == RGBInvoiceStatus.WaitingConfirmations)
                        && (wallet.DiscoveryScanCursor == null
                            || string.Compare(i.Id, wallet.DiscoveryScanCursor) > 0))
            .OrderBy(i => i.Id)
            .Take(DurableInvoicePageSize)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            if (wallet.DiscoveryScanCursor != null || wallet.DiscoveryAssetPage != 0)
            {
                wallet.DiscoveryScanCursor = null;
                wallet.DiscoveryAssetPage = 0;
                await ctx.SaveChangesAsync(ct);
            }
            return true;
        }
        var nextInvoiceCursor = DurableInvoiceScan.NextCursor(
            pending.Select(i => i.Id).ToList(), DurableInvoicePageSize);

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Expiry sweep FIRST — runs independent of any rgb-lib calls. If asset listing
        // fails below, expired invoices still get marked Expired on this iteration.
        var stillPending = new List<RGBInvoice>(pending.Count);
        foreach (var inv in pending)
        {
            if (inv.ExpirationTimestamp.HasValue && nowUnix > inv.ExpirationTimestamp.Value)
            {
                inv.Status = RGBInvoiceStatus.Expired;
                continue;
            }
            stillPending.Add(inv);
        }

        if (stillPending.Count == 0)
        {
            wallet.DiscoveryScanCursor = nextInvoiceCursor;
            wallet.DiscoveryAssetPage = 0;
            await ctx.SaveChangesAsync(ct);
            return true;
        }

        List<RgbMatchedTransfer> matches;
        try
        {
            matches = await _wallets.GetIncomingTransfersForRecipientsAsync(
                walletId,
                stillPending.Select(i => i.RecipientId).Distinct(StringComparer.Ordinal).ToList(),
                assetId: null,
                ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Bounded transfer lookup failed during asset-discovery scan for wallet {WalletId}",
                walletId);
            await ctx.SaveChangesAsync(ct);
            return false;
        }

        foreach (var inv in stillPending)
        {
            foreach (var matched in matches.Where(
                         m => string.Equals(m.Transfer.RecipientId, inv.RecipientId,
                             StringComparison.Ordinal)))
            {
                var match = EvaluateAssetDiscoveryMatch(
                    inv, matched.AssetId, [matched.Transfer]);
                if (match == null) continue;

                if (match.IsZeroAmount)
                {
                    _log.LogCritical("Asset-discovery invoice {Id} matched zero-amount transfer — refusing to register asset {AssetId}", inv.Id, matched.AssetId);
                    _events.Publish(new RgbAmountVerificationFailedEvent(inv.Id, walletId, match.Transfer.Idx));
                    break;
                }

                if (match.NewStatus == RGBInvoiceStatus.Failed)
                {
                    inv.Status = RGBInvoiceStatus.Failed;
                    inv.Txid = match.Transfer.Txid;
                    break;
                }

                // Safe to register: positive amount AND not Failed. Let any DB exception
                // propagate to the outer try in RefreshAllWallets so we retry next poll
                // WITHOUT having advanced the invoice state.
                await _wallets.RegisterSingleAssetIfNewAsync(walletId, matched.Asset, ct);

                inv.ReceivedAssetId = matched.AssetId;
                inv.ReceivedAmount = match.ReceivedAmount;
                inv.Txid = match.Transfer.Txid;
                inv.Status = match.NewStatus;
                if (match.NewStatus == RGBInvoiceStatus.Settled)
                    inv.SettledAt = DateTimeOffset.UtcNow;

                _log.LogInformation("Asset-discovery invoice {Id} -> {Status} (asset={AssetId}, amount={Amount})",
                    inv.Id, match.NewStatus, matched.AssetId, match.ReceivedAmount);

                break;
            }
        }

        wallet.DiscoveryAssetPage = 0;
        wallet.DiscoveryScanCursor = nextInvoiceCursor;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    internal enum PaymentRegistration
    {
        Recorded,
        Declined,
        Unregisterable,
        Failed
    }

    // WHY a separate function: ProcessTransfers cannot be driven in a unit test (it opens a DB
    // context), so the decision that governs whether a payment can be lost must live somewhere a
    // test can reach.
    // WHY every advance and not just Settled: a failed WaitingConfirmations registration used to commit
    // anyway, and the next sweep then found invoice.Status != Pending, so EvaluateInvoiceState returned
    // no payment work and no status — the attempt was never retried and, because that branch is skipped
    // entirely, never alarmed either. Holding the row at its previous status is what puts it back in
    // front of the same branch on the next sweep, which is the retry.
    internal static bool ShouldCommitAdvance(RGBInvoiceStatus? newStatus, bool registrationFailed)
        => !(registrationFailed && newStatus.HasValue);

    // WHY bounded to Settled: an underpaid invoice never leaves the sweep filter, so an unbounded
    // republish would emit one event per poll for the life of the invoice.
    internal static bool ShouldRepublishOnAlreadyRecorded(BTCPayServer.Data.PaymentStatus target)
        => target == BTCPayServer.Data.PaymentStatus.Settled;

    // WHY re-query rather than trust the null: AddPayment returns null for a failed insert and for a
    // duplicate alike, and treating the failure as success is the exact defect this gate closes.
    internal static PaymentRegistration ClassifyNullAddPayment(
        InvoiceEntity? after, PaymentPrompt? prompt, string paymentId)
    {
        if (after is null) return PaymentRegistration.Declined;
        if (prompt is null) return PaymentRegistration.Unregisterable;
        return after.GetPayments(false).Any(p => p.Id == paymentId)
            ? PaymentRegistration.Recorded
            : PaymentRegistration.Failed;
    }

    async Task<PaymentRegistration> RecordOrUpdatePayment(RGBInvoice rgbInv, RgbTransfer tx, BTCPayServer.Data.PaymentStatus targetStatus, string expectedStoreId, CancellationToken ct)
    {
        var invoiceEntity = await _invoices.GetInvoice(rgbInv.BtcPayInvoiceId);
        if (invoiceEntity == null)
        {
            _log.LogWarning("BTCPay invoice {Id} not found", rgbInv.BtcPayInvoiceId);
            return PaymentRegistration.Declined;
        }

        if (!RGBPaymentMethodHandler.WalletBelongsToStore(invoiceEntity.StoreId, expectedStoreId))
        {
            _log.LogCritical("BTCPay invoice {Id} (store {InvoiceStoreId}) does not belong to wallet store {ExpectedStoreId}; refusing to credit a received RGB payment",
                rgbInv.BtcPayInvoiceId, invoiceEntity.StoreId, expectedStoreId);
            return PaymentRegistration.Unregisterable;
        }

        var prompt = invoiceEntity.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId);
        if (prompt == null)
        {
            _log.LogCritical("No RGB payment prompt on invoice {Id}; refusing to credit a received RGB payment",
                rgbInv.BtcPayInvoiceId);
            return PaymentRegistration.Unregisterable;
        }

        var details = _handler.ParsePaymentPromptDetails(prompt.Details);
        var identity = ClassifyPromptPricingIdentity(rgbInv, details, out var paymentCurrency);
        if (identity == PaymentRegistration.Unregisterable)
        {
            _log.LogCritical(
                "RGB payment for invoice {InvoiceId} has no securely contract-bound current pricing code; refusing registration",
                rgbInv.BtcPayInvoiceId);
            return identity;
        }

        var receivedAmount = tx.Amount;
        var divisibility = details.AssetPrecision;
        var amountDecimal = divisibility > 0
            ? receivedAmount / (decimal)Math.Pow(10, divisibility)
            : receivedAmount;
        var paymentId = $"rgb:{rgbInv.RecipientId}:{tx.Idx}";

        var existingPayment = invoiceEntity.GetPayments(false)
            .FirstOrDefault(p => p.Id == paymentId);

        if (existingPayment != null)
        {
            if (existingPayment.Status != targetStatus)
            {
                existingPayment.Status = targetStatus;
                await _payments.UpdatePayments(new List<PaymentEntity> { existingPayment });
                _events.Publish(new Events.InvoiceNeedUpdateEvent(rgbInv.BtcPayInvoiceId));
                _log.LogInformation("Updated payment {PaymentId} to {Status} for invoice {InvoiceId}",
                    paymentId, targetStatus, rgbInv.BtcPayInvoiceId);
            }
            // WHY republish when nothing changed: if a previous attempt inserted the payment and then
            // lost its event, BTCPay never re-derived the invoice. Asking it to re-derive is a no-op
            // when nothing was lost and a repair when something was.
            else if (ShouldRepublishOnAlreadyRecorded(targetStatus))
            {
                _events.Publish(new Events.InvoiceNeedUpdateEvent(rgbInv.BtcPayInvoiceId));
            }

            return PaymentRegistration.Recorded;
        }
        else
        {
            var paymentData = new BTCPayServer.Data.PaymentData
            {
                Id = paymentId,
                Created = DateTimeOffset.UtcNow,
                Status = targetStatus,
                Currency = paymentCurrency,
                InvoiceDataId = rgbInv.BtcPayInvoiceId,
                Amount = amountDecimal,
                PaymentMethodId = RGBPlugin.RGBPaymentMethodId.ToString()
            }.Set(invoiceEntity, _handler, new RGBPaymentData
            {
                RecipientId = rgbInv.RecipientId,
                Txid = tx.Txid,
                AssetId = rgbInv.AssetId,
                Amount = receivedAmount,
                TransferIdx = tx.Idx
            });

            var payment = await _payments.AddPayment(paymentData);
            if (payment == null)
            {
                // AddPayment returns null for a missing invoice, a missing prompt/handler AND any
                // DbUpdateException — so the null cannot be read as "already added". Classify from
                // observed state instead, against a freshly fetched invoice.
                var after = await _invoices.GetInvoice(rgbInv.BtcPayInvoiceId);
                var afterPrompt = after?.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId);
                var classified = ClassifyNullAddPayment(after, afterPrompt, paymentId);

                if (classified == PaymentRegistration.Recorded)
                    _events.Publish(new Events.InvoiceNeedUpdateEvent(rgbInv.BtcPayInvoiceId));

                _log.LogWarning("AddPayment returned null for {PaymentId} on invoice {InvoiceId}; classified as {Outcome}",
                    paymentId, rgbInv.BtcPayInvoiceId, classified);
                return classified;
            }

            invoiceEntity = await _invoices.GetInvoice(rgbInv.BtcPayInvoiceId);
            if (invoiceEntity != null)
                _events.Publish(new InvoiceEvent(invoiceEntity, InvoiceEvent.ReceivedPayment) { Payment = payment });

            _log.LogInformation("Recorded {Status} payment {PaymentId} for invoice {InvoiceId}: {Amount} {Ticker}",
                targetStatus, paymentId, rgbInv.BtcPayInvoiceId, amountDecimal, details.AssetTicker);

            return PaymentRegistration.Recorded;
        }
    }

    async Task CheckSingleInvoice(string invoiceId, CancellationToken ct)
    {
        try
        {
            var inv = await _invoices.GetInvoice(invoiceId);
            if (inv == null) return;
            var prompt = inv.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId);
            if (prompt == null) return;
            // An event is only a latency hint. State interpretation must go through RefreshAllWallets,
            // where refresh + expiry cleanup both acquired this wallet's lock. Calling ProcessTransfers
            // here let a hint bypass that gate after a busy wallet was skipped and could record an expired
            // status-1 transfer as Processing.
            _queue.RequestRecovery();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to check invoice {InvoiceId}", invoiceId);
        }
    }

    // The subscription is server-wide, so without this every invoice created anywhere on the instance
    // costs a queue slot, a GetInvoice round-trip and a cached InvoiceEntity before being discarded as
    // irrelevant. Deliberately broader than the check CheckSingleInvoice applies: it asks only whether an
    // RGB prompt exists, because a lazily-activated invoice is published with a prompt whose Details is
    // still null and gains them later without republishing Created, and the queue carries the id alone —
    // nothing has cached such an invoice yet, so the drain re-fetches and processes it if activation landed
    // first. What the width buys is exactly that: the window between Created and this one entry draining.
    // It does not rescue an activation that lands after the drain — no path re-enqueues within the process,
    // so that invoice waits for the sweep or a restart, and a wallet whose post-refresh durability flush
    // throws is never swept. The window is still worth having, and testing Details here would give it up
    // for nothing. Fail open here; fail closed there.
    internal static bool ShouldEnqueue(InvoiceEntity inv) =>
        inv.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId) != null;

    internal static SettlementDecision EvaluateTransfer(int transferStatus, long transferAmount, long? invoiceAmount)
    {
        if (transferStatus == 2)
            return transferAmount > 0 ? SettlementDecision.TransitionWaiting : SettlementDecision.TransitionWaitingNoPayment;

        if (transferStatus == 3)
        {
            if (transferAmount <= 0)
                return SettlementDecision.RejectZeroAmount;

            var isFullyPaid = invoiceAmount == null || transferAmount >= invoiceAmount.Value;
            return isFullyPaid ? SettlementDecision.RecordSettled : SettlementDecision.RecordUnderpaid;
        }

        return SettlementDecision.TransitionWaitingNoPayment;
    }

    // Order is load-bearing: the cheap gates must precede the caller's ListUnspentsAsync, so a wallet whose
    // store never enabled RGB costs no rgb-lib work at all.
    internal static ReplenishOutcome? EvaluateReplenishEligibility(
        string walletId, bool isActive, bool needsRecovery, int maxAllocationsPerUtxo,
        bool paymentMethodEnabled, string? configuredWalletId,
        DateTimeOffset now, DateTimeOffset? nextEligibleAt)
    {
        if (!isActive) return ReplenishOutcome.SkipWalletNotConfigured;
        if (nextEligibleAt.HasValue && now < nextEligibleAt.Value) return ReplenishOutcome.SkipCooldown;
        if (!paymentMethodEnabled) return ReplenishOutcome.SkipPaymentMethodDisabled;
        if (!string.Equals(configuredWalletId, walletId, StringComparison.Ordinal))
            return ReplenishOutcome.SkipWalletNotConfigured;
        if (needsRecovery) return ReplenishOutcome.SkipQuarantined;
        // WHY refuse rather than clamp: the wallet row is written only through ResolveAllocationsPerUtxo,
        // which clamps to [1,50], so a non-positive value means a corrupt or hand-edited row. Repairing it
        // here would turn a corrupt row into a valid signing request.
        if (maxAllocationsPerUtxo <= 0) return ReplenishOutcome.SkipInvalidWalletConfig;
        return null;
    }

    internal static ReplenishDecision EvaluateReplenishDemand(
        int colorableCount, int usedByColorings, int activePendingInvoices,
        int maxAllocationsPerUtxo, int minFreeSlots, int utxoSize, int maxAutoColorableUtxos,
        bool standingAuthorizationGranted)
    {
        // Must precede the Math.Clamp below, whose min > max throws ArgumentException.
        if (!standingAuthorizationGranted || maxAutoColorableUtxos <= 0
            || colorableCount >= maxAutoColorableUtxos)
            return new ReplenishDecision(ReplenishOutcome.SkipCapReached, 0, utxoSize);

        var totalSlots = (long)colorableCount * maxAllocationsPerUtxo;
        var usedSlots = (long)usedByColorings + activePendingInvoices;
        var freeSlots = Math.Max(0L, totalSlots - usedSlots);
        if (freeSlots >= minFreeSlots)
            return new ReplenishDecision(ReplenishOutcome.SkipEnoughFreeSlots, 0, utxoSize);

        var needed = (long)Math.Ceiling((double)(minFreeSlots - freeSlots) / maxAllocationsPerUtxo);
        var headroomBelowCap = (long)maxAutoColorableUtxos - colorableCount;
        var request = (int)Math.Clamp(needed, 1L, headroomBelowCap);
        return new ReplenishDecision(ReplenishOutcome.Create, request, utxoSize);
    }

    // A row whose expiry has passed no longer reserves anything: rgb-lib fails the blind receive and releases
    // the UTXO, while our row stays Pending forever because only the asset-discovery sweep ever marks rows
    // Expired. Counting those rows is what lets anyone who can mint an invoice inflate demand permanently.
    // A null expiry is rgb-lib's own omission on a checkout-path row, so it is inactive for the same reason.
    internal static Expression<Func<RGBInvoice, bool>> ActivePendingInvoicePredicate(string walletId, long nowUnix)
        => i => i.WalletId == walletId
                && i.Status == RGBInvoiceStatus.Pending
                && i.ExpirationTimestamp != null
                && i.ExpirationTimestamp > nowUnix;

    internal static string ResolvePaymentCurrency(RGBPromptDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.AssetId)
            || !RgbPricingCode.IsCurrentPricingCode(details.PricingCode))
            throw new FormatException("RGB prompt does not contain a current contract-bound pricing identity");

        string expected;
        try
        {
            expected = RgbPricingCode.For(details.AssetId);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException("RGB prompt contains an invalid contract id", ex);
        }

        if (!string.Equals(details.PricingCode, expected, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("RGB prompt pricing identity does not match its contract id");

        return expected;
    }

    internal static PaymentRegistration ClassifyPromptPricingIdentity(
        RGBInvoice rgbInvoice,
        RGBPromptDetails details,
        out string paymentCurrency)
    {
        paymentCurrency = "";
        if (!IsAssetMatch(rgbInvoice.AssetId, details.AssetId ?? ""))
            return PaymentRegistration.Unregisterable;

        try
        {
            paymentCurrency = ResolvePaymentCurrency(details);
            return PaymentRegistration.Recorded;
        }
        catch (FormatException)
        {
            return PaymentRegistration.Unregisterable;
        }
    }

    internal static bool IsAssetMatch(string? invoiceAssetId, string transferAssetId)
    {
        return !string.IsNullOrEmpty(invoiceAssetId) && transferAssetId == invoiceAssetId;
    }

    internal static long SaturatingSum(IEnumerable<long> values)
    {
        long total = 0;
        foreach (var v in values)
        {
            if (v <= 0) { total += v; continue; }
            if (total > long.MaxValue - v) return long.MaxValue;
            total += v;
        }
        return total;
    }

    internal record InvoiceProcessingResult(
        RGBInvoiceStatus? NewStatus,
        long? ReceivedAmount,
        string? Txid,
        IReadOnlyList<RgbTransfer> PaymentsToRecord,
        BTCPayServer.Data.PaymentStatus? PaymentStatus,
        SettlementDecision? Decision);

    internal static InvoiceProcessingResult EvaluateInvoiceState(
        RGBInvoice invoice, IReadOnlyList<RgbTransfer> matchingTransfers)
    {
        var settled = matchingTransfers.Where(t => t.Status == 3).ToList();
        var waiting = matchingTransfers.Where(t => t.Status == 2).ToList();

        if (settled.Count > 0 && invoice.Status is not RGBInvoiceStatus.Settled)
        {
            var cumulative = SaturatingSum(settled.Select(t => t.Amount));
            var latest = settled.OrderByDescending(t => t.Idx).First();
            var decision = EvaluateTransfer(3, cumulative, invoice.Amount);
            return decision switch
            {
                SettlementDecision.RecordSettled => new InvoiceProcessingResult(
                    RGBInvoiceStatus.Settled, cumulative, latest.Txid,
                    settled.OrderBy(t => t.Idx).ToList(),
                    BTCPayServer.Data.PaymentStatus.Settled, decision),
                SettlementDecision.RecordUnderpaid => new InvoiceProcessingResult(
                    RGBInvoiceStatus.Underpaid, cumulative, latest.Txid,
                    settled.OrderBy(t => t.Idx).ToList(),
                    BTCPayServer.Data.PaymentStatus.Processing, decision),
                _ => new InvoiceProcessingResult(null, null, null, Array.Empty<RgbTransfer>(), null, decision)
            };
        }

        if (waiting.Count > 0 && invoice.Status == RGBInvoiceStatus.Pending)
        {
            var first = waiting.First();
            return new InvoiceProcessingResult(
                RGBInvoiceStatus.WaitingConfirmations,
                first.Amount > 0 ? first.Amount : 0,
                first.Txid,
                first.Amount > 0 ? new[] { first } : Array.Empty<RgbTransfer>(),
                first.Amount > 0 ? BTCPayServer.Data.PaymentStatus.Processing : null,
                first.Amount > 0 ? SettlementDecision.TransitionWaiting : SettlementDecision.TransitionWaitingNoPayment);
        }

        return new InvoiceProcessingResult(null, null, null, Array.Empty<RgbTransfer>(), null, null);
    }

    internal record AssetDiscoveryMatch(
        string AssetId,
        RgbTransfer Transfer,
        RGBInvoiceStatus NewStatus,
        long ReceivedAmount,
        string? Txid,
        bool ShouldRecordPayment,
        bool IsZeroAmount);

    internal static AssetDiscoveryMatch? EvaluateAssetDiscoveryMatch(
        RGBInvoice invoice, string candidateAssetId, IReadOnlyList<RgbTransfer> transfersForAsset)
    {
        // Discriminator (defense-in-depth — also enforced in the LINQ filter at the
        // ProcessAssetDiscoveryInvoices call site). Asset-discovery invoices have BOTH
        // AssetId IS NULL AND BtcPayInvoiceId IS NULL. Refuse to match anything else,
        // even if a future bug lets a payment-method row through with AssetId=null —
        // otherwise we regress audit finding C3 (settling without amount verification).
        if (!string.IsNullOrEmpty(invoice.AssetId)) return null;
        if (!string.IsNullOrEmpty(invoice.BtcPayInvoiceId)) return null;
        if (string.IsNullOrEmpty(invoice.RecipientId)) return null;

        var matching = transfersForAsset
            .Where(t => t.RecipientId == invoice.RecipientId && t.Kind is 1 or 2 && t.Status is 1 or 2 or 3 or 4)
            .OrderBy(t => t.Idx)
            .ToList();
        if (matching.Count == 0) return null;

        var first = matching[0];

        // beta.30 creates an incoming WaitingCounterparty row (status 1) with the invoice itself.
        // It is not evidence that a sender has acted, so it must leave the invoice Pending.
        if (first.Status == 1) return null;

        if (first.Status == 4)
        {
            return new AssetDiscoveryMatch(candidateAssetId, first,
                RGBInvoiceStatus.Failed, 0, first.Txid,
                ShouldRecordPayment: false, IsZeroAmount: false);
        }

        // Amount <= 0 BEFORE distinguishing status — zero-amount transfers at ANY status
        // must not advance state and must not trigger asset registration.
        if (first.Amount <= 0)
        {
            return new AssetDiscoveryMatch(candidateAssetId, first,
                invoice.Status, 0, first.Txid,
                ShouldRecordPayment: false, IsZeroAmount: true);
        }

        if (first.Status == 2)
        {
            return new AssetDiscoveryMatch(candidateAssetId, first,
                RGBInvoiceStatus.WaitingConfirmations, first.Amount, first.Txid,
                ShouldRecordPayment: false, IsZeroAmount: false);
        }

        return new AssetDiscoveryMatch(candidateAssetId, first,
            RGBInvoiceStatus.Settled, first.Amount, first.Txid,
            ShouldRecordPayment: false, IsZeroAmount: false);
    }
}

internal enum ReplenishOutcome
{
    Create,
    SkipCooldown,
    SkipPaymentMethodDisabled,
    SkipWalletNotConfigured,
    SkipQuarantined,
    SkipInvalidWalletConfig,
    SkipCapReached,
    SkipEnoughFreeSlots
}

internal record ReplenishDecision(ReplenishOutcome Outcome, int RequestCount, int UtxoSize);

internal enum SettlementDecision
{
    TransitionWaiting,
    TransitionWaitingNoPayment,
    RecordSettled,
    RecordUnderpaid,
    RejectZeroAmount
}

public class RgbAmountVerificationFailedEvent
{
    public string InvoiceId { get; }
    public string WalletId { get; }
    public int TransferIdx { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    public RgbAmountVerificationFailedEvent(string invoiceId, string walletId, int transferIdx)
    {
        InvoiceId = invoiceId;
        WalletId = walletId;
        TransferIdx = transferIdx;
    }
}

public class RgbAssetDiscoveredEvent
{
    public string WalletId { get; }
    public string AssetId { get; }
    public string Ticker { get; }
    public string Name { get; }
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    public RgbAssetDiscoveredEvent(string walletId, string assetId, string ticker, string name)
    {
        WalletId = walletId;
        AssetId = assetId;
        Ticker = ticker;
        Name = name;
    }
}
