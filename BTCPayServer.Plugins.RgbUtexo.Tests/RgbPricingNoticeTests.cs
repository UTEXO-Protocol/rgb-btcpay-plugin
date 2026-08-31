using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The merchant's remediation surface. It sits behind a catch-all that renders nothing on any
// exception, so without these tests a defect here is silent in exactly the state it exists to
// explain: refused at invoice time, settings page showing nothing wrong.
public class RgbPricingNoticeTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";

    static RgbRateResult NoRule(bool preferredSource) =>
        RgbRateResult.Failed(RgbRateFailure.NoRule, preferredSource);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoAssetSelected_RendersNothing(string? assetId)
    {
        Assert.Equal(RgbPricingNotice.None, RgbPricingNotice.For(assetId, "USD", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoQuoteCurrency_RendersNothing(string? quote)
    {
        Assert.Equal(RgbPricingNotice.None, RgbPricingNotice.For(AssetA, quote, null));
    }

    [Fact]
    public void SelectedAsset_YieldsItsCodeAndBothRuleForms()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", null);
        var code = RgbPricingCode.For(AssetA);

        Assert.Equal(code, notice.PricingCode);
        Assert.Equal("USD", notice.QuoteCurrency);
        Assert.Equal($"{code}_USD = <exchange>(<MARKET>);", notice.SuggestedRateRule);
        Assert.Equal($"{code}_USD = 1;", notice.SuggestedPegRule);
    }

    // The emitted rule must never be runnable: a concrete market would price THIS contract at THAT
    // asset's rate — finding E's own harm, recommended by the plugin.
    [Fact]
    public void TheEmittedRule_IsNotRunnable()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", null);

        Assert.Contains("<exchange>", notice.SuggestedRateRule);
        Assert.Contains("<MARKET>", notice.SuggestedRateRule);
        Assert.False(BTCPayServer.Rating.RateRules.TryParse(notice.SuggestedRateRule!, out _));
        // The peg form IS runnable — it is an assertion the merchant makes deliberately.
        Assert.True(BTCPayServer.Rating.RateRules.TryParse(notice.SuggestedPegRule!, out _));
    }

    [Fact]
    public void NoRuleOnDefaultRules_ReportsBothTheMissingRuleAndTheScriptingCause()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", NoRule(preferredSource: true));

        Assert.True(notice.RateRuleMissing);
        Assert.True(notice.UsesDefaultRules);
    }

    [Fact]
    public void NoRuleOnAScriptedStore_ReportsTheMissingRuleOnly()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", NoRule(preferredSource: false));

        Assert.True(notice.RateRuleMissing);
        Assert.False(notice.UsesDefaultRules);
    }

    // Timeout and Error are one-shot failures of THIS page's probe, not evidence about the store, so
    // they must accuse nothing and report nothing.
    [Theory]
    [InlineData(RgbRateFailure.Timeout)]
    [InlineData(RgbRateFailure.Error)]
    public void ATransientProbeFailure_AccusesNothingAndReportsNothing(RgbRateFailure failure)
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", RgbRateResult.Failed(failure, preferredSource: true));

        Assert.False(notice.RateRuleMissing);
        Assert.False(notice.UsesDefaultRules);
        Assert.False(notice.RateUnresolved);
    }

    // NoRate means a rule DOES name the pair and the source returned nothing for it. It must never
    // accuse the merchant's rules — but it MUST still be reported, because checkout is refused either
    // way and this page is the only pull surface. Before the NoRule split this state rendered the
    // rule-missing alert; dropping it entirely would leave RGB unofferable with the page showing
    // nothing wrong, which is the exact silent outage the notice work exists to remove.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoRate_ReportsAnUnresolvedRateWithoutAccusingTheRules(bool preferredSource)
    {
        var notice = RgbPricingNotice.For(
            AssetA, "USD", RgbRateResult.Failed(RgbRateFailure.NoRate, preferredSource));

        Assert.True(notice.RateUnresolved,
            "a rule that names the pair but resolves no rate is reported nowhere else in the plugin: "
            + "no notice cause is raised for it and checkout only says the source returned nothing.");
        Assert.False(notice.RateRuleMissing);
        Assert.False(notice.UsesDefaultRules);
    }

    [Fact]
    public void NoRule_DoesNotAlsoReportAnUnresolvedRate()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", NoRule(preferredSource: false));

        Assert.True(notice.RateRuleMissing);
        Assert.False(notice.RateUnresolved);
    }

    [Fact]
    public void AResolvedRate_AccusesNothing()
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", RgbRateResult.Ok(2.5m, "test"));

        Assert.False(notice.RateRuleMissing);
        Assert.False(notice.UsesDefaultRules);
        Assert.False(notice.RateUnresolved);
    }

    // Every refusal a merchant can reach from ConfigurePrompt must light up at least one alert on this
    // page, or the page's whole purpose — explaining a refused checkout — fails for that state.
    [Theory]
    [InlineData(RgbRateFailure.NoRule)]
    [InlineData(RgbRateFailure.NoRate)]
    public void EveryConfigurationRelevantRefusal_LightsUpAnAlert(RgbRateFailure failure)
    {
        var notice = RgbPricingNotice.For(AssetA, "USD", RgbRateResult.Failed(failure, false));

        Assert.True(notice.RateRuleMissing || notice.RateUnresolved,
            $"{failure} refuses the invoice but renders no alert on the settings page, so the merchant "
            + "sees a store that cannot take RGB payments and a settings page reporting nothing.");
    }

    // The probe is cached for 60s; rates are edited on a different controller with no invalidation
    // hook, so the fingerprint is what stops a fixed rule still showing "no rate could be resolved".
    [Fact]
    public void Fingerprint_ChangesWhenTheRateScriptChanges()
    {
        var before = TestStores.StoreWithScript("USDT_USD = 1;").GetStoreBlob();
        var after = TestStores.StoreWithScript("USDT_USD = 2;").GetStoreBlob();

        Assert.NotEqual(RgbPricingNotice.RateRulesFingerprint(before),
                        RgbPricingNotice.RateRulesFingerprint(after));
    }

    [Fact]
    public void Fingerprint_ChangesWhenScriptingIsToggled()
    {
        var on = TestStores.StoreWithScript("USDT_USD = 1;", scripting: true).GetStoreBlob();
        var off = TestStores.StoreWithScript("USDT_USD = 1;", scripting: false).GetStoreBlob();

        Assert.NotEqual(RgbPricingNotice.RateRulesFingerprint(on),
                        RgbPricingNotice.RateRulesFingerprint(off));
    }

    [Fact]
    public void Fingerprint_IsStableForAnUnchangedStore()
    {
        var a = TestStores.StoreWithScript("USDT_USD = 1;").GetStoreBlob();
        var b = TestStores.StoreWithScript("USDT_USD = 1;").GetStoreBlob();

        Assert.Equal(RgbPricingNotice.RateRulesFingerprint(a),
                     RgbPricingNotice.RateRulesFingerprint(b));
    }

    [Fact]
    public void Fingerprint_ChangesWhenTheSpreadChanges()
    {
        var blob = TestStores.StoreWithScript("USDT_USD = 1;").GetStoreBlob();
        var before = RgbPricingNotice.RateRulesFingerprint(blob);
        blob.Spread = 0.02m;

        Assert.NotEqual(before, RgbPricingNotice.RateRulesFingerprint(blob));
    }
}

