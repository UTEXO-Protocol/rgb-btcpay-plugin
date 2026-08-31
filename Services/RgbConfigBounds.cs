namespace BTCPayServer.Plugins.RgbUtexo.Services;

// WHY const rather than static readonly: these are used as [Range] attribute arguments and in Razor
// literals, both of which require compile-time constants.
// WHY bounds only and no defaults: a default is not a security bound. Deduplicating the defaults was
// considered and deliberately rejected as scope creep.
public static class RgbConfigBounds
{
    public const int UtxoCountMin = 1;
    public const int UtxoCountMax = 20;

    public const int UtxoSizeMin = 546;
    public const int UtxoSizeMax = 100_000;

    public const int AllocationsPerUtxoMin = 1;
    public const int AllocationsPerUtxoMax = 50;

    public const int MinConfirmationsMin = 1;
    public const int MinConfirmationsMax = 100;

    public static bool ArePaymentMethodValuesValid(
        int utxoCount, int utxoSize, int minConfirmations) =>
        utxoCount is >= UtxoCountMin and <= UtxoCountMax
        && utxoSize is >= UtxoSizeMin and <= UtxoSizeMax
        && minConfirmations is >= MinConfirmationsMin and <= MinConfirmationsMax;

    public static void EnsurePaymentMethodValuesValid(
        int utxoCount, int utxoSize, int minConfirmations)
    {
        if (!ArePaymentMethodValuesValid(utxoCount, utxoSize, minConfirmations))
            throw new InvalidOperationException(
                "Stored RGB configuration is outside the supported safety bounds; save valid store settings before continuing");
    }
}
