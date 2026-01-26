using System.Text.Json;
using System.Threading.Channels;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RGBInvoiceListener : IHostedService
{
    readonly IMemoryCache _cache;
    readonly InvoiceRepository _invoices;
    readonly RGBPaymentMethodHandler _handler;
    readonly RGBWalletService _wallets;
    readonly RGBPluginDbContextFactory _db;
    readonly ApplicationDbContextFactory _appDb;
    readonly EventAggregator _events;
    readonly PaymentService _payments;
    readonly ILogger<RGBInvoiceListener> _log;

    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    CompositeDisposable _subs = new();
    CancellationTokenSource? _cts;
    Task? _worker;

    const int PollSeconds = 10;

    public RGBInvoiceListener(IMemoryCache cache, InvoiceRepository invoices, RGBPaymentMethodHandler handler,
        RGBWalletService wallets, RGBPluginDbContextFactory db, ApplicationDbContextFactory appDb,
        EventAggregator events, PaymentService payments, ILogger<RGBInvoiceListener> log)
    {
        _cache = cache; _invoices = invoices; _handler = handler; _wallets = wallets;
        _db = db; _appDb = appDb; _events = events; _payments = payments; _log = log;
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
                _log.LogInformation("Wallet {WalletId} refreshed, processing transfers...", w.Id);
                await ProcessSettledTransfers(w.Id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to refresh wallet {WalletId}", w.Id);
            }
        }
        _log.LogInformation("RefreshAllWallets completed");
    }

    async Task ProcessSettledTransfers(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var pending = await ctx.RGBInvoices.Where(i => i.WalletId == walletId && i.Status == RGBInvoiceStatus.Pending).ToListAsync(ct);
        _log.LogInformation("ProcessSettledTransfers: {Count} pending invoices for wallet {WalletId}", pending.Count, walletId);
        if (pending.Count == 0) return;

        var assetIds = pending.Where(i => !string.IsNullOrEmpty(i.AssetId)).Select(i => i.AssetId!).Distinct().ToList();
        if (pending.Any(i => string.IsNullOrEmpty(i.AssetId)))
        {
            try
            {
                var allAssets = await _wallets.ListAssetsAsync(walletId);
                _log.LogInformation("ProcessSettledTransfers: Listed {Count} assets for wildcard invoices", allAssets.Count);
                assetIds = assetIds.Union(allAssets.Select(a => a.AssetId)).ToList();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Failed to list assets for wallet {WalletId}", walletId);
            }
        }
        _log.LogInformation("ProcessSettledTransfers: Checking {Count} asset IDs", assetIds.Count);
        if (assetIds.Count == 0) return;
        
        var settled = new List<RgbTransfer>();
        foreach (var aid in assetIds)
        {
            _log.LogInformation("ProcessSettledTransfers: Fetching transfers for asset {AssetId}", aid);
            try
            {
                var transfers = await _wallets.GetTransfersAsync(walletId, aid);
                _log.LogInformation("ProcessSettledTransfers: Asset {AssetId} has {Count} transfers", aid.Length > 30 ? aid[..30] : aid, transfers.Count);
                foreach (var t in transfers)
                {
                    _log.LogInformation("  Transfer idx={Idx} status={Status} kind={Kind} recipientId={RecipientId}", t.Idx, t.Status, t.Kind, t.RecipientId ?? "null");
                }
                settled.AddRange(transfers.Where(t => t.Status == 2 && t.Kind is 1 or 2));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to get transfers for asset {AssetId}", aid);
            }
        }
        _log.LogInformation("ProcessSettledTransfers: Found {Count} settled transfers to process", settled.Count);
        
        foreach (var tx in settled.GroupBy(t => t.Idx).Select(g => g.First()))
        {
            if (string.IsNullOrEmpty(tx.RecipientId)) continue;
            var inv = pending.Find(i => i.RecipientId == tx.RecipientId);
            if (inv == null) continue;

            inv.Status = RGBInvoiceStatus.Settled;
            inv.SettledAt = DateTimeOffset.UtcNow;
            inv.Txid = tx.Txid;
            inv.ReceivedAmount = tx.Amount > 0 ? tx.Amount : inv.Amount ?? 0;

            if (!string.IsNullOrEmpty(inv.BtcPayInvoiceId))
            {
                try
                {
                    await RecordPayment(inv, tx, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to record payment for invoice {InvoiceId}", inv.BtcPayInvoiceId);
                }
            }
            _log.LogInformation("settled {Id}", inv.Id);
        }
        await ctx.SaveChangesAsync(ct);
    }

    async Task RecordPayment(RGBInvoice rgbInv, RgbTransfer tx, CancellationToken ct)
    {
        await using var appCtx = _appDb.CreateContext();
        
        var invoiceData = await appCtx.Invoices
            .FirstOrDefaultAsync(i => i.Id == rgbInv.BtcPayInvoiceId, ct);
        if (invoiceData == null)
        {
            _log.LogWarning("BTCPay invoice {Id} not found", rgbInv.BtcPayInvoiceId);
            return;
        }

        var paymentId = $"rgb:{rgbInv.RecipientId}:{tx.Idx}";
        var existingPayment = await appCtx.Payments
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.InvoiceDataId == rgbInv.BtcPayInvoiceId, ct);
        if (existingPayment != null)
        {
            _log.LogDebug("Payment {Id} already exists", paymentId);
            return;
        }

        if (invoiceData.Status == "New" || invoiceData.Status == "Processing")
        {
            invoiceData.Status = "Settled";
            _log.LogInformation("Updated BTCPay invoice {Id} status to Settled", rgbInv.BtcPayInvoiceId);
        }

        await appCtx.SaveChangesAsync(ct);
        _log.LogInformation("Invoice {InvoiceId} marked as Settled", rgbInv.BtcPayInvoiceId);
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
            await ProcessSettledTransfers(_handler.ParsePaymentPromptDetails(prompt.Details).WalletId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to check invoice {InvoiceId}", invoiceId);
        }
    }

    static DateTimeOffset ComputeExpiry(InvoiceEntity inv)
    {
        var left = inv.ExpirationTime - DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow + (left > TimeSpan.FromMinutes(5) ? left : TimeSpan.FromMinutes(5));
    }
}
