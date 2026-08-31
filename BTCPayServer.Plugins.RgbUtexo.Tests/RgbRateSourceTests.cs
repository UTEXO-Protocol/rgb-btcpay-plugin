using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Rates;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRateSourceTests
{
    const string Code = "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // StoreWithScript and TestRateSource live in the shared Tests/TestRateSource.cs;
    // this alias keeps the cases readable.
    static StoreData StoreWithScript(string? script, bool scripting = true) =>
        TestStores.StoreWithScript(script, scripting);

    [Fact]
    public async Task ConstantRule_ResolvesWithNoExchangeAvailableAtAll()
    {
        // No providers are registered, so resolving at all proves the constant rule needed no exchange.
        // (An earlier draft asserted a call counter; RateProviderFactory substitutes NullRateProvider
        // for any unknown name, so the counter could never increment and the assertion was vacuous.)
        var source = TestRateSource.WithNoExchanges();
        var result = await source.FetchAsync(Code, "USD", StoreWithScript($"{Code}_USD = 1;"), default);
        Assert.True(result.IsOk);
        Assert.Equal(1m, result.Rate);
    }

    [Fact]
    public async Task ConstantRule_DoesNotSatisfyADifferentQuoteCurrency()
    {
        var source = TestRateSource.WithNoExchanges();
        var result = await source.FetchAsync(Code, "EUR", StoreWithScript($"{Code}_USD = 1;"), default);
        Assert.False(result.IsOk);
        Assert.Equal(RgbRateFailure.NoRule, result.Failure);
    }

    [Fact]
    public async Task WildcardRule_CannotPriceAContractWithoutAnExplicitPair()
    {
        var result = await TestRateSource.WithNoExchanges().FetchAsync(
            Code, "USD", StoreWithScript("X_X = 1;"), default);

        Assert.False(result.IsOk);
        Assert.Equal(RgbRateFailure.NoRule, result.Failure);
    }

    [Fact]
    public async Task ExplicitInverseRule_IsAccepted()
    {
        var result = await TestRateSource.WithNoExchanges().FetchAsync(
            Code, "USD", StoreWithScript($"USD_{Code} = 0.5;"), default);

        Assert.True(result.IsOk);
        Assert.Equal(2m, result.Rate);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "this is not a rate script {{{")]
    public async Task DefaultRuleStores_ReportPreferredSource(bool scripting, string script)
    {
        var source = TestRateSource.WithNoExchanges();
        var result = await source.FetchAsync(Code, "USD", StoreWithScript(script, scripting), default);
        Assert.False(result.IsOk);
        Assert.True(result.PreferredSource);
    }

    [Fact]
    public async Task ScriptedStoreWithoutAMatchingRule_DoesNotReportPreferredSource()
    {
        var source = TestRateSource.WithNoExchanges();
        var result = await source.FetchAsync(Code, "USD", StoreWithScript("USDT_USD = 1;"), default);
        Assert.False(result.IsOk);
        Assert.False(result.PreferredSource);
    }

    [Fact]
    public async Task FallbackSettingsRule_IsHonoured()
    {
        var store = TestStores.StoreWithFallbackScript(
            primary: "USDT_USD = 1;", fallback: $"{Code}_USD = 3;");

        var result = await TestRateSource.WithNoExchanges().FetchAsync(Code, "USD", store, default);
        Assert.True(result.IsOk);
        Assert.Equal(3m, result.Rate);
    }

    [Fact]
    public async Task ExchangeThatHangs_YieldsTimeoutNotAnException()
    {
        var source = TestRateSource.ThatHangs();
        var result = await source.FetchAsync(Code, "USD", StoreWithScript($"{Code}_USD = hang(X_X);"), default);
        Assert.False(result.IsOk);
        Assert.Equal(RgbRateFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task ExchangeThatThrows_IsReportedAsNoRate_NotAsAnEscapingException()
    {
        // WrapperRateProvider swallows provider exceptions, so this legitimately surfaces as NoRate.
        var source = TestRateSource.ThatThrows();
        var result = await source.FetchAsync(Code, "USD", StoreWithScript($"{Code}_USD = boom(X_X);"), default);
        Assert.False(result.IsOk);
        Assert.Equal(RgbRateFailure.NoRate, result.Failure);
    }
}

// Two properties with no coverage before the final independent review found them missing.
public class RgbRateSourceCancellationTests
{
    const string Code = "RGB2AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    // The budget must TEAR DOWN the exchange call, not merely stop waiting for it. The pre-change
    // code passed a 5s CancellationTokenSource into FetchRate; abandoning instead would leave one
    // live provider call per invoice attempt.
    [Fact]
    public async Task WhenTheBudgetExpires_TheProviderCallIsCancelled()
    {
        var (source, provider) = TestRateSource.ThatHangsObservably();

        var result = await source.FetchAsync(
            Code, "USD", TestStores.StoreWithScript($"{Code}_USD = hang(X_X);"), default);

        Assert.Equal(RgbRateFailure.Timeout, result.Failure);
        Assert.True(provider.SeenToken.CanBeCanceled, "the provider was handed a non-cancellable token");
        Assert.True(provider.SeenToken.IsCancellationRequested,
            "the provider's call was abandoned but never cancelled");
    }

    // A constant rule resolves synchronously, and Task.WaitAsync's completed-task fast path does not
    // observe the token — so without an explicit check an already-cancelled caller got a rate back.
    [Fact]
    public async Task AnAlreadyCancelledCaller_IsNotHandedARate()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestRateSource.WithNoExchanges().FetchAsync(
                Code, "USD", TestStores.StoreWithScript($"{Code}_USD = 1;"), cts.Token));
    }
}
