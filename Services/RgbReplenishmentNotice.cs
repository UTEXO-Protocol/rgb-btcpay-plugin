namespace BTCPayServer.Plugins.RgbUtexo.Services;

public enum RgbReplenishmentNoticeCause
{
    None,
    ConfigOutOfBounds,
    CapDisabledDeploymentWide,
    NotAuthorized,
    PricingCodeHasNoRule
}

public static class RgbReplenishmentNotice
{
    public static RgbReplenishmentNoticeCause Evaluate(
        bool paymentMethodEnabled,
        bool hasStoredConfig,
        bool configValuesValid,
        int maxAutoColorableUtxos,
        bool standingAuthorizationGranted)
    {
        if (!paymentMethodEnabled || !hasStoredConfig) return RgbReplenishmentNoticeCause.None;
        if (!configValuesValid) return RgbReplenishmentNoticeCause.ConfigOutOfBounds;
        if (maxAutoColorableUtxos <= 0) return RgbReplenishmentNoticeCause.CapDisabledDeploymentWide;
        if (!standingAuthorizationGranted) return RgbReplenishmentNoticeCause.NotAuthorized;
        return RgbReplenishmentNoticeCause.None;
    }

    public static bool InvitesGrant(RgbReplenishmentNoticeCause cause)
        => cause == RgbReplenishmentNoticeCause.NotAuthorized;

    public static bool LogsPerSweep(RgbReplenishmentNoticeCause cause)
        => cause == RgbReplenishmentNoticeCause.NotAuthorized;

    public static string MessageFor(RgbReplenishmentNoticeCause cause) => cause switch
    {
        RgbReplenishmentNoticeCause.ConfigOutOfBounds =>
            "This store's saved RGB configuration is out of range, so automatic colorable-UTXO creation is refused and RGB payments will stop once the current pool is exhausted. Re-save the RGB settings to bring the values back into range.",
        RgbReplenishmentNoticeCause.CapDisabledDeploymentWide =>
            "Automatic colorable-UTXO creation is disabled for this whole deployment (RGB_MAX_AUTO_COLORABLE_UTXOS is 0), so RGB payments will stop once the current pool is exhausted. A store-level authorization will not change that. Press Create UTXOs to provision manually, or ask the host operator to raise the cap.",
        RgbReplenishmentNoticeCause.NotAuthorized =>
            "Automatic colorable-UTXO creation is not authorized for this store, so RGB payments will stop once the current pool is exhausted. Granting standing authorization is required, and may not be sufficient — this page reports any other blocking condition. Or press Create UTXOs to provision manually.",
        RgbReplenishmentNoticeCause.PricingCodeHasNoRule =>
            "RGB is unavailable at checkout: no exchange rate rule names this store's RGB pricing code against its default currency, so RGB cannot be offered on invoices priced in that currency. Whether such an invoice is still created depends on your other payment methods — creation fails only when none of them ends up priced or awaiting activation. Pricing is now bound to the contract id rather than the asset ticker, so a rule written against a ticker no longer matches. Open the RGB settings page — it prints the exact pricing code and a rule to paste. An invoice created in any other currency needs its own rule for that pair.",
        _ => ""
    };
}
