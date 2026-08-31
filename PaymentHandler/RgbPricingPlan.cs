using System.Numerics;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public record RgbPricingPlan(string PricingCode, long Units, string PromptCurrency, string RatesKey)
{
    public const int MaxPrecision = 18;

    public static RgbPricingPlan Build(string pricingCode, int precision, decimal invoicePrice, decimal rate)
    {
        // Validate the code's SHAPE, not just its presence. Scope, stated precisely because an
        // earlier draft overclaimed it: this catches substitution of a raw ticker and legacy code,
        // but shape alone cannot prove which contract produced the value. That binding is established
        // by ConfigurePrompt's derivation and collision guard and rechecked by the listener.
        if (!RgbPricingCode.IsCurrentPricingCode(pricingCode))
            throw new ArgumentException(
                $"'{pricingCode}' is not a pricing code; pricing must be contract-derived", nameof(pricingCode));
        if (invoicePrice < 0m)
            // Same shape as the precision guard: the `invoicePrice > 0 ? … : 1L` ternary silently
            // yields 1 unit for a negative price. BTCPay rejects negatives upstream, but round 17
            // established that a reachability argument is not an acceptable substitute for a guard.
            throw new PaymentMethodUnavailableException($"Invoice price {invoicePrice} is negative");
        if (rate <= 0m)
            throw new PaymentMethodUnavailableException($"Exchange rate for {pricingCode} is not positive");
        if (precision is < 0 or > MaxPrecision)
            // A negative precision is a DIRECT false-ACCEPT, not a curiosity: RGBAsset.Precision is a
            // plain int, and -2 makes the multiplier 0.01, so a 100-unit invoice at rate 2.5 demands
            // Ceiling(0.4) = 1 unit. Round 12 waved this off as unreachable via rgb-lib's u8; the
            // guard is one token and the invariant does not permit the bet.
            throw new PaymentMethodUnavailableException(
                $"Asset precision {precision} is outside the supported range (0..{MaxPrecision})");

        if (invoicePrice == 0m)
            return new RgbPricingPlan(pricingCode, 1L, pricingCode, pricingCode);

        // EXACT integer ceiling — do NOT reintroduce `invoicePrice / rate * multiplier` with
        // Math.Ceiling. Decimal division rounds at ~28 significant digits, and Ceiling cannot recover
        // a remainder that has already been rounded away: price 7m over rate
        // 6.9999999999999999999999999999m at precision 18 collapses to exactly 1m, demanding
        // 1_000_000_000_000_000_000 units where the true ceiling is …_001 — one unit FEWER than
        // priced, i.e. a false-ACCEPT. Found by the final codex review; reordering the operands does
        // not fix it, because the quotient still exceeds decimal's significand. BigInteger has no
        // rounding, so the ceiling is exact by construction.
        // Verified empirically before this plan shipped: compiled and run standalone on net10.0, this
        // returns 1_000_000_000_000_000_001 for the codex case where the old formula returned …_000,
        // and leaves 100/2.5@p2=4000, 100/3@p2=3334, 100/1@p0=100 and price-0=1 unchanged.
        var (priceUnscaled, priceScale) = Unscale(invoicePrice);
        var (rateUnscaled, rateScale)   = Unscale(rate);

        var numerator   = priceUnscaled * BigInteger.Pow(10, precision + rateScale);
        var denominator = rateUnscaled * BigInteger.Pow(10, priceScale);
        // Both are strictly positive here (price > 0 and rate > 0 are guarded above), so this is the
        // standard positive-operand ceiling division.
        var units = (numerator + denominator - BigInteger.One) / denominator;

        if (units > long.MaxValue)
            throw new PaymentMethodUnavailableException($"Calculated amount exceeds maximum ({units} units)");

        return new RgbPricingPlan(pricingCode, (long)units, pricingCode, pricingCode);
    }

    // decimal is a scaled integer: value == unscaled / 10^scale. GetBits exposes both exactly.
    static (BigInteger Unscaled, int Scale) Unscale(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        var scale = (bits[3] >> 16) & 0xFF;
        var magnitude = new BigInteger(new decimal(bits[0], bits[1], bits[2], false, 0));
        return (magnitude, scale);
    }
}
