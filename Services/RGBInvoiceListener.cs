using System.Threading.Channels;
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
    readonly IMemoryCache _cache;
    readonly InvoiceRepository _invoices;
    readonly RGBPaymentMethodHandler _handler;
    readonly RGBWalletService _wallets;
    readonly RGBPluginDbContextFactory _db;
    readonly EventAggregator _events;
    readonly PaymentService _payments;
    readonly StoreRepository _stores;
    readonly ILogger<RGBInvoiceListener> _log;

    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    CompositeDisposable _subs = new();
    CancellationTokenSource? _cts;
    Task? _worker;

    const int PollSeconds = 10;
    const int UtxoCheckMinutes = 10;
    DateTimeOffset _lastUtxoCheck = DateTimeOffset.MinValue;

    public RGBInvoiceListener(IMemoryCache cache, InvoiceRepository invoices, RGBPaymentMethodHandler handler,
        RGBWalletService wallets, RGBPluginDbContextFactory db,
        EventAggregator events, PaymentService payments, StoreRepository stores, ILogger<RGBInvoiceListener> log)
    {
        _cache = cache; _invoices = invoices; _handler = handler; _wallets = wallets;
        _db = db; _events = events; _payments = payments; _stores = stores; _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await EnqueuePendingInvoices(ct);
        _subs.Add(_events.SubscribeAsync<InvoiceEvent>(OnInvoice));
        _worker = PollLoop(_cts.Token);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _subs.Dispose();
        _subs = new CompositeDisposable();
        if (_worker != null) await _worker;
    }

    Task OnInvoice(InvoiceEvent e)
    {
        if (e.Name != InvoiceEvent.Created) return Task.CompletedTask;
        _cache.Remove($"rgb:inv:{e.Invoice.Id}");
        _queue.Writer.TryWrite(e.Invoice.Id);
        return Task.CompletedTask;
    }

    async Task EnqueuePendingInvoices(CancellationToken ct)
    {
        var pending = await _invoices.GetMonitoredInvoices(RGBPlugin.RGBPaymentMethodId, ct);
        foreach (var inv in pending)
        {
            if (inv.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId)?.Details == null) continue;
            _queue.Writer.TryWrite(inv.Id);
            _cache.Set($"rgb:inv:{inv.Id}", inv, ComputeExpiry(inv));
        }
        _log.LogDebug("queued {N} pending rgb invoices", pending.Length);
    }

    async Task PollLoop(CancellationToken ct)
    {
        var lastPoll = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastPoll > TimeSpan.FromSeconds(PollSeconds))
                {
                    await RefreshAllWallets(ct);
                    lastPoll = DateTimeOffset.UtcNow;
                }
                if (DateTimeOffset.UtcNow - _lastUtxoCheck > TimeSpan.FromMinutes(UtxoCheckMinutes))
                {
                    await ReplenishUtxosAsync(ct);
                    _lastUtxoCheck = DateTimeOffset.UtcNow;
                }
                while (_queue.Reader.TryRead(out var id))
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckSingleInvoice(id, ct);
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

    async Task RefreshAllWallets(CancellationToken ct)
    {
        _log.LogInformation("RefreshAllWallets starting...");
        await using var ctx = _db.CreateContext();
        var wallets = await ctx.RGBWallets.Where(w => w.IsActive).ToListAsync(ct);
        _log.LogInformation("Found {Count} active RGB wallets", wallets.Count);
        foreach (var w in wallets)
        {
            try
            {
                _log.LogInformation("Refreshing wallet {WalletId}...", w.Id);
                await _wallets.RefreshWalletAsync(w.Id);
                await CleanupExpiredTransfers(w, ct);
                _log.LogInformation("Wallet {WalletId} refreshed, processing transfers...", w.Id);
                await ProcessTransfers(w.Id, w.StoreId, ct);
                await ProcessAssetDiscoveryInvoices(w.Id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to refresh wallet {WalletId}", w.Id);
            }
        }
        _log.LogInformation("RefreshAllWallets completed");
    }

    async Task CleanupExpiredTransfers(RGBWallet wallet, CancellationToken ct)
    {
        try
        {
            await _wallets.CleanupExpiredTransfersAsync(wallet.Id, wallet.Network, wallet.MasterFingerprint, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to cleanup expired transfers for wallet {WalletId}", wallet.Id);
        }
    }

    async Task ReplenishUtxosAsync(CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var wallets = await ctx.RGBWallets.Where(w => w.IsActive).ToListAsync(ct);

        foreach (var w in wallets)
        {
            try
            {
                var store = await _stores.FindStore(w.StoreId);
                if (store == null) continue;

                var config = store.GetPaymentMethodConfigs().TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)
                    ? tok.ToObject<RGBPaymentMethodConfig>(_blobSerializer) : null;
                var minFreeSlots = config?.UtxoCount ?? 4;
                var utxoSize = config?.UtxoSize ?? 1000;

                var maxAlloc = w.MaxAllocationsPerUtxo;
                var utxos = await _wallets.ListUnspentsAsync(w.Id, ct);
                var colorable = utxos.Where(u => u.Utxo.Colorable).ToList();
                var totalSlots = colorable.Count * maxAlloc;
                var usedByColorings = colorable.Sum(u => u.RgbAllocations.Count);
                var pendingInvoices = await ctx.RGBInvoices.CountAsync(
                    i => i.WalletId == w.Id && i.Status == RGBInvoiceStatus.Pending, ct);
                var usedSlots = usedByColorings + pendingInvoices;
                var freeSlots = Math.Max(0, totalSlots - usedSlots);

                if (freeSlots >= minFreeSlots)
                {
                    _log.LogDebug("Wallet {WalletId} has {FreeSlots} free slots ({Colorings} colorings + {Pending} pending invoices using {Used}/{Total} slots), skipping",
                        w.Id, freeSlots, usedByColorings, pendingInvoices, usedSlots, totalSlots);
                    continue;
                }

                var newUtxosNeeded = (int)Math.Ceiling((double)(minFreeSlots - freeSlots) / maxAlloc);
                var requestCount = newUtxosNeeded + colorable.Count;
                _log.LogInformation("Wallet {WalletId}: {FreeSlots} free slots ({Colorings} colorings + {Pending} pending, {Used}/{Total} slots). Need {New} new UTXOs, requesting {Request} total",
                    w.Id, freeSlots, usedByColorings, pendingInvoices, usedSlots, totalSlots, newUtxosNeeded, requestCount);
                await _wallets.CreateColorableUtxosAsync(w.Id, requestCount, utxoSize, ct);
                _log.LogInformation("Wallet {WalletId}: requested {Request} total UTXOs (expected ~{New} new)",
                    w.Id, requestCount, newUtxosNeeded);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to replenish UTXOs for wallet {WalletId}", w.Id);
            }
        }
    }

    async Task ProcessTransfers(string walletId, string expectedStoreId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();

        var wallet = await ctx.RGBWallets.FindAsync(walletId);
        if (wallet == null) return;

        if (!RGBPaymentMethodHandler.WalletBelongsToStore(wallet.StoreId, expectedStoreId))
        {
            _log.LogWarning("ProcessTransfers: wallet {WalletId} (store {WalletStoreId}) does not belong to expected store {ExpectedStoreId}; skipping",
                walletId, wallet.StoreId, expectedStoreId);
            return;
        }

        var allInvoices = await ctx.RGBInvoices.Where(i => i.WalletId == walletId).ToListAsync(ct);
        _log.LogInformation("ProcessTransfers: total={Total} invoices for wallet {WalletId}, statuses: {Statuses}",
            allInvoices.Count, walletId, string.Join(",", allInvoices.Select(i => $"{i.Id[..8]}={i.Status}")));
        var pending = allInvoices.Where(i => i.Status is RGBInvoiceStatus.Pending or RGBInvoiceStatus.WaitingConfirmations or RGBInvoiceStatus.Underpaid).ToList();
        _log.LogInformation("ProcessTransfers: {Count} pending/waiting invoices for wallet {WalletId}", pending.Count, walletId);
        if (pending.Count == 0) return;

        var assetIds = pending.Where(i => !string.IsNullOrEmpty(i.AssetId)).Select(i => i.AssetId!).Distinct().ToList();
        _log.LogInformation("ProcessTransfers: Checking {Count} asset IDs", assetIds.Count);
        if (assetIds.Count == 0) return;

        var incomingTransfers = new List<(RgbTransfer Transfer, string AssetId)>();
        foreach (var aid in assetIds)
        {
            _log.LogInformation("ProcessTransfers: Fetching transfers for asset {AssetId}", aid);
            try
            {
                var transfers = await _wallets.GetTransfersAsync(walletId, aid);
                _log.LogInformation("ProcessTransfers: Asset {AssetId} has {Count} transfers",
                    aid.Length > 30 ? aid[..30] : aid, transfers.Count);
                foreach (var t in transfers)
                {
                    _log.LogInformation("  Transfer idx={Idx} status={Status} kind={Kind} recipientId={RecipientId}",
                        t.Idx, t.Status, t.Kind, t.RecipientId ?? "null");
                }
                incomingTransfers.AddRange(transfers.Where(t => t.Kind is 1 or 2 && t.Status is 1 or 2 or 3).Select(t => (t, aid)));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to get transfers for asset {AssetId}", aid);
            }
        }
        _log.LogInformation("ProcessTransfers: Found {Count} incoming transfers to process", incomingTransfers.Count);

        var dedupedTransfers = incomingTransfers.GroupBy(t => (t.AssetId, t.Transfer.Idx)).Select(g => g.First()).ToList();

        foreach (var inv in pending)
        {
            var matchingTransfers = dedupedTransfers
                .Where(t => t.Transfer.RecipientId == inv.RecipientId && IsAssetMatch(inv.AssetId, t.AssetId))
                .Select(t => t.Transfer)
                .ToList();
            if (matchingTransfers.Count == 0) continue;

            var settledTransfers = matchingTransfers.Where(t => t.Status == 3).ToList();
            var waitingTransfers = matchingTransfers.Where(t => t.Status is 1 or 2).ToList();

            var result = EvaluateInvoiceState(inv, matchingTransfers);
            if (result.Decision == SettlementDecision.RejectZeroAmount)
            {
                var latestIdx = settledTransfers.OrderByDescending(t => t.Idx).First().Idx;
                _log.LogCritical("Settled transfers for invoice {Id} sum to zero — cannot verify payment. Manual review required.", inv.Id);
                _events.Publish(new RgbAmountVerificationFailedEvent(inv.BtcPayInvoiceId ?? inv.Id, inv.WalletId, latestIdx));
            }
            else if (result.NewStatus.HasValue)
            {
                inv.Status = result.NewStatus.Value;
                inv.Txid = result.Txid;
                inv.ReceivedAmount = result.ReceivedAmount;
                if (result.NewStatus == RGBInvoiceStatus.Settled)
                    inv.SettledAt = DateTimeOffset.UtcNow;
                if (result.PaymentStatus.HasValue && !string.IsNullOrEmpty(inv.BtcPayInvoiceId))
                {
                    foreach (var t in result.PaymentsToRecord)
                    {
                        try { await RecordOrUpdatePayment(inv, t, result.PaymentStatus.Value, wallet.StoreId, ct); }
                        catch (Exception ex) { _log.LogWarning(ex, "Failed to record payment for invoice {Id} transfer {Idx}", inv.BtcPayInvoiceId, t.Idx); }
                    }
                }
                _log.LogInformation("invoice {Id} → {Status} (amount={Amount})", inv.Id, result.NewStatus, result.ReceivedAmount);
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    async Task ProcessAssetDiscoveryInvoices(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var pending = await ctx.RGBInvoices
            .Where(i => i.WalletId == walletId
                        && i.AssetId == null
                        && i.BtcPayInvoiceId == null
                        && (i.Status == RGBInvoiceStatus.Pending
                            || i.Status == RGBInvoiceStatus.WaitingConfirmations))
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var anyChanged = false;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Expiry sweep FIRST — runs independent of any rgb-lib calls. If asset listing
        // fails below, expired invoices still get marked Expired on this iteration.
        var stillPending = new List<RGBInvoice>(pending.Count);
        foreach (var inv in pending)
        {
            if (inv.ExpirationTimestamp.HasValue && nowUnix > inv.ExpirationTimestamp.Value)
            {
                inv.Status = RGBInvoiceStatus.Expired;
                anyChanged = true;
                continue;
            }
            stillPending.Add(inv);
        }

        if (stillPending.Count == 0)
        {
            if (anyChanged) await ctx.SaveChangesAsync(ct);
            return;
        }

        List<RgbAsset> assets;
        try { assets = await _wallets.ListAssetsRawAsync(walletId, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListAssetsRawAsync failed during asset-discovery scan for wallet {WalletId}", walletId);
            if (anyChanged) await ctx.SaveChangesAsync(ct);
            return;
        }
        if (assets.Count == 0)
        {
            if (anyChanged) await ctx.SaveChangesAsync(ct);
            return;
        }

        // Prefetch transfers per asset ONCE per scan: with N pending invoices and M assets,
        // the prior structure made N×M rgb-lib calls every poll. Build the per-asset
        // transfer list up front and let the per-invoice loop evaluate against the cache.
        var transfersByAsset = new Dictionary<string, List<RgbTransfer>>(assets.Count);
        foreach (var asset in assets)
        {
            try
            {
                transfersByAsset[asset.AssetId] = await _wallets.GetTransfersAsync(walletId, asset.AssetId, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "GetTransfersAsync failed for asset {AssetId} during discovery", asset.AssetId);
            }
        }

        foreach (var inv in stillPending)
        {
            foreach (var asset in assets)
            {
                if (!transfersByAsset.TryGetValue(asset.AssetId, out var transfers)) continue;

                var match = EvaluateAssetDiscoveryMatch(inv, asset.AssetId, transfers);
                if (match == null) continue;

                if (match.IsZeroAmount)
                {
                    _log.LogCritical("Asset-discovery invoice {Id} matched zero-amount transfer — refusing to register asset {AssetId}", inv.Id, asset.AssetId);
                    _events.Publish(new RgbAmountVerificationFailedEvent(inv.Id, walletId, match.Transfer.Idx));
                    break;
                }

                if (match.NewStatus == RGBInvoiceStatus.Failed)
                {
                    inv.Status = RGBInvoiceStatus.Failed;
                    inv.Txid = match.Transfer.Txid;
                    anyChanged = true;
                    break;
                }

                // Safe to register: positive amount AND not Failed. Let any DB exception
                // propagate to the outer try in RefreshAllWallets so we retry next poll
                // WITHOUT having advanced the invoice state.
                await _wallets.RegisterSingleAssetIfNewAsync(walletId, asset, ct);

                inv.ReceivedAssetId = asset.AssetId;
                inv.ReceivedAmount = match.ReceivedAmount;
                inv.Txid = match.Transfer.Txid;
                inv.Status = match.NewStatus;
                if (match.NewStatus == RGBInvoiceStatus.Settled)
                    inv.SettledAt = DateTimeOffset.UtcNow;

                _log.LogInformation("Asset-discovery invoice {Id} -> {Status} (asset={AssetId}, amount={Amount})",
                    inv.Id, match.NewStatus, asset.AssetId, match.ReceivedAmount);

                anyChanged = true;
                break;
            }
        }

        if (anyChanged) await ctx.SaveChangesAsync(ct);
    }

    async Task RecordOrUpdatePayment(RGBInvoice rgbInv, RgbTransfer tx, BTCPayServer.Data.PaymentStatus targetStatus, string expectedStoreId, CancellationToken ct)
    {
        var invoiceEntity = await _invoices.GetInvoice(rgbInv.BtcPayInvoiceId);
        if (invoiceEntity == null)
        {
            _log.LogWarning("BTCPay invoice {Id} not found", rgbInv.BtcPayInvoiceId);
            return;
        }

        if (!RGBPaymentMethodHandler.WalletBelongsToStore(invoiceEntity.StoreId, expectedStoreId))
        {
            _log.LogWarning("BTCPay invoice {Id} (store {InvoiceStoreId}) does not belong to wallet store {ExpectedStoreId}; skipping payment record",
                rgbInv.BtcPayInvoiceId, invoiceEntity.StoreId, expectedStoreId);
            return;
        }

        var prompt = invoiceEntity.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId);
        if (prompt == null)
        {
            _log.LogWarning("No RGB payment prompt on invoice {Id}", rgbInv.BtcPayInvoiceId);
            return;
        }

        var details = _handler.ParsePaymentPromptDetails(prompt.Details);
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
        }
        else
        {
            var paymentData = new BTCPayServer.Data.PaymentData
            {
                Id = paymentId,
                Created = DateTimeOffset.UtcNow,
                Status = targetStatus,
                Currency = details.AssetTicker ?? "RGB",
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
            if (payment != null)
            {
                invoiceEntity = await _invoices.GetInvoice(rgbInv.BtcPayInvoiceId);
                if (invoiceEntity != null)
                    _events.Publish(new InvoiceEvent(invoiceEntity, InvoiceEvent.ReceivedPayment) { Payment = payment });

                _log.LogInformation("Recorded {Status} payment {PaymentId} for invoice {InvoiceId}: {Amount} {Ticker}",
                    targetStatus, paymentId, rgbInv.BtcPayInvoiceId, amountDecimal, details.AssetTicker);
            }
        }
    }

    async Task CheckSingleInvoice(string invoiceId, CancellationToken ct)
    {
        try
        {
            var inv = await _cache.GetOrCreateAsync($"rgb:inv:{invoiceId}", async e => {
                var i = await _invoices.GetInvoice(invoiceId);
                if (i != null) e.AbsoluteExpiration = ComputeExpiry(i);
                return i;
            });
            if (inv == null) return;
            var prompt = inv.GetPaymentPrompt(RGBPlugin.RGBPaymentMethodId);
            if (prompt?.Details == null) return;
            await ProcessTransfers(_handler.ParsePaymentPromptDetails(prompt.Details).WalletId, inv.StoreId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to check invoice {InvoiceId}", invoiceId);
        }
    }

    internal static SettlementDecision EvaluateTransfer(int transferStatus, long transferAmount, long? invoiceAmount)
    {
        if (transferStatus is 1 or 2)
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
        var waiting = matchingTransfers.Where(t => t.Status is 1 or 2).ToList();

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

    static DateTimeOffset ComputeExpiry(InvoiceEntity inv)
    {
        var left = inv.ExpirationTime - DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow + (left > TimeSpan.FromMinutes(5) ? left : TimeSpan.FromMinutes(5));
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

        if (first.Status is 1 or 2)
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
