using BTCPayServer.Data;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RgbRateSource : IRgbRateSource
{
    static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    readonly RateFetcher _rateFetcher;
    readonly DefaultRulesCollection _defaultRules;
    readonly ILogger<RgbRateSource> _log;
    readonly TimeSpan _budget;

    public RgbRateSource(RateFetcher rateFetcher, DefaultRulesCollection defaultRules, ILogger<RgbRateSource> log)
        : this(rateFetcher, defaultRules, log, DefaultBudget) { }

    internal RgbRateSource(RateFetcher rateFetcher, DefaultRulesCollection defaultRules,
        ILogger<RgbRateSource> log, TimeSpan budget)
    {
        _rateFetcher = rateFetcher;
        _defaultRules = defaultRules;
        _log = log;
        _budget = budget;
    }

    public async Task<RgbRateResult> FetchAsync(string pricingCode, string invoiceCurrency, StoreData store, CancellationToken ct)
    {
        // EVERYTHING is inside the try: this method's contract is that it never throws for an
        // unavailable rate, and GetStoreBlob/GetRateRules can throw on malformed persisted state
        // (an invalid Spread makes RateRules.Spread throw). Work outside the try would escape as an
        // exception and 500 the settings probe in task 9.
        var preferredSource = false;
        try
        {
            // WaitAsync's completed-task fast path returns without observing the token, so a caller
            // that cancelled before a synchronously-resolving constant rule would otherwise get a rate.
            ct.ThrowIfCancellationRequested();

            var storeBlob = store.GetStoreBlob();
            // Advisory only: it selects the refusal wording, never a pricing decision.
            (storeBlob.PrimaryRateSettings ?? new())
                .GetRateRules(_defaultRules, storeBlob.Spread, out preferredSource);

            var pair = new CurrencyPair(pricingCode, invoiceCurrency);
            if (!RgbPricingCode.IsCurrentPricingCode(pricingCode))
                return RgbRateResult.Failed(RgbRateFailure.NoRule, preferredSource);

            var hasPrimary = TryGetExplicitRules(storeBlob.PrimaryRateSettings, storeBlob.Spread, pair,
                out var primaryRules);
            var hasFallback = TryGetExplicitRules(storeBlob.FallbackRateSettings, storeBlob.Spread, pair,
                out var fallbackRules);
            if (!hasPrimary && !hasFallback)
                return RgbRateResult.Failed(RgbRateFailure.NoRule, preferredSource);

            var rateRules = hasPrimary
                ? new RateRulesCollection(primaryRules!, hasFallback ? fallbackRules : null)
                : new RateRulesCollection(fallbackRules!, null);

            // TWO mechanisms, because neither alone suffices.
            // The token bounds the PROVIDER's work: the pre-change code passed a 5s
            // CancellationTokenSource into FetchRate for exactly this, and dropping it would leave
            // abandoned exchange calls running after we stop waiting — one per invoice attempt.
            // WaitAsync bounds OUR wait, because WrapperRateProvider (RateProviderFactory.cs:30)
            // catches every exception including OperationCanceledException, so a cancelled token
            // cannot by itself make the call return.
            using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var result = await _rateFetcher
                    .FetchRate(pair, rateRules, new StoreIdRateContext(store.Id), providerCts.Token)
                    .WaitAsync(_budget, ct);

                if (result.BidAsk is { Bid: > 0 })
                    return RgbRateResult.Ok(result.BidAsk.Bid, result.Rule ?? "exchange");

                return RgbRateResult.Failed(RgbRateFailure.NoRate, preferredSource);
            }
            finally
            {
                // Whether we returned or gave up, nothing may keep querying the exchange for us.
                providerCts.Cancel();
            }
        }
        catch (TimeoutException)
        {
            return RgbRateResult.Failed(RgbRateFailure.Timeout, preferredSource);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return RgbRateResult.Failed(RgbRateFailure.Timeout, preferredSource);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rate lookup failed for {Code}/{Currency}", pricingCode, invoiceCurrency);
            return RgbRateResult.Failed(RgbRateFailure.Error, preferredSource);
        }
    }

    static bool TryGetExplicitRules(
        StoreBlob.RateSettings? settings,
        decimal spread,
        CurrencyPair requestedPair,
        out RateRules? rules)
    {
        rules = null;
        if (settings is not { RateScripting: true }
            || string.IsNullOrWhiteSpace(settings.RateScript)
            || !RateRules.TryParse(settings.RateScript, out var parsed)
            || !DeclaresExactPair(settings.RateScript, requestedPair))
            return false;

        parsed.Spread = spread;
        rules = parsed;
        return true;
    }

    static bool DeclaresExactPair(string script, CurrencyPair requestedPair)
    {
        var inverse = requestedPair.Inverse();
        return CSharpSyntaxTree.ParseText(script).GetRoot()
            .DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
            .Select(assignment => assignment.Left as IdentifierNameSyntax)
            .Where(identifier => identifier is not null)
            .Select(identifier => CurrencyPair.TryParse(identifier!.Identifier.ValueText, out var pair)
                ? pair
                : null)
            .Any(pair => pair == requestedPair || pair == inverse);
    }
}
