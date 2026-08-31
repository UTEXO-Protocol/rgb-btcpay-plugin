using BTCPayServer.Data;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

// None is the zero value on purpose: RgbRateResult.Ok leaves Failure at default, and without it a
// SUCCESS would literally carry Failure == NoRate, making any future `Failure == NoRate` test wrong.
public enum RgbRateFailure { None, NoRate, Timeout, Error, NoRule }

public record RgbRateResult(bool IsOk, decimal Rate, string Source, RgbRateFailure Failure, bool PreferredSource)
{
    public static RgbRateResult Ok(decimal rate, string source) =>
        new(true, rate, source, default, false);

    public static RgbRateResult Failed(RgbRateFailure failure, bool preferredSource) =>
        new(false, 0m, "", failure, preferredSource);
}

public interface IRgbRateSource
{
    Task<RgbRateResult> FetchAsync(string pricingCode, string invoiceCurrency, StoreData store, CancellationToken ct);
}
