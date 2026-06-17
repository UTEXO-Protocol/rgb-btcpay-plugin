using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodHandler : IPaymentMethodHandler
{
    readonly RGBWalletService _wallets;
    readonly RGBPluginDbContextFactory _db;
    readonly RateFetcher _rateFetcher;
    readonly DefaultRulesCollection _defaultRules;
    readonly ILogger<RGBPaymentMethodHandler> _log;

    public RGBPaymentMethodHandler(
        RGBWalletService wallets,
        RGBPluginDbContextFactory db,
        RateFetcher rateFetcher,
        DefaultRulesCollection defaultRules,
        ILogger<RGBPaymentMethodHandler> log)
    {
        _wallets = wallets;
        _db = db;
        _rateFetcher = rateFetcher;
        _defaultRules = defaultRules;
        _log = log;
    }

    public PaymentMethodId PaymentMethodId => RGBPlugin.RGBPaymentMethodId;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public async Task ConfigurePrompt(PaymentMethodContext ctx)
    {
        if (!ctx.Store.GetPaymentMethodConfigs().TryGetValue(PaymentMethodId, out var configToken))
            throw new PaymentMethodUnavailableException("RGB not configured for this store");

        var config = ParsePaymentMethodConfig(configToken);
        
        var wallet = await _wallets.GetWalletAsync(config.WalletId);
        if (wallet == null)
            throw new PaymentMethodUnavailableException("RGB wallet missing");

        if (!WalletBelongsToStore(wallet.StoreId, ctx.Store.Id))
            throw new PaymentMethodUnavailableException("RGB wallet does not belong to this store");

        if (string.IsNullOrEmpty(config.DefaultAssetId))
            throw new PaymentMethodUnavailableException(
                "Select a default RGB asset in store Settings to accept payments");

        await using var dbContext = _db.CreateContext();
        var asset = await dbContext.RGBAssets.FirstOrDefaultAsync(
            a => a.WalletId == config.WalletId && a.AssetId == config.DefaultAssetId);
        if (asset == null)
            throw new PaymentMethodUnavailableException(
                $"Configured asset {config.DefaultAssetId[..Math.Min(20, config.DefaultAssetId.Length)]}... not found in wallet");

        var assetId = asset.AssetId;
        var ticker = asset.Ticker ?? "RGB";
        var name = asset.Name ?? "RGB Asset";
        var precision = asset.Precision;

        var invoiceCurrency = ctx.InvoiceEntity.Currency;
        var invoicePrice = ctx.InvoiceEntity.Price;
        
        var (rate, rateSource) = await TryFetchRateAsync(ticker, invoiceCurrency, ctx.Store, config.AllowOneToOneRateFallback);
        if (precision > 18)
            throw new PaymentMethodUnavailableException(
                $"Asset precision {precision} exceeds maximum supported (18)");
        var multiplier = (decimal)Math.Pow(10, precision);
        var unitsDecimal = invoicePrice / rate * multiplier;
        if (unitsDecimal > long.MaxValue)
            throw new PaymentMethodUnavailableException(
                $"Calculated amount exceeds maximum ({unitsDecimal:N0} units)");
        var units = invoicePrice > 0 ? (long)Math.Ceiling(unitsDecimal) : 1L;
        
        _log.LogInformation("RGB invoice: {Price} {Currency} → {Units} {Ticker} (rate: {Rate} from {Source})", 
            invoicePrice, invoiceCurrency, units, ticker, rate, rateSource);

        var expiration = ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow;
        var invoice = await _wallets.CreateInvoiceAsync(config.WalletId, assetId, units, expiration, ctx.InvoiceEntity.Id, config.MinConfirmations);
        
        ctx.Prompt.Currency = ticker;
        ctx.Prompt.Divisibility = precision;
        
        ctx.InvoiceEntity.Rates[ticker] = rate;

        ctx.Prompt.Destination = invoice.Invoice;
        ctx.Prompt.PaymentMethodFee = 0m;
        ctx.TrackedDestinations.Add(invoice.RecipientId);
        
        ctx.Prompt.Details = JObject.FromObject(new RGBPromptDetails
        {
            WalletId = config.WalletId,
            RgbInvoiceId = invoice.Id,
            RecipientId = invoice.RecipientId,
            AssetId = assetId,
            AssetTicker = ticker,
            AssetName = name,
            AssetPrecision = precision,
            AmountInAssetUnits = units
        }, Serializer);
    }

    public Task BeforeFetchingRates(PaymentMethodContext ctx)
    {
        ctx.Prompt.Currency = ctx.InvoiceEntity.Currency;
        ctx.Prompt.Divisibility = 0;
        ctx.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    public RGBPromptDetails ParsePaymentPromptDetails(JToken d) =>
        d.ToObject<RGBPromptDetails>(Serializer) ?? throw new FormatException("bad prompt");
    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken d) => ParsePaymentPromptDetails(d);

    public RGBPaymentMethodConfig ParsePaymentMethodConfig(JToken c) =>
        c.ToObject<RGBPaymentMethodConfig>(Serializer) ?? throw new FormatException("bad config");
    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken c) => ParsePaymentMethodConfig(c);

    public RGBPaymentData ParsePaymentDetails(JToken d) =>
        d.ToObject<RGBPaymentData>(Serializer) ?? throw new FormatException("bad payment");
    object IPaymentMethodHandler.ParsePaymentDetails(JToken d) => ParsePaymentDetails(d);

    public static bool WalletBelongsToStore(string? walletStoreId, string? expectedStoreId) =>
        !string.IsNullOrEmpty(walletStoreId)
        && !string.IsNullOrEmpty(expectedStoreId)
        && walletStoreId == expectedStoreId;

    public void StripDetailsForNonOwner(object details)
    {
        if (details is RGBPromptDetails d)
        {
            d.WalletId = "";
            d.RgbInvoiceId = "";
            d.RecipientId = "";
        }
    }

    async Task<(decimal Rate, string Source)> TryFetchRateAsync(string ticker, string invoiceCurrency, StoreData store, bool allowFallback)
    {
        try
        {
            var pair = new CurrencyPair(ticker, invoiceCurrency);
            var storeBlob = store.GetStoreBlob();
            var rateRules = storeBlob.GetRateRules(_defaultRules);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var result = await _rateFetcher.FetchRate(pair, rateRules, new StoreIdRateContext(store.Id), cts.Token);

            if (result.BidAsk != null && result.BidAsk.Bid > 0)
            {
                _log.LogInformation("Found exchange rate for {Pair}: {Rate}", pair, result.BidAsk.Bid);
                return (result.BidAsk.Bid, result.Rule ?? "exchange");
            }

            if (allowFallback)
            {
                _log.LogWarning("No exchange rate found for {Pair}, using opted-in 1:1 fallback", pair);
                return (1m, "fallback-1:1-opted-in");
            }
            throw new PaymentMethodUnavailableException($"Exchange rate for {ticker}/{invoiceCurrency} unavailable");
        }
        catch (PaymentMethodUnavailableException) { throw; }
        catch (OperationCanceledException)
        {
            if (allowFallback)
            {
                _log.LogWarning("Rate fetch timed out for {Ticker}/{Currency}, using opted-in 1:1 fallback", ticker, invoiceCurrency);
                return (1m, "fallback-1:1-opted-in");
            }
            throw new PaymentMethodUnavailableException($"Exchange rate for {ticker}/{invoiceCurrency} unavailable");
        }
        catch (Exception ex)
        {
            if (allowFallback)
            {
                _log.LogWarning(ex, "Failed to fetch rate for {Ticker}/{Currency}, using opted-in 1:1 fallback", ticker, invoiceCurrency);
                return (1m, "fallback-1:1-opted-in");
            }
            throw new PaymentMethodUnavailableException($"Exchange rate for {ticker}/{invoiceCurrency} unavailable");
        }
    }
}
