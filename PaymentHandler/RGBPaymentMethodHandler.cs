using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodHandler : IPaymentMethodHandler
{
    readonly RGBWalletService _wallets;
    readonly RateFetcher _rateFetcher;
    readonly DefaultRulesCollection _defaultRules;
    readonly ILogger<RGBPaymentMethodHandler> _log;

    public RGBPaymentMethodHandler(
        RGBWalletService wallets, 
        RGBConfiguration config, 
        RateFetcher rateFetcher,
        DefaultRulesCollection defaultRules,
        ILogger<RGBPaymentMethodHandler> log)
    {
        _wallets = wallets;
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

        var assetId = config.DefaultAssetId;
        var ticker = "RGB";
        var name = "RGB Asset";
        var precision = 0;

        if (!string.IsNullOrEmpty(assetId))
        {
            try
            {
                var assets = await _wallets.ListAssetsAsync(config.WalletId);
                var match = assets.Find(a => a.AssetId == assetId);
                if (match != null)
                {
                    ticker = match.Ticker;
                    name = match.Name;
                    precision = match.Precision;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not fetch asset {Id} details", assetId);
            }
        }

        var invoiceCurrency = ctx.InvoiceEntity.Currency;
        var invoicePrice = ctx.InvoiceEntity.Price;
        
        var (rate, rateSource) = await TryFetchRateAsync(ticker, invoiceCurrency, ctx.Store);
        
        var multiplier = (decimal)Math.Pow(10, precision);
        decimal unitsDecimal;
        if (rate > 0)
        {
            unitsDecimal = invoicePrice / rate * multiplier;
        }
        else
        {
            unitsDecimal = invoicePrice * multiplier;
            rate = 1m;
        }
        var units = invoicePrice > 0 ? (long)Math.Ceiling(unitsDecimal) : 1L;
        
        _log.LogInformation("RGB invoice: {Price} {Currency} → {Units} {Ticker} (rate: {Rate} from {Source})", 
            invoicePrice, invoiceCurrency, units, ticker, rate, rateSource);

        var expiration = ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow;
        var invoice = await _wallets.CreateInvoiceAsync(config.WalletId, assetId, units, expiration, ctx.InvoiceEntity.Id);
        
        ctx.Prompt.Currency = ticker;
        ctx.Prompt.Divisibility = precision;
        
        ctx.InvoiceEntity.Rates[ticker] = rate;
        ctx.InvoiceEntity.Rates[$"{ticker}_{invoiceCurrency}"] = rate;

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

    public void StripDetailsForNonOwner(object details) { }

    async Task<(decimal Rate, string Source)> TryFetchRateAsync(string ticker, string invoiceCurrency, StoreData store)
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
            
            _log.LogInformation("No exchange rate found for {Pair}, using 1:1 fallback", pair);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Rate fetch timed out for {Ticker}/{Currency}, using 1:1 fallback", ticker, invoiceCurrency);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to fetch rate for {Ticker}/{Currency}, using 1:1 fallback", ticker, invoiceCurrency);
        }
        
        return (1m, "fallback-1:1");
    }
}
