using BTCPayServer.Data;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

// Extracted from the controller so the merchant's remediation surface is testable. It sits behind a
// catch-all that renders nothing on any exception, so a defect here would be silent in exactly the
// state it exists to explain: the merchant is refused at invoice time and the settings page shows
// nothing wrong.
public record RgbPricingNotice(
    string? PricingCode,
    string? SuggestedRateRule,
    string? SuggestedPegRule,
    string? QuoteCurrency,
    bool RateRuleMissing,
    bool UsesDefaultRules,
    bool RateUnresolved = false)
{
    public static readonly RgbPricingNotice None = new(null, null, null, null, false, false);

    public static RgbPricingNotice For(string? selectedAssetId, string? quoteCurrency, RgbRateResult? probe)
    {
        // IsNullOrWhiteSpace, not IsNullOrEmpty: RgbPricingCode.For throws on whitespace, and a store
        // that has not picked an asset yet is a state the settings view explicitly supports.
        if (string.IsNullOrWhiteSpace(selectedAssetId) || string.IsNullOrWhiteSpace(quoteCurrency))
            return None;

        var code = RgbPricingCode.For(selectedAssetId);

        // Only NoRule accuses the store's CONFIGURATION. NoRate is a rule that names the pair with no
        // rate behind it: still reported, never blamed on the merchant. Timeout and Error are one-shot
        // failures of this page's own probe, so they report nothing at all.
        var missing = probe is { IsOk: false, Failure: RgbRateFailure.NoRule };
        var unresolved = probe is { IsOk: false, Failure: RgbRateFailure.NoRate };

        return new RgbPricingNotice(
            code,
            // NEVER a runnable market reference. A concrete example such as kraken(USDT_USD) is
            // copy-pasteable and would price THIS contract at THAT asset's rate — finding E's own
            // harm, recommended by the plugin.
            $"{code}_{quoteCurrency} = <exchange>(<MARKET>);",
            $"{code}_{quoteCurrency} = 1;",
            quoteCurrency,
            missing,
            missing && probe!.PreferredSource,
            unresolved);
    }

    // The rate rules are part of the probe's cache key, not just the pair: this notice is what a
    // merchant reloads to confirm they have FIXED their rule, and rates are edited on a different
    // controller with no invalidation hook.
    public static string RateRulesFingerprint(StoreBlob blob) =>
        // \u001f separates fields so no value can impersonate a boundary.
        string.Join('\u001f',
            blob.PrimaryRateSettings?.RateScripting == true ? blob.PrimaryRateSettings.RateScript : "",
            blob.FallbackRateSettings?.RateScripting == true ? blob.FallbackRateSettings.RateScript : "",
            blob.PrimaryRateSettings?.PreferredExchange,
            blob.FallbackRateSettings?.PreferredExchange,
            blob.Spread);
}
