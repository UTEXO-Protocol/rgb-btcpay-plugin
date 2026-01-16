using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RGB.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RGB.PaymentHandler;

public class RGBPaymentMethodHandler : IPaymentMethodHandler
{
    readonly RGBWalletService _wallets;
    readonly ILogger<RGBPaymentMethodHandler> _log;

    public RGBPaymentMethodHandler(RGBWalletService wallets, RGBConfiguration config, ILogger<RGBPaymentMethodHandler> log)
    {
        _wallets = wallets; _log = log;
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

        var due = ctx.Prompt.Calculate().Due;
        var multiplier = (decimal)Math.Pow(10, precision);
        var units = due > 0 ? (long)(due * multiplier) : 1L;

        var expiration = ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow;
        var invoice = await _wallets.CreateInvoiceAsync(config.WalletId, assetId, units, expiration, ctx.InvoiceEntity.Id);

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
        ctx.Prompt.Currency = "BTC";
        ctx.Prompt.Divisibility = 8;
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
}
