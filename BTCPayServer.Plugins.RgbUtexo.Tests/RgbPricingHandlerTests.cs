using BTCPayServer.Data;              // BlobSerializer
using BTCPayServer.Logging;           // InvoiceLogs
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Invoices;
using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;           // JObject
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPaymentCurrencyTests
{
    const string Asset = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string OtherAsset = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";

    [Fact]
    public void CurrentContractBoundPricingCode_IsThePaymentCurrency()
    {
        var details = new RGBPromptDetails
        {
            AssetId = Asset, PricingCode = RgbPricingCode.For(Asset), AssetTicker = "USDT"
        };
        Assert.Equal(RgbPricingCode.For(Asset), RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Fact]
    public void PreUpgradePromptWithoutCode_IsRejectedInsteadOfUsingTicker()
    {
        var json = JObject.Parse($$"""{"walletId":"w1","assetId":"{{Asset}}","assetTicker":"USDT","amountInAssetUnits":5}""");
        var details = json.ToObject<RGBPromptDetails>(BlobSerializer.CreateSerializer().Serializer)!;
        Assert.Null(details.PricingCode);
        Assert.Throws<FormatException>(() => RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Fact]
    public void PromptWithOld64BitCode_IsRejected()
    {
        var details = new RGBPromptDetails
        {
            AssetId = Asset, PricingCode = "RGB0123456789ABCDEF", AssetTicker = "USDT"
        };
        Assert.Throws<FormatException>(() => RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Fact]
    public void CurrentCodeForAnotherContract_IsRejected()
    {
        var details = new RGBPromptDetails
        {
            AssetId = Asset, PricingCode = RgbPricingCode.For(OtherAsset), AssetTicker = "USDT"
        };
        Assert.Throws<FormatException>(() => RGBInvoiceListener.ResolvePaymentCurrency(details));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("RGB0123456789ABCDEF")]
    public void LegacyPromptIdentity_IsUnregisterableNotFailed(string? pricingCode)
    {
        var invoice = new RGBInvoice { AssetId = Asset };
        var details = new RGBPromptDetails
        {
            AssetId = Asset, PricingCode = pricingCode, AssetTicker = "USDT"
        };

        var registration = RGBInvoiceListener.ClassifyPromptPricingIdentity(
            invoice, details, out var paymentCurrency);

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Unregisterable, registration);
        Assert.Equal("", paymentCurrency);
    }

    [Fact]
    public void PromptAssetMismatch_IsUnregisterableNotFailed()
    {
        var details = new RGBPromptDetails
        {
            AssetId = Asset, PricingCode = RgbPricingCode.For(Asset), AssetTicker = "USDT"
        };

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Unregisterable,
            RGBInvoiceListener.ClassifyPromptPricingIdentity(
                new RGBInvoice { AssetId = Asset + "other" }, details, out _));
    }
}

public class RgbPricingHandlerTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";
    const string StoreId = "store-1";
    const string WalletId = "wallet-1";

    // Not Stubs/FakeRGBWalletService: task 4 leaves its new members throwing, so every case here would
    // fault before reaching the pricing path.
    sealed class PricingWalletStub : IRGBWalletService, IRgbPricingCodeCollisionGuard
    {
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

        public RGBAsset? Asset;
        public long? RecordedAmount;
        public bool Unambiguous = true;
        public string? CollisionCheckedAssetId;

        public Task<bool> IsUnambiguousAsync(string assetId, CancellationToken ct = default)
        {
            CollisionCheckedAssetId = assetId;
            return Task.FromResult(Unambiguous);
        }

        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) =>
            Task.FromResult<RGBWallet?>(new RGBWallet { Id = walletId, StoreId = StoreId });

        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) =>
            Task.FromResult(Asset is not null && Asset.AssetId == assetId ? Asset : null);

        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount,
            TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1,
            long? monitoringExpirationTimestamp = null, CancellationToken ct = default)
        {
            RecordedAmount = amount;
            return Task.FromResult(new RGBInvoice
            {
                Id = "rgb-inv-1", WalletId = walletId, Invoice = "rgb:~/~/dest", RecipientId = "utxob:recipient"
            });
        }

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default) =>
            Task.FromResult<RGBWallet?>(new RGBWallet { Id = WalletId, StoreId = storeId });
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    sealed class RecordingRateSource : IRgbRateSource
    {
        readonly RgbRateResult _result;
        public string? SeenPricingCode;
        public string? SeenInvoiceCurrency;

        public RecordingRateSource(RgbRateResult result) => _result = result;

        public Task<RgbRateResult> FetchAsync(string pricingCode, string invoiceCurrency, StoreData store, CancellationToken ct)
        {
            SeenPricingCode = pricingCode;
            SeenInvoiceCurrency = invoiceCurrency;
            return Task.FromResult(_result);
        }
    }

    static RGBAsset AssetRow(string assetId, string ticker, int precision) =>
        new() { AssetId = assetId, WalletId = WalletId, Ticker = ticker, Name = "Token", Precision = precision };

    static StoreData Store(string assetId, string? rateScript = null, string? defaultCurrency = null)
    {
        var store = rateScript is null
            ? new StoreData { Id = StoreId }
            : TestStores.StoreWithScript(rateScript);
        store.Id = StoreId;
        if (defaultCurrency is not null)
        {
            var blob = store.GetStoreBlob();
            blob.DefaultCurrency = defaultCurrency;
            store.SetStoreBlob(blob);
        }
        store.SetPaymentMethodConfig(RGBPlugin.RGBPaymentMethodId, JObject.FromObject(
            new RGBPaymentMethodConfig { WalletId = WalletId, DefaultAssetId = assetId }));
        return store;
    }

    [Fact]
    public async Task LazyPaymentMethods_RefusesTheRgbPromptInsteadOfHandingOutAnUnsettleableDestination()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(1m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
        ctx.InvoiceEntity.LazyPaymentMethods = true;

        var refusal = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(
            () => handler.ConfigurePrompt(ctx));

        Assert.Contains("lazy payment-method activation", refusal.Message);
        Assert.True(wallets.RecordedAmount == null,
            "the handler created an RGB invoice on the lazy activation path. BTCPay persists that prompt "
            + "from a freshly re-read invoice blob, which discards the pricing rate this handler records, "
            + "and every later read of the invoice then throws on the missing rate — so a customer would be "
            + "handed a payable destination for an invoice that can never settle");
    }

    [Fact]
    public async Task AnInvoiceThatOptedOutOfLazyActivation_IsPricedEvenWhenTheStoreDefaultIsLazy()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);
        var store = Store(AssetA);
        var blob = store.GetStoreBlob();
        blob.LazyPaymentMethods = true;
        store.SetStoreBlob(blob);

        var ctx = Context(store, handler, price: 100m, currency: "USD");
        ctx.InvoiceEntity.LazyPaymentMethods = false;

        await handler.ConfigurePrompt(ctx);

        Assert.Equal(50L, wallets.RecordedAmount);
    }

    [Fact]
    public async Task EagerPaymentMethods_StillPriceTheInvoice()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);

        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
        await handler.ConfigurePrompt(ctx);

        Assert.Equal(50L, wallets.RecordedAmount);
    }

    [Fact]
    public async Task OmittedConfigWalletId_UsesTheStoresAuthoritativeActiveWallet()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(1m, "test")), asset);
        var store = Store(AssetA);
        store.SetPaymentMethodConfig(RGBPlugin.RGBPaymentMethodId, JObject.FromObject(
            new RGBPaymentMethodConfig { WalletId = "", DefaultAssetId = AssetA }));

        var ctx = Context(store, handler, price: 1m, currency: "USD");
        await handler.ConfigurePrompt(ctx);

        Assert.Equal(WalletId, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).WalletId);
    }

    static PaymentMethodContext Context(StoreData store, IPaymentMethodHandler handler,
        decimal price, string currency)
    {
        var invoice = new InvoiceEntity
        {
            Id = "btcpay-inv-1",
            Currency = currency,
            Price = price,
            ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(30)
        };
        var config = store.GetPaymentMethodConfig(RGBPlugin.RGBPaymentMethodId)!;
        return new PaymentMethodContext(store, store.GetStoreBlob(), config, handler, invoice, new InvoiceLogs());
    }

    static (RGBPaymentMethodHandler Handler, PricingWalletStub Wallets) Build(
        IRgbRateSource rates, RGBAsset asset, bool unambiguous = true)
    {
        var (handler, wallets, _) = BuildWithNoticeRaiser(rates, asset, unambiguous);
        return (handler, wallets);
    }

    static (RGBPaymentMethodHandler Handler, PricingWalletStub Wallets, RecordingNoticeRaiser Notices)
        BuildWithNoticeRaiser(
            IRgbRateSource rates, RGBAsset asset, bool unambiguous = true,
            Exception? noticeFault = null)
    {
        var wallets = new PricingWalletStub { Asset = asset, Unambiguous = unambiguous };
        var notices = new RecordingNoticeRaiser { Fault = noticeFault };
        var handler = new RGBPaymentMethodHandler(
            wallets, rates, wallets, notices, NullLogger<RGBPaymentMethodHandler>.Instance);
        return (handler, wallets, notices);
    }

    sealed class RecordingNoticeRaiser : IRgbNoticeRaiser
    {
        internal Exception? Fault { get; init; }

        internal List<(string StoreId, RgbReplenishmentNoticeCause Cause)> Raised { get; } = [];

        public Task RaiseOncePerCauseAsync(
            string storeId, RgbReplenishmentNoticeCause cause, CancellationToken ct = default)
        {
            Raised.Add((storeId, cause));
            return Fault == null ? Task.CompletedTask : Task.FromException(Fault);
        }
    }

    // 15 — a resolved rate is the rate that prices the invoice.
    [Fact]
    public async Task OkRate_PricesTheInvoice()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2.5m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        var details = handler.ParsePaymentPromptDetails(ctx.Prompt.Details);
        Assert.Equal(40L, details.AmountInAssetUnits);
    }

    // 16/17/18 — no Failure kind may become a rate. [T3]
    [Theory]
    [InlineData(RgbRateFailure.NoRate)]
    [InlineData(RgbRateFailure.NoRule)]
    [InlineData(RgbRateFailure.Timeout)]
    [InlineData(RgbRateFailure.Error)]
    public async Task EveryFailureKind_RefusesTheInvoice(RgbRateFailure failure)
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Failed(failure, false)), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Contains(RgbPricingCode.For(AssetA), ex.Message);
        Assert.Contains("USD", ex.Message);
        Assert.Null(wallets.RecordedAmount);
    }

    [Fact]
    public async Task PricingCodeCollision_RefusesBeforeRateLookupOrRgbInvoiceCreation()
    {
        var source = new RecordingRateSource(RgbRateResult.Ok(1m, "must-not-be-used"));
        var (handler, wallets) = Build(source, AssetRow(AssetA, "USDT", 0), unambiguous: false);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Equal(AssetA, wallets.CollisionCheckedAssetId);
        Assert.Null(source.SeenPricingCode);
        Assert.Null(wallets.RecordedAmount);
    }

    // 18b — a store still on default rules is told what is actually wrong.
    [Fact]
    public async Task DefaultRulesStore_IsToldToAddARateRule()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Failed(RgbRateFailure.NoRule, true)), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Contains("rate scripting", ex.Message);
        Assert.Contains(RgbPricingCode.For(AssetA), ex.Message);
    }

    // The upgrade break reaching the notification bell. Only NoRule is a configuration cause: the notice
    // stamps a PERMANENT per-store marker, so raising it on NoRate/Timeout/Error would tell a
    // correctly-configured merchant whose exchange is momentarily down to rewrite their rate rules, AND
    // would permanently consume the one pricing notification that store will ever get.
    [Fact]
    public async Task NoRuleRefusal_RaisesThePricingNoticeExactlyOnce()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _, notices) = BuildWithNoticeRaiser(
            new RecordingRateSource(RgbRateResult.Failed(RgbRateFailure.NoRule, false)), asset);
        var store = Store(AssetA);
        var ctx = Context(store, handler, price: 100m, currency: "USD");

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Equal(
            [(store.Id, RgbReplenishmentNoticeCause.PricingCodeHasNoRule)],
            notices.Raised);
    }

    [Theory]
    [InlineData(RgbRateFailure.NoRate)]
    [InlineData(RgbRateFailure.Timeout)]
    [InlineData(RgbRateFailure.Error)]
    public async Task ASourceUnavailableRefusal_RaisesNoPricingNotice(RgbRateFailure failure)
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _, notices) = BuildWithNoticeRaiser(
            new RecordingRateSource(RgbRateResult.Failed(failure, false)), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.True(notices.Raised.Count == 0,
            $"{failure} raised {notices.Raised.Count} pricing notice(s). Only NoRule says anything about "
            + "the store's configuration; the notice marker is permanent, so a single exchange outage "
            + "would burn it and a genuinely stale rate rule would then never be reported.");
    }

    // N1: RgbRateSource resolves a rule for the exact (pricingCode, invoiceCurrency) PAIR, but the notice
    // and its durable marker are per STORE and fire once ever. A store whose default currency prices fine
    // must not have that one notice consumed — nor be told every RGB invoice is refused — because one
    // invoice arrived in a currency it has no rule for. Driven through the REAL rate source so the pair
    // scoping is exercised rather than asserted.
    [Fact]
    public async Task AnUnsupportedQuoteCurrency_DoesNotBurnTheStoreWideNotice()
    {
        var codeA = RgbPricingCode.For(AssetA);
        var (handler, _, notices) = BuildWithNoticeRaiser(
            TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var store = Store(AssetA, $"{codeA}_USD = 2;", defaultCurrency: "USD");

        var ctx = Context(store, handler, price: 100m, currency: "EUR");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.True(notices.Raised.Count == 0,
            "a EUR invoice against a store whose USD pricing works raised the store-wide pricing notice. "
            + "That notice is one-shot and permanent, so it would be consumed by an unsupported quote "
            + "currency and unavailable when a genuinely store-wide failure happens.");

        // The same store still prices its default currency, which is what makes the claim false.
        var working = Context(store, handler, price: 100m, currency: "USD");
        await handler.ConfigurePrompt(working);
        Assert.Equal(50L, handler.ParsePaymentPromptDetails(working.Prompt.Details).AmountInAssetUnits);
        Assert.Empty(notices.Raised);
    }

    [Fact]
    public async Task NoRuleForTheStoresDefaultCurrency_DoesRaiseTheStoreWideNotice()
    {
        var (handler, _, notices) = BuildWithNoticeRaiser(
            TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var store = Store(AssetA, "X_X = 1;", defaultCurrency: "USD");

        var ctx = Context(store, handler, price: 100m, currency: "USD");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Equal(
            [(StoreId, RgbReplenishmentNoticeCause.PricingCodeHasNoRule)],
            notices.Raised);
    }

    // The merchant round 3 left with no surface at all: on BTCPay's DEFAULT rates (PreferredSource true)
    // with a NON-default invoice currency, the notification is correctly suppressed and the settings page
    // probes the default currency, so the refusal is the ONLY place the needed pair can appear. It named
    // the pricing code alone until this test existed.
    [Theory]
    [InlineData("EUR")]
    [InlineData("GBP")]
    public async Task ADefaultRatesStoreOnANonDefaultCurrency_IsToldTheExactPairItNeeds(string currency)
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _, notices) = BuildWithNoticeRaiser(
            new RecordingRateSource(RgbRateResult.Failed(RgbRateFailure.NoRule, preferredSource: true)),
            asset);
        var store = Store(AssetA, defaultCurrency: "USD");
        var ctx = Context(store, handler, price: 100m, currency: currency);

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(
            () => handler.ConfigurePrompt(ctx));

        var pair = $"{RgbPricingCode.For(AssetA)}_{currency}";
        Assert.True(ex.Message.Contains(pair, StringComparison.Ordinal),
            $"the refusal does not name '{pair}'. This is a default-rates store on a non-default quote "
            + "currency: the store-wide notification is deliberately suppressed and the settings page "
            + "probes the default currency, so this message is the only place the merchant can learn "
            + $"which pair to add. It said: {ex.Message}");

        // The actionable half for THIS merchant is still there.
        Assert.Contains("rate scripting", ex.Message);
        Assert.Empty(notices.Raised);
    }

    // Both NoRule arms must name the pair; the PreferredSource one was missed once already.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryNoRuleRefusal_NamesTheCompletePair(bool preferredSource)
    {
        var message = RGBPaymentMethodHandler.RefusalMessageForTest(
            RgbRateResult.Failed(RgbRateFailure.NoRule, preferredSource), "RGB2ABC", "EUR");

        Assert.Contains("RGB2ABC_EUR", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("USD", "USD", true)]
    [InlineData("usd", "USD", true)]
    [InlineData(" USD ", "USD", true)]
    [InlineData("EUR", "USD", false)]
    [InlineData("USD", null, false)]
    [InlineData("USD", "", false)]
    [InlineData("", "USD", false)]
    public void TheStoreWideTest_HoldsOnlyForTheStoresOwnDefaultCurrency(
        string invoiceCurrency, string? storeDefault, bool expected)
        => Assert.Equal(expected,
            RGBPaymentMethodHandler.IsStoreWidePricingFailure(invoiceCurrency, storeDefault));

    // The message is the money surface: it must not assert a blast radius wider than the condition that
    // triggered it.
    [Fact]
    public void ThePricingNoticeMessage_DoesNotClaimEveryInvoiceIsRefused()
    {
        var message = RgbReplenishmentNotice.MessageFor(
            RgbReplenishmentNoticeCause.PricingCodeHasNoRule);

        foreach (var overclaim in new[] { "every RGB invoice is refused", "all RGB invoices" })
            Assert.True(!message.Contains(overclaim, StringComparison.OrdinalIgnoreCase),
                $"the pricing notice claims '{overclaim}'. It is raised for the store's DEFAULT currency "
                + "only, and invoices in another currency with their own rule still price, so that "
                + "overstates the outage on a surface the merchant acts on.");

        Assert.Contains("default currency", message, StringComparison.OrdinalIgnoreCase);
    }

    // A notification failure must never change what the merchant sees at checkout.
    [Fact]
    public async Task NoticeRaiserThatThrows_DoesNotChangeTheRefusal()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _, _) = BuildWithNoticeRaiser(
            new RecordingRateSource(RgbRateResult.Failed(RgbRateFailure.NoRule, false)), asset,
            noticeFault: new InvalidOperationException("notification subsystem is down"));
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        var ex = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(
            () => handler.ConfigurePrompt(ctx));

        Assert.Contains(RgbPricingCode.For(AssetA), ex.Message);
    }

    // 24 — the finding itself: two contracts sharing a ticker must NOT share a rate rule. Both halves
    // in one test; asserting only the refusal would pass vacuously if the real source failed for
    // every asset, leaving T1 unproven. [T1]
    [Fact]
    public async Task TwoContractsSharingATicker_PriceIndependently()
    {
        var codeA = RgbPricingCode.For(AssetA);
        var script = $"{codeA}_USD = 2;";

        var (handlerA, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var ctxA = Context(Store(AssetA, script), handlerA, price: 100m, currency: "USD");
        await handlerA.ConfigurePrompt(ctxA);
        Assert.Equal(50L, handlerA.ParsePaymentPromptDetails(ctxA.Prompt.Details).AmountInAssetUnits);

        var (handlerB, walletsB) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetB, "USDT", 0));
        var ctxB = Context(Store(AssetB, script), handlerB, price: 100m, currency: "USD");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handlerB.ConfigurePrompt(ctxB));
        Assert.Null(walletsB.RecordedAmount);
    }

    [Theory]
    [InlineData("BTC", "BTC_USD = 90000;")]
    [InlineData("EUR", "EUR_USD = 1;")]
    [InlineData("RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_USD = 1;")]
    public async Task IssuerChosenTicker_CannotBorrowItsLiteralRateRule(string ticker, string script)
    {
        var (handler, wallets) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, ticker, 0));
        var ctx = Context(Store(AssetA, script), handler, price: 100m, currency: "USD");

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.Null(wallets.RecordedAmount);
    }

    [Fact]
    public async Task Old64BitRule_IsNotMappedAndMerchantCanRecoverWithCurrentExplicitRule()
    {
        const string oldCode = "RGBF40956866D3C22DA";
        var currentCode = RgbPricingCode.For(AssetA);

        var (oldHandler, oldWallets) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var oldContext = Context(Store(AssetA, $"{oldCode}_USD = 2;"), oldHandler, 100m, "USD");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => oldHandler.ConfigurePrompt(oldContext));
        Assert.Null(oldWallets.RecordedAmount);

        var (currentHandler, currentWallets) = Build(
            TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USDT", 0));
        var currentContext = Context(
            Store(AssetA, $"{currentCode}_USD = 2;"), currentHandler, 100m, "USD");
        await currentHandler.ConfigurePrompt(currentContext);

        Assert.Equal(50L, currentWallets.RecordedAmount);
    }

    // 25 — an ISO code as a ticker claims nothing. USD_JPY exists; the contract calling itself USD
    // still cannot use it. [T1, ISO]
    [Fact]
    public async Task AnIsoTicker_DoesNotClaimThatIsoCodesRule()
    {
        var codeA = RgbPricingCode.For(AssetA);

        var (handler, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USD", 0));
        var ctx = Context(Store(AssetA, "USD_JPY = 150;"), handler, price: 300m, currency: "JPY");
        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        var (handler2, _) = Build(TestRateSource.WithNoExchanges(), AssetRow(AssetA, "USD", 0));
        var ctx2 = Context(Store(AssetA, $"USD_JPY = 150;\n{codeA}_JPY = 150;"), handler2, price: 300m, currency: "JPY");
        await handler2.ConfigurePrompt(ctx2);
        Assert.Equal(2L, handler2.ParsePaymentPromptDetails(ctx2.Prompt.Details).AmountInAssetUnits);
    }

    // 26 — the Rates write is keyed by the code and cannot overwrite a real currency's rate.
    [Fact]
    public async Task RatesIsKeyedByTheCode_AndLeavesTheTickersEntryAlone()
    {
        var asset = AssetRow(AssetA, "BTC", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
#pragma warning disable CS0618
        ctx.InvoiceEntity.Rates["BTC"] = 90_000m;

        await handler.ConfigurePrompt(ctx);

        var code = RgbPricingCode.For(AssetA);
        Assert.Equal(2m, ctx.InvoiceEntity.Rates[code]);
        Assert.Equal(90_000m, ctx.InvoiceEntity.Rates["BTC"]);
        // Exactly the seeded key plus the code: a ticker-keyed write would either overwrite the
        // Bitcoin rate above or add a third key here.
        Assert.Equal(new[] { "BTC", code }.Order(), ctx.InvoiceEntity.Rates.Keys.Order());
#pragma warning restore CS0618
    }

    // 27 — one derivation feeds the rate lookup, the prompt currency and the persisted prompt.
    [Fact]
    public async Task OneDerivation_FeedsEveryCurrencyIdentity()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var source = new RecordingRateSource(RgbRateResult.Ok(1m, "test"));
        var (handler, _) = Build(source, asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        var expected = RgbPricingCode.For(AssetA);
        Assert.Equal(expected, source.SeenPricingCode);
        Assert.Equal(expected, ctx.Prompt.Currency);
        var details = handler.ParsePaymentPromptDetails(ctx.Prompt.Details);
        Assert.Equal(expected, details.PricingCode);
        Assert.Equal(expected, RGBInvoiceListener.ResolvePaymentCurrency(details));
#pragma warning disable CS0618
        Assert.Equal(expected, Assert.Single(ctx.InvoiceEntity.Rates).Key);
#pragma warning restore CS0618
    }

    // 28 — a non-integral quotient, so ceiling and truncation differ and a dropped or constant rate
    // is visible. 100/3 at precision 2 = 3333.33…; the ceiling is 3334.
    [Fact]
    public async Task NonIntegralQuotient_RoundsUpAndRecordsTheFetchedRate()
    {
        var asset = AssetRow(AssetA, "USDT", 2);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(3m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        Assert.Equal(3334L, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).AmountInAssetUnits);
#pragma warning disable CS0618
        Assert.Equal(3m, ctx.InvoiceEntity.Rates[RgbPricingCode.For(AssetA)]);
#pragma warning restore CS0618
    }

    // 29 — the quantity that reaches the wire. Both compared to the same INDEPENDENT literal: comparing
    // them to each other only proves they agree, which an inline recomputation would also satisfy
    // while undercounting both.
    [Fact]
    public async Task TheQuantityOnTheWire_IsTheCeilingQuantity()
    {
        var asset = AssetRow(AssetA, "USDT", 2);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(3m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");

        await handler.ConfigurePrompt(ctx);

        Assert.Equal(3334L, wallets.RecordedAmount);
        Assert.Equal(3334L, handler.ParsePaymentPromptDetails(ctx.Prompt.Details).AmountInAssetUnits);
    }

    [Fact]
    public async Task ConfigurePrompt_ReplacesTheRatesInstance_BecauseEveryConcurrentPromptSharesTheOneItWasHanded()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
#pragma warning disable CS0618
        var sharedWithEverySiblingPrompt = ctx.InvoiceEntity.Rates;
        sharedWithEverySiblingPrompt["BTC"] = 90_000m;
        sharedWithEverySiblingPrompt["LTC"] = 80m;
        sharedWithEverySiblingPrompt["EUR"] = 1.1m;

        await handler.ConfigurePrompt(ctx);

        Assert.True(!ReferenceEquals(sharedWithEverySiblingPrompt, ctx.InvoiceEntity.Rates),
            "ConfigurePrompt mutated the dictionary instance it was handed. It runs on a post-await "
            + "continuation inside BTCPay's CreatePaymentPrompts phase, which is Task.WhenAll over every "
            + "payment method and hands all of them the SAME InvoiceEntity; an in-place insert bumps the "
            + "dictionary's version while a sibling's PaymentPrompt.Calculate is enumerating it through "
            + "RateBook's constructor, that sibling's context is marked Failed, and the invoice is issued "
            + "to the customer with the sibling's prompt silently missing");
        Assert.True(sharedWithEverySiblingPrompt.Count == 3,
            $"the dictionary siblings hold gained an entry ({sharedWithEverySiblingPrompt.Count} keys)");
        Assert.DoesNotContain(RgbPricingCode.For(AssetA), sharedWithEverySiblingPrompt.Keys);
        Assert.Equal(2m, ctx.InvoiceEntity.Rates[RgbPricingCode.For(AssetA)]);
        Assert.Equal(4, ctx.InvoiceEntity.Rates.Count);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task ASiblingEnumerationInFlightAcrossConfigurePrompt_Survives_WhereARateBookConstructorWouldHaveThrown()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(2m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
#pragma warning disable CS0618
        ctx.InvoiceEntity.Rates["BTC"] = 90_000m;
        ctx.InvoiceEntity.Rates["LTC"] = 80m;
        ctx.InvoiceEntity.Rates["EUR"] = 1.1m;

        var siblingEnumeration = ctx.InvoiceEntity.Rates.GetEnumerator();
#pragma warning restore CS0618
        Assert.True(siblingEnumeration.MoveNext(),
            "the sibling's enumeration must be in flight before ConfigurePrompt runs");

        await handler.ConfigurePrompt(ctx);

        var seen = 1;
        Exception? failure = null;
        try
        {
            while (siblingEnumeration.MoveNext())
                seen++;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Assert.True(failure is null,
            $"the sibling's in-flight enumeration threw {failure?.GetType().Name}. This is exactly what "
            + "BitcoinLikePaymentHandler.ConfigurePrompt hits: its own post-await continuation calls "
            + "PaymentPrompt.Calculate, which builds a RateBook, whose constructor does "
            + "'foreach (var rate in rates)' over this dictionary. BTCPay swallows that exception into "
            + "ContextStatus.Failed plus one invoice-log line, so the merchant ships an invoice with no "
            + "on-chain prompt and must void and reissue it");
        Assert.True(seen == 3, $"the sibling's enumeration saw {seen} of the 3 entries it started with");
    }

    [Theory]
    [InlineData(100, 3, 2, 3334L)]
    [InlineData(100, 2.5, 2, 4000L)]
    [InlineData(100, 3, 0, 34L)]
    [InlineData(7, 1.3, 4, 53847L)]
    public async Task TheUnitsDemanded_AreExactlyTheDueBTCPayDisplays_ScaledByTheAssetPrecision(
        decimal price, decimal rate, int precision, long expectedUnits)
    {
        var asset = AssetRow(AssetA, "USDT", precision);
        var (handler, wallets) = Build(new RecordingRateSource(RgbRateResult.Ok(rate, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: price, currency: "USD");

        await handler.ConfigurePrompt(ctx);
        ctx.InvoiceEntity.UpdateTotals();

        var scale = BigInteger.Pow(10, precision);
        var displayedDue = ctx.Prompt.Calculate().TotalDue;
        var displayedDueInUnits = (long)(displayedDue * (decimal)scale);

        Assert.True(wallets.RecordedAmount == expectedUnits,
            $"the RGB invoice demands {wallets.RecordedAmount} units, not the expected {expectedUnits}");
        Assert.True(displayedDueInUnits == expectedUnits,
            $"BTCPay shows {displayedDue} due, i.e. {displayedDueInUnits} units, while the RGB invoice "
            + $"demands {expectedUnits}. PaymentPrompt.Calculate reads the rate back out of "
            + "InvoiceEntity.Rates, so any divergence here means the invoice can settle for less than it "
            + "asked for");
    }

    [Fact]
    public async Task ARefusalAfterTheFetch_LeavesNoPricingEntryBehind_SoNoPartialRateIsPersisted()
    {
        var asset = AssetRow(AssetA, "USDT", 0);
        var (handler, _) = Build(new RecordingRateSource(RgbRateResult.Ok(0m, "test")), asset);
        var ctx = Context(Store(AssetA), handler, price: 100m, currency: "USD");
#pragma warning disable CS0618
        var before = ctx.InvoiceEntity.Rates;

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(ctx));

        Assert.True(ReferenceEquals(before, ctx.InvoiceEntity.Rates),
            "a refused invoice replaced the rate table anyway");
        Assert.Empty(ctx.InvoiceEntity.Rates);
#pragma warning restore CS0618
    }

    [Fact]
    public void TheRatesCopy_LeavesItsInputUntouched_AndCarriesEveryEntryForward()
    {
        var original = new Dictionary<string, decimal> { ["BTC"] = 90_000m, ["EUR"] = 1.1m };

        var copy = RGBPaymentMethodHandler.RatesCopyThatNoSiblingPromptCanBeEnumerating(
            original, "RGB2ABC", 7m);

        Assert.True(!ReferenceEquals(original, copy), "the copy is the input dictionary");
        Assert.Equal(2, original.Count);
        Assert.Equal(new[] { "BTC", "EUR", "RGB2ABC" }.Order(), copy.Keys.Order());
        Assert.Equal(90_000m, copy["BTC"]);
        Assert.Equal(1.1m, copy["EUR"]);
        Assert.Equal(7m, copy["RGB2ABC"]);
    }

    [Fact]
    public void TheRatesCopy_RefusesInsteadOfDroppingThePricingRate()
    {
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            RGBPaymentMethodHandler.RatesCopyThatNoSiblingPromptCanBeEnumerating(null, "RGB2ABC", 1m));
        Assert.Throws<PaymentMethodUnavailableException>(() =>
            RGBPaymentMethodHandler.RatesCopyThatNoSiblingPromptCanBeEnumerating(
                new Dictionary<string, decimal>(), "", 1m));
    }
}
