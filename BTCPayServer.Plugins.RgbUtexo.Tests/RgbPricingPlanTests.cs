using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingPlanTests
{
    const string Code = "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Build_ComputesUnitsFromRateAndPrecision()
    {
        var plan = RgbPricingPlan.Build(Code, precision: 2, invoicePrice: 100m, rate: 2.5m);
        Assert.Equal(4000L, plan.Units);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_RejectsNonPositiveRate(decimal rate)
    {
        Assert.Throws<PaymentMethodUnavailableException>(
            () => RgbPricingPlan.Build(Code, precision: 0, invoicePrice: 100m, rate: rate));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(-1)]
    [InlineData(-2)]
    public void Build_RejectsPrecisionOutsideRange(int precision)
    {
        Assert.Throws<PaymentMethodUnavailableException>(
            () => RgbPricingPlan.Build(Code, precision, invoicePrice: 100m, rate: 1m));
    }

    [Theory]
    [InlineData("USDT")]
    [InlineData("RGB")]
    [InlineData("RGB0123456789ABCDEF")]
    public void Build_RejectsAnythingThatIsNotAPricingCode(string code)
    {
        Assert.Throws<ArgumentException>(
            () => RgbPricingPlan.Build(code, precision: 0, invoicePrice: 100m, rate: 1m));
    }

    [Fact]
    public void Build_RejectsNegativePrice()
    {
        Assert.Throws<PaymentMethodUnavailableException>(
            () => RgbPricingPlan.Build(Code, precision: 0, invoicePrice: -1m, rate: 1m));
    }

    [Fact]
    public void Build_ZeroPriceYieldsOneUnit()
    {
        var plan = RgbPricingPlan.Build(Code, precision: 8, invoicePrice: 0m, rate: 1m);
        Assert.Equal(1L, plan.Units);
    }

    [Fact]
    public void Build_RejectsResultsBeyondLongRange()
    {
        // 1e20 units: above long.MaxValue (9.22e18). Under the BigInteger implementation nothing
        // can overflow, so the long.MaxValue ceiling is the only thing that may reject this.
        Assert.Throws<PaymentMethodUnavailableException>(
            () => RgbPricingPlan.Build(Code, precision: 0, invoicePrice: 100_000_000_000_000_000_000m, rate: 1m));
    }

    [Fact]
    public void Build_DoesNotRoundDownBeforeCeiling()
    {
        // Decimal division rounds this quotient to exactly 1m; a Ceiling applied afterwards cannot
        // recover the lost remainder and would demand one unit FEWER than priced.
        var plan = RgbPricingPlan.Build(Code, precision: 18,
            invoicePrice: 7m, rate: 6.9999999999999999999999999999m);
        Assert.Equal(1_000_000_000_000_000_001L, plan.Units);
    }

    [Fact]
    public void Build_RejectsAstronomicalResultsCleanly()
    {
        // Under the BigInteger implementation this cannot overflow; it must be rejected by the
        // long.MaxValue ceiling as a PaymentMethodUnavailableException, never escape as an
        // arithmetic exception.
        Assert.Throws<PaymentMethodUnavailableException>(
            () => RgbPricingPlan.Build(Code, precision: 18, invoicePrice: decimal.MaxValue / 2, rate: 0.00000001m));
    }

    [Fact]
    public void Build_AllCurrencyIdentitiesEqualThePricingCodeArgument()
    {
        var plan = RgbPricingPlan.Build(Code, precision: 0, invoicePrice: 10m, rate: 1m);
        Assert.Equal(Code, plan.PricingCode);
        Assert.Equal(Code, plan.PromptCurrency);
        Assert.Equal(Code, plan.RatesKey);
    }

    [Fact]
    public void Build_HasNoTickerParameter()
    {
        var parameters = typeof(RgbPricingPlan)
            .GetMethod(nameof(RgbPricingPlan.Build))!
            .GetParameters()
            .Select(p => p.Name);
        Assert.DoesNotContain("ticker", parameters);
    }
}