// Extends RgbPricingNoticeTests: the fingerprint must cover EVERY field StoreBlob.GetRateRules reads
// (primary + fallback RateScript/RateScripting/PreferredExchange, and Spread). Round 3 caught an
// omission here once; deleting a line must not stay green.
public class RgbPricingFingerprintCoverageTests
{
    static StoreBlobPair Pair(string script = "USDT_USD = 1;") =>
        new(TestStores.StoreWithScript(script).GetStoreBlob(), TestStores.StoreWithScript(script).GetStoreBlob());

    record StoreBlobPair(BTCPayServer.Data.StoreBlob A, BTCPayServer.Data.StoreBlob B);

    [Fact]
    public void PrimaryPreferredExchange_IsCovered()
    {
        var (a, b) = Pair();
        b.GetOrCreateRateSettings(false).PreferredExchange = "kraken";

        Assert.NotEqual(RgbPricingNotice.RateRulesFingerprint(a), RgbPricingNotice.RateRulesFingerprint(b));
    }

    [Fact]
    public void FallbackPreferredExchange_IsCovered()
    {
        var (a, b) = Pair();
        b.GetOrCreateRateSettings(true).PreferredExchange = "coingecko";

        Assert.NotEqual(RgbPricingNotice.RateRulesFingerprint(a), RgbPricingNotice.RateRulesFingerprint(b));
    }

    [Fact]
    public void FallbackRateScript_IsCovered()
    {
        var a = TestStores.StoreWithFallbackScript("USDT_USD = 1;", "RGB0123456789ABCDEF_USD = 3;").GetStoreBlob();
        var b = TestStores.StoreWithFallbackScript("USDT_USD = 1;", "RGB0123456789ABCDEF_USD = 4;").GetStoreBlob();

        Assert.NotEqual(RgbPricingNotice.RateRulesFingerprint(a), RgbPricingNotice.RateRulesFingerprint(b));
    }

    [Fact]
    public void AResolvedRate_CarriesNoFailureKind()
    {
        Assert.Equal(RgbRateFailure.None, RgbRateResult.Ok(2.5m, "test").Failure);
    }
}
