using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodHandler : IPaymentMethodHandler
{
    readonly IRGBWalletService _wallets;
    readonly IRgbRateSource _rates;
    readonly IRgbPricingCodeCollisionGuard _pricingCodeGuard;
    readonly IRgbNoticeRaiser _notices;
    readonly ILogger<RGBPaymentMethodHandler> _log;

    public RGBPaymentMethodHandler(
        IRGBWalletService wallets,
        IRgbRateSource rates,
        IRgbPricingCodeCollisionGuard pricingCodeGuard,
        IRgbNoticeRaiser notices,
        ILogger<RGBPaymentMethodHandler> log)
    {
        _wallets = wallets;
        _rates = rates;
        _pricingCodeGuard = pricingCodeGuard;
        _notices = notices;
        _log = log;
    }

    public PaymentMethodId PaymentMethodId => RGBPlugin.RGBPaymentMethodId;
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    // WHY a second serializer: BlobSerializer sets DefaultValueHandling.Ignore, which Newtonsoft
    // applies on DESERIALIZE too — an explicit 0 is skipped and the property initialiser survives,
    // so a validator using it cannot see the value the caller actually sent. Everything else is
    // identical, including the contract resolver, so property matching (and its case-insensitive
    // fallback) is exactly what BTCPay will use to store the config.
    // CreateSerializer() returns per-call-fresh settings and resolver, so mutating this copy cannot
    // affect how any other blob in BTCPay is serialized.
    // internal, not private: the tests read it directly via InternalsVisibleTo.
    internal static readonly JsonSerializer StrictSerializer = BuildStrictSerializer();

    static JsonSerializer BuildStrictSerializer()
    {
        var settings = BlobSerializer.CreateSerializer().SerializerSettings;
        settings.DefaultValueHandling = DefaultValueHandling.Include;
        settings.NullValueHandling = NullValueHandling.Include;
        return JsonSerializer.CreateDefault(settings);
    }

    public Task ValidatePaymentMethodConfig(PaymentMethodConfigValidationContext validationContext)
    {
        RGBPaymentMethodConfig config;
        try
        {
            config = validationContext.Config.ToObject<RGBPaymentMethodConfig>(StrictSerializer)
                     ?? throw new JsonSerializationException("empty configuration");
        }
        catch (JsonException ex)
        {
            // WHY catch here rather than let it escape: the Greenfield controller converts any
            // exception into a generic 422, which is correct but names no field.
            // WHY pattern-match for Path instead of reading ex.Path: Newtonsoft declares Path on the
            // concrete subtypes only, not on JsonException, so ex.Path would not compile.
            var path = ex switch
            {
                JsonReaderException reader => reader.Path,
                JsonSerializationException serialization => serialization.Path,
                _ => null
            };
            validationContext.ModelState.AddModelError(
                string.IsNullOrEmpty(path) ? "config" : path!,
                string.IsNullOrEmpty(path)
                    ? "the configuration could not be read"
                    : $"{path} could not be read as a valid value");
            return Task.CompletedTask;
        }

        // WHY reject rather than clamp: storing a value other than the one requested and returning
        // 200 relocates the surprise instead of removing it. ResolveAllocationsPerUtxo still clamps
        // on the wallet-creation path, where there is no caller to return an error to.
        Bound("utxoCount", config.UtxoCount, RgbConfigBounds.UtxoCountMin, RgbConfigBounds.UtxoCountMax);
        Bound("utxoSize", config.UtxoSize, RgbConfigBounds.UtxoSizeMin, RgbConfigBounds.UtxoSizeMax);
        Bound("minConfirmations", config.MinConfirmations, RgbConfigBounds.MinConfirmationsMin, RgbConfigBounds.MinConfirmationsMax);

        return Task.CompletedTask;

        // WHY report every violation: these are pure comparisons, so a caller fixing three fields
        // should not need three round trips.
        void Bound(string key, int value, int min, int max)
        {
            if (value < min || value > max)
                validationContext.ModelState.AddModelError(key, $"{key} must be between {min} and {max}");
        }
    }

    public async Task ConfigurePrompt(PaymentMethodContext ctx)
    {
        if (!ctx.Store.GetPaymentMethodConfigs().TryGetValue(PaymentMethodId, out var configToken))
            throw new PaymentMethodUnavailableException("RGB not configured for this store");

        if (ctx.InvoiceEntity.LazyPaymentMethods)
            throw new PaymentMethodUnavailableException(
                "RGB payments require lazy payment-method activation to be OFF for this invoice — the "
                + "store setting \"Only enable the payment method after the user selects it\", or the "
                + "invoice's own checkout.lazyPaymentMethods override. On the lazy activation path "
                + "BTCPay persists the payment prompt "
                + "from a freshly re-read invoice blob, which discards the RGB pricing rate this handler "
                + "records, and every later read of the invoice then fails on the missing rate.");

        var config = ParsePaymentMethodConfig(configToken);
        try
        {
            RgbConfigBounds.EnsurePaymentMethodValuesValid(
                config.UtxoCount, config.UtxoSize, config.MinConfirmations);
        }
        catch (InvalidOperationException ex)
        {
            throw new PaymentMethodUnavailableException(ex.Message);
        }
        
        // The wallet table is authoritative (one active wallet per store). The config pointer is retained
        // only for wire compatibility: Greenfield treats PUT config as replacement, so a partial update can
        // omit walletId and serialize it as empty. Resolving by store makes that harmless and also prevents a
        // foreign pointer from selecting another store's wallet.
        var wallet = await _wallets.GetWalletForStoreAsync(ctx.Store.Id);
        if (wallet == null)
            throw new PaymentMethodUnavailableException("RGB wallet missing");

        if (!WalletBelongsToStore(wallet.StoreId, ctx.Store.Id))
            throw new PaymentMethodUnavailableException("RGB wallet does not belong to this store");

        if (string.IsNullOrEmpty(config.DefaultAssetId))
            throw new PaymentMethodUnavailableException(
                "Select a default RGB asset in store Settings to accept payments");

        var asset = await _wallets.GetAssetAsync(wallet.Id, config.DefaultAssetId);
        if (asset == null)
            throw new PaymentMethodUnavailableException(
                $"Configured asset {config.DefaultAssetId[..Math.Min(20, config.DefaultAssetId.Length)]}... not found in wallet");

        var pricingCode = RgbPricingCode.For(asset.AssetId);
        if (!await _pricingCodeGuard.IsUnambiguousAsync(asset.AssetId))
        {
            _log.LogCritical("RGB pricing code {Code} is ambiguous for contract {AssetId}; refusing invoice pricing",
                pricingCode, asset.AssetId);
            throw new PaymentMethodUnavailableException(
                $"RGB pricing identity collision for {pricingCode}; invoice creation was refused");
        }

        var invoiceCurrency = ctx.InvoiceEntity.Currency;
        var rate = await _rates.FetchAsync(pricingCode, invoiceCurrency, ctx.Store, default);
        if (!rate.IsOk)
        {
            if (rate.Failure == RgbRateFailure.NoRule
                && IsStoreWidePricingFailure(invoiceCurrency, ctx.Store.GetStoreBlob().DefaultCurrency))
            {
                try
                {
                    await _notices.RaiseOncePerCauseAsync(
                        ctx.Store.Id, RgbReplenishmentNoticeCause.PricingCodeHasNoRule);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Failed to raise the RGB pricing notice for store {StoreId}; the invoice is still refused",
                        ctx.Store.Id);
                }
            }
            throw new PaymentMethodUnavailableException(RefusalMessage(rate, pricingCode, invoiceCurrency));
        }

        var plan = RgbPricingPlan.Build(pricingCode, asset.Precision, ctx.InvoiceEntity.Price, rate.Rate);

        _log.LogInformation("RGB invoice: {Price} {Currency} -> {Units} {Code} (rate: {Rate} from {Source})",
            ctx.InvoiceEntity.Price, invoiceCurrency, plan.Units, plan.PricingCode, rate.Rate, rate.Source);

        var expiration = ctx.InvoiceEntity.ExpirationTime - DateTimeOffset.UtcNow;
        var invoice = await _wallets.CreateInvoiceAsync(wallet.Id, asset.AssetId, plan.Units, expiration,
            ctx.InvoiceEntity.Id, config.MinConfirmations,
            ctx.InvoiceEntity.MonitoringExpiration.ToUnixTimeSeconds());

        ctx.Prompt.Currency = plan.PromptCurrency;
        ctx.Prompt.Divisibility = asset.Precision;

        ctx.InvoiceEntity.Rates = RatesCopyThatNoSiblingPromptCanBeEnumerating(
            ctx.InvoiceEntity.Rates, plan.RatesKey, rate.Rate);

        var rateBTCPayWillChargeAt = ctx.InvoiceEntity.GetInvoiceRate(plan.PromptCurrency);
        if (rateBTCPayWillChargeAt != rate.Rate)
            throw new PaymentMethodUnavailableException(
                $"BTCPay resolves rate {rateBTCPayWillChargeAt} for {plan.PromptCurrency}, but the "
                + $"{plan.Units} units this invoice demands were computed from {rate.Rate}. Refusing the "
                + "invoice: a customer paying the demanded units would settle a total BTCPay never showed.");

        ctx.Prompt.Destination = invoice.Invoice;
        ctx.Prompt.PaymentMethodFee = 0m;
        ctx.TrackedDestinations.Add(invoice.RecipientId);

        ctx.Prompt.Details = JObject.FromObject(new RGBPromptDetails
        {
            WalletId = wallet.Id,
            RgbInvoiceId = invoice.Id,
            RecipientId = invoice.RecipientId,
            AssetId = asset.AssetId,
            AssetTicker = asset.Ticker,
            AssetName = asset.Name,
            AssetPrecision = asset.Precision,
            AmountInAssetUnits = plan.Units,
            PricingCode = plan.PricingCode
        }, Serializer);
    }

    internal static Dictionary<string, decimal> RatesCopyThatNoSiblingPromptCanBeEnumerating(
        Dictionary<string, decimal>? ratesSharedAcrossEveryConcurrentPrompt,
        string ratesKey,
        decimal rate)
    {
        if (ratesSharedAcrossEveryConcurrentPrompt is null)
            throw new PaymentMethodUnavailableException(
                "The invoice carries no rate table, so the RGB pricing rate cannot be recorded");
        if (string.IsNullOrEmpty(ratesKey))
            throw new PaymentMethodUnavailableException(
                "The RGB pricing rate has no key, so nothing could read it back at checkout");

        var copy = new Dictionary<string, decimal>(
            ratesSharedAcrossEveryConcurrentPrompt,
            ratesSharedAcrossEveryConcurrentPrompt.Comparer);
        copy[ratesKey] = rate;
        return copy;
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

    internal static bool IsStoreWidePricingFailure(string invoiceCurrency, string? storeDefaultCurrency) =>
        !string.IsNullOrWhiteSpace(invoiceCurrency)
        && !string.IsNullOrWhiteSpace(storeDefaultCurrency)
        && string.Equals(invoiceCurrency.Trim(), storeDefaultCurrency.Trim(),
            StringComparison.OrdinalIgnoreCase);

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

    internal static string RefusalMessageForTest(
        RgbRateResult result, string pricingCode, string invoiceCurrency) =>
        RefusalMessage(result, pricingCode, invoiceCurrency);

    static string RefusalMessage(RgbRateResult result, string pricingCode, string invoiceCurrency) => result.Failure switch
    {
        RgbRateFailure.Timeout => $"Exchange rate lookup for {pricingCode}/{invoiceCurrency} timed out",
        RgbRateFailure.Error => $"Exchange rate lookup for {pricingCode}/{invoiceCurrency} failed",
        RgbRateFailure.NoRule when result.PreferredSource =>
            $"This store uses default exchange rates, which cannot price an RGB contract. Add a rate rule naming {pricingCode}_{invoiceCurrency}, the exact pair this invoice needs. This requires rate scripting; enabling it copies your current default rules into the script, so other payment methods keep pricing, but the script then stops tracking BTCPay's future defaults.",
        RgbRateFailure.NoRule =>
            $"No rate rule names {pricingCode}_{invoiceCurrency}, so RGB cannot be priced for this invoice. Add a rule naming that pair in this store's rate settings.",
        // Distinct from NoRule: WrapperRateProvider swallows provider exceptions, so this arm covers a
        // correctly configured store whose exchange is simply down. Naming the configuration cause
        // here would tell that merchant their rules are wrong.
        _ => $"The rate source returned no rate for {pricingCode}_{invoiceCurrency}; a rule names the pair, so this is the source being unavailable rather than a configuration problem"
    };
}
