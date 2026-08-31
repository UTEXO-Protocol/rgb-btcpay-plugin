using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

internal static class TestStores
{
    // GetOrCreateRateSettings(false) rather than `blob.PrimaryRateSettings.RateScripting = true`:
    // PrimaryRateSettings is a nullable property with no initializer (StoreBlob.cs:172), so the
    // direct form NREs on a fresh blob.
    internal static StoreData StoreWithScript(string? script, bool scripting = true)
    {
        var store = new StoreData { Id = "test-store" };
        var blob = store.GetStoreBlob();
        var settings = blob.GetOrCreateRateSettings(false);
        settings.RateScripting = scripting;
        settings.RateScript = script;
        store.SetStoreBlob(blob);
        return store;
    }

    internal static StoreData StoreWithFallbackScript(string primary, string fallback)
    {
        var store = new StoreData { Id = "test-store" };
        var blob = store.GetStoreBlob();

        var primarySettings = blob.GetOrCreateRateSettings(false);
        primarySettings.RateScripting = true;
        primarySettings.RateScript = primary;

        var fallbackSettings = blob.GetOrCreateRateSettings(true);
        fallbackSettings.RateScripting = true;
        fallbackSettings.RateScript = fallback;

        store.SetStoreBlob(blob);
        return store;
    }
}

internal static class TestRateSource
{
    // A store on default rules resolves through this; no provider by this name is ever registered,
    // so the default path yields no BidAsk while constant script rules still evaluate.
    const string DefaultRuleText = "X_X = coingecko(X_X);";

    internal static IRgbRateSource WithNoExchanges() => Build([], budget: null);

    internal static IRgbRateSource ThatHangs() =>
        Build([new HangingRateProvider()], budget: TimeSpan.FromMilliseconds(50));

    // Exposes the token the provider was handed, so a test can prove the exchange call is actually
    // TORN DOWN when the budget expires rather than merely abandoned.
    internal static (IRgbRateSource Source, HangingRateProvider Provider) ThatHangsObservably()
    {
        var provider = new HangingRateProvider();
        return (Build([provider], budget: TimeSpan.FromMilliseconds(50)), provider);
    }

    internal static IRgbRateSource ThatThrows() => Build([new ThrowingRateProvider()], budget: null);

    static IRgbRateSource Build(IRateProvider[] providers, TimeSpan? budget)
    {
        var factory = new RateProviderFactory(null!, providers);
        var fetcher = new RateFetcher(factory);
        var defaults = new DefaultRulesCollection([new DefaultRules(DefaultRuleText)]);
        var log = NullLogger<RgbRateSource>.Instance;
        return budget is { } b
            ? new RgbRateSource(fetcher, defaults, log, b)
            : new RgbRateSource(fetcher, defaults, log);
    }

    // Both fakes implement IContextualRateProvider so RateProviderFactory uses them directly instead
    // of wrapping them in a caching BackgroundFetcherRateProvider, which would make the hang and the
    // throw arrive through an extra layer of its own timing.
    //
    // RateSourceInfo.Id MUST equal the exchange name used in the test's rule text: the factory keys
    // Providers by that id and silently substitutes NullRateProvider for any other name, which would
    // turn the budget test into a NoRate result with no indication why.
    internal sealed class HangingRateProvider : IContextualRateProvider
    {
        public CancellationToken SeenToken { get; private set; }

        public RateSourceInfo RateSourceInfo => new("hang", "Hanging", "");

        // Ignores the token deliberately: BTCPay's WrapperRateProvider swallows
        // OperationCanceledException, so a cooperative provider could not demonstrate that only the
        // WaitAsync budget bounds the call.
        public Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, CancellationToken.None).ContinueWith(_ => Array.Empty<PairRate>());

        public Task<PairRate[]> GetRatesAsync(IRateContext context, CancellationToken cancellationToken)
        {
            SeenToken = cancellationToken;
            return GetRatesAsync(cancellationToken);
        }
    }

    sealed class ThrowingRateProvider : IContextualRateProvider
    {
        public RateSourceInfo RateSourceInfo => new("boom", "Throwing", "");

        public Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("exchange is down");

        public Task<PairRate[]> GetRatesAsync(IRateContext context, CancellationToken cancellationToken) =>
            GetRatesAsync(cancellationToken);
    }
}
