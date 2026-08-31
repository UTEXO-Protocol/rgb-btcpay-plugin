using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCurrencyDataProviderTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";

    static RGBAsset Asset(string assetId, string ticker = "", string name = "Token",
        int precision = 0, string walletId = "w1") =>
        new() { AssetId = assetId, WalletId = walletId, Ticker = ticker, Name = name, Precision = precision };

    static CurrencyData[] Build(params RGBAsset[] assets) =>
        RgbCurrencyDataProvider.BuildCurrencies(assets, RgbPricingCode.For);

    static CurrencyData? Find(CurrencyData[] currencies, string code) =>
        currencies.FirstOrDefault(c => c.Code == code);

    // One malformed RGB_Assets row must cost only its own entry. Before the per-row guard,
    // CanonicalizeAssetId's ArgumentException escaped BuildCurrencies into LoadCurrencyData's catch,
    // which returns just [{Code="RGB"}] — so a single bad row in any wallet on the server dropped EVERY
    // contract pricing code, and pricing is keyed on those codes, so RGB stopped being offered on every
    // store on the instance.
    [Fact]
    public void OneUnparseableAssetRow_CostsOnlyItsOwnCurrencyEntry()
    {
        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [
                Asset(AssetA, ticker: "AAA"),
                Asset("not-a-contract-id", ticker: "BAD", walletId: "w2"),
                Asset(AssetB, ticker: "BBB")
            ],
            RgbPricingCode.For);

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetB)));
        Assert.NotNull(Find(currencies, "RGB"));
    }

    [Fact]
    public void UnparseableAssetRow_IsReportedOnceWithItsWalletAndAssetId()
    {
        var reported = new List<(string WalletId, string AssetId)>();

        RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "AAA"), Asset("::not-a-contract::", walletId: "w2")],
            RgbPricingCode.For,
            onUnparseableAssetId: (walletId, assetId) => reported.Add((walletId, assetId)));

        Assert.Equal([("w2", "::not-a-contract::")], reported);
    }

    [Theory]
    [InlineData("not-a-contract-id")]
    [InlineData("::")]
    [InlineData("rgb:")]
    [InlineData("rgb:!!!!")]
    [InlineData("rgb:AAAA")]
    [InlineData("rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw2dHQy")]
    public void UnparseableAssetRow_DoesNotThrowOutOfBuildCurrencies(string assetId)
    {
        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(assetId, walletId: "w2"), Asset(AssetA, ticker: "AAA")],
            RgbPricingCode.For);

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 30
    [Fact]
    public void DerivedCode_IsRegisteredWithTheAssetsDivisibility()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 8));

        var entry = Find(currencies, RgbPricingCode.For(AssetA));
        Assert.NotNull(entry);
        Assert.Equal(8, entry!.Divisibility);
        Assert.True(entry.Crypto);
    }

    // 31 — registration is unconditional; the ticker is not a precondition for being priceable.
    [Fact]
    public void TicklerlessAsset_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: ""));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 32 — a reserved ticker blocks only the raw-ticker entry, never the contract's own code.
    [Fact]
    public void AssetWithAReservedTicker_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: "USD"));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // 33 — the ticker-dedup must not swallow the second contract's code.
    [Fact]
    public void SecondAssetSharingATicker_StillRegistersItsDerivedCode()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT"), Asset(AssetB, ticker: "USDT"));

        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetB)));
    }

    // 34 — the pricing-code namespace is reserved against issuer-chosen tickers, in either case. [T2]
    [Theory]
    [InlineData("RGB0123456789ABCDEF")]
    [InlineData("rgb0123456789abcdef")]
    [InlineData("RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rgb2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ATickerShapedLikeAPricingCode_IsRefused_ButTheAssetStillGetsItsOwnCode(string ticker)
    {
        var currencies = Build(Asset(AssetA, ticker: ticker));

        Assert.Null(Find(currencies, ticker.ToUpperInvariant()));
        Assert.NotNull(Find(currencies, RgbPricingCode.For(AssetA)));
    }

    // A collision removes every claimant from the registry. Keeping the first owner would still
    // advertise an ambiguous identity to rate and formatting consumers.
    [Fact]
    public void TwoAssetIdsMappingToOneCode_RegisterNeitherAndReportTheCollision()
    {
        var collisions = new List<(string Code, string Owner, string Other)>();

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "AAA"), Asset(AssetB, ticker: "BBB")],
            _ => "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            (code, owner, other) => collisions.Add((code, owner, other)));

        var collision = Assert.Single(collisions);
        Assert.Equal(AssetA, collision.Owner);
        Assert.Equal(AssetB, collision.Other);
        Assert.DoesNotContain(currencies,
            c => c.Code == "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    }

    // 36 — RGB_Assets is keyed (WalletId, AssetId), so one contract held in two wallets is one asset
    // and must NOT be reported as a collision.
    [Fact]
    public void SameAssetInTwoWallets_IsOneEntryAndNoCollision()
    {
        var collisions = new List<string>();

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, ticker: "USDT", walletId: "w1"), Asset(AssetA, ticker: "USDT", walletId: "w2")],
            RgbPricingCode.For,
            (code, _, _) => collisions.Add(code));

        Assert.Empty(collisions);
        Assert.Single(currencies, c => c.Code == RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void EquivalentContractIdTextInTwoWallets_IsOneEntryAndNoCollision()
    {
        var collisions = new List<string>();
        var compact = AssetA[4..].Replace("-", "");

        var currencies = RgbCurrencyDataProvider.BuildCurrencies(
            [Asset(AssetA, walletId: "w1"), Asset(compact, walletId: "w2")],
            RgbPricingCode.For,
            (code, _, _) => collisions.Add(code));

        Assert.Empty(collisions);
        Assert.Single(currencies, c => c.Code == RgbPricingCode.For(AssetA));
    }

    // Raw ticker metadata remains display-only for already-recorded historical payments. Current
    // pricing and listener registration never consume it.
    [Fact]
    public void RawTickerRegistration_StillHappens()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 2));

        var entry = Find(currencies, "USDT");
        Assert.NotNull(entry);
        Assert.Equal(2, entry!.Divisibility);
    }

    [Fact]
    public void HistoricalTickerCanRenderButCannotAuthorizeANewPayment()
    {
        var currencies = Build(Asset(AssetA, ticker: "USDT", precision: 2));
        Assert.NotNull(Find(currencies, "USDT"));

        var details = new RGBPromptDetails
        {
            AssetId = AssetA, AssetTicker = "USDT", PricingCode = null
        };
        var outcome = RGBInvoiceListener.ClassifyPromptPricingIdentity(
            new RGBInvoice { AssetId = AssetA }, details, out _);

        Assert.Equal(RGBInvoiceListener.PaymentRegistration.Unregisterable, outcome);
    }

    [Fact]
    public void TheGenericRgbEntry_IsAlwaysPresent()
    {
        Assert.NotNull(Find(Build(), "RGB"));
    }
}
