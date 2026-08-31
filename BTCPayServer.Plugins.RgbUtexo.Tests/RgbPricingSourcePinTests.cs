using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins the contract-bound pricing wiring (audit finding E).
///
/// Scope, stated so nobody extends these as if they were something else: they guard ACCIDENTAL
/// REGRESSION — a refactor that changes FetchAsync(pricingCode, …) back to FetchAsync(ticker, …).
/// They are NOT a defence against adversarial evasion. Six review rounds produced five successive
/// generations of bypass, each closed and the next appearing one hop further up the dataflow, and one
/// is beyond any pin over ConfigurePrompt entirely: the same mutation placed inside
/// RGBWalletService.GetAssetAsync. A syntactic check over one method cannot win that race.
///
/// The behavioural guarantee rests on RgbPricingHandlerTests 24-29, RgbAssetLookupTests (37b/37c) and
/// the live end-to-end run. Add a pin only when it is cheap and closes an accidental path; record a
/// deliberately obfuscated mutation as out of scope instead.
/// </summary>
public class RgbPricingSourcePinTests
{
    const string HandlerFile = "PaymentHandler/RGBPaymentMethodHandler.cs";
    const string ListenerFile = "Services/RGBInvoiceListener.cs";
    const string WalletServiceFile = "Services/RGBWalletService.cs";

    static MethodDeclarationSyntax ConfigurePrompt() =>
        RoslynPins.Method(PluginCompilation.Shared.Tree(HandlerFile), "RGBPaymentMethodHandler", "ConfigurePrompt");

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static List<InvocationExpressionSyntax> InvocationsNamed(SyntaxNode scope, string name) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => NameOf(i) == name).ToList();

    static InvocationExpressionSyntax Single(SyntaxNode scope, string name)
    {
        var found = InvocationsNamed(scope, name);
        Assert.True(found.Count == 1, $"expected exactly one '{name}' invocation, found {found.Count}");
        return found[0];
    }

    static string Text(SyntaxNode node) => node.ToString().Trim();

    // Named arguments are property-preserving — `FetchAsync(pricingCode: pricingCode, …)` compiles and
    // means the same thing — so a pin must match the ARGUMENT rather than its spelling, and must find
    // it by name when one is given rather than by position.
    static ExpressionSyntax Argument(InvocationExpressionSyntax call, string parameterName, int position)
    {
        var named = call.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameterName);
        return (named ?? call.ArgumentList.Arguments[position]).Expression;
    }

    // Discovered from the declarator rather than hardcoded, so RENAMING the local stays green. A pin
    // that fires on a property-preserving refactor is itself a defect: it trains the next developer to
    // edit the pin rather than to trust it.
    static VariableDeclaratorSyntax PricingCodeLocal(SyntaxNode body)
    {
        var declarators = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Initializer?.Value is InvocationExpressionSyntax i
                        && Text(i.Expression) == "RgbPricingCode.For")
            .ToList();
        Assert.True(declarators.Count == 1,
            $"expected exactly one local bound to RgbPricingCode.For, found {declarators.Count}");
        return declarators[0];
    }

    // P-E2 — one derivation, from the contract id itself.
    [Fact]
    public void PE2_TheOnlyPricingCodeDerivation_TakesAssetAssetId()
    {
        var call = Single(RoslynPins.BodyOf(ConfigurePrompt()), "For");

        Assert.Equal("RgbPricingCode.For", Text(call.Expression));
        Assert.Single(call.ArgumentList.Arguments);
        Assert.Equal("asset.AssetId", Text(Argument(call, "assetId", 0)));
    }

    // P-E2b — without this, P-E2 pins the argument's SYNTAX and not its VALUE:
    //   var realId = asset.AssetId; asset.AssetId = asset.Ticker;
    //   var pricingCode = RgbPricingCode.For(asset.AssetId); asset.AssetId = realId;
    // compiles, satisfies every other pin, and derives the pricing identity from ticker metadata.
    // RGBAsset.AssetId has a public setter, so this is reachable.
    [Fact]
    public void PE2b_NoMemberOfTheAssetLocal_IsAssigned()
    {
        var mutations = RoslynPins.BodyOf(ConfigurePrompt())
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax m
                        && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "asset" })
            .Select(Text)
            .ToList();

        Assert.True(mutations.Count == 0,
            $"ConfigurePrompt mutates the asset row: {string.Join("; ", mutations)}");
    }

    // P-E3 — the derived code is bound once and never re-pointed.
    [Fact]
    public void PE3_ThePricingCodeLocal_IsSingleAssignment()
    {
        var method = ConfigurePrompt();
        var body = RoslynPins.BodyOf(method);

        var declarator = PricingCodeLocal(body);
        var name = declarator.Identifier.ValueText;
        Assert.Equal("RgbPricingCode.For(asset.AssetId)", Text(declarator.Initializer!.Value));

        // Covers direct assignment and ref/out arguments.
        RoslynPins.AssertNeverReassigned(method, name);

        // AssertNeverReassigned covers neither of the following, and both are expressible here. C# 13
        // lifted the CS8177 ban on ref locals in async methods for a ref that is not alive across an
        // await, and this project targets net10.0 with no explicit LangVersion, so
        //   ref var alias = ref pricingCode; alias = asset.Ticker;
        // placed before the fetch compiles.
        var refAliases = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(d => d.Initializer?.Value is RefExpressionSyntax r
                        && r.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name)
            .Select(d => d.Identifier.ValueText)
            .ToList();
        Assert.True(refAliases.Count == 0,
            $"'{name}' is aliased by a ref local: {string.Join(", ", refAliases)}");

        var deconstructions = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is TupleExpressionSyntax t
                        && t.Arguments.Any(arg => arg.Expression is IdentifierNameSyntax id
                                                  && id.Identifier.ValueText == name))
            .Select(Text)
            .ToList();
        Assert.True(deconstructions.Count == 0,
            $"'{name}' is a deconstruction target: {string.Join("; ", deconstructions)}");
    }

    // P-E4 — the rate lookup and the unit arithmetic both key off that one local.
    [Fact]
    public void PE4_TheRateFetchAndThePlan_BothKeyOffThePricingCode()
    {
        var body = RoslynPins.BodyOf(ConfigurePrompt());
        var name = PricingCodeLocal(body).Identifier.ValueText;

        var fetch = Single(body, "FetchAsync");
        Assert.Equal(name, Text(Argument(fetch, "pricingCode", 0)));

        var build = Single(body, "Build");
        Assert.Equal("RgbPricingPlan.Build", Text(build.Expression));
        Assert.Equal(name, Text(Argument(build, "pricingCode", 0)));
    }

    // P-E1 — every currency identity the prompt carries comes from the plan, never from the ticker.
    [Fact]
    public void PE1_EveryCurrencyIdentity_ComesFromThePlan()
    {
        var body = RoslynPins.BodyOf(ConfigurePrompt());

        var promptCurrency = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => Text(a.Left) == "ctx.Prompt.Currency");
        Assert.Equal("plan.PromptCurrency", Text(promptCurrency.Right));

        var ratesWrite = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => Text(a.Left) == "ctx.InvoiceEntity.Rates");
        var copy = Assert.IsType<InvocationExpressionSyntax>(ratesWrite.Right);
        Assert.True(Text(copy.Expression) == "RatesCopyThatNoSiblingPromptCanBeEnumerating",
            $"the pricing rate must reach Rates by REPLACING the dictionary, so that no sibling prompt "
            + $"enumerating it concurrently can throw; it reaches it via '{Text(copy.Expression)}'");
        Assert.True(Text(Argument(copy, "ratesKey", 1)) == "plan.RatesKey",
            $"the Rates key must be plan.RatesKey, not '{Text(Argument(copy, "ratesKey", 1))}'");
        Assert.True(Text(Argument(copy, "rate", 2)) == "rate.Rate",
            $"the recorded rate must be the fetched rate the units were priced from, not "
            + $"'{Text(Argument(copy, "rate", 2))}'");

        var promptDetails = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "PricingCode" });
        Assert.Equal("plan.PricingCode", Text(promptDetails.Right));
    }

    [Fact]
    public void PF3_ConfigurePrompt_NeverMutatesTheRatesDictionaryEveryConcurrentPromptShares()
    {
        var body = RoslynPins.BodyOf(ConfigurePrompt());

        var indexerWrites = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is ElementAccessExpressionSyntax e
                        && Text(e.Expression).EndsWith("Rates", StringComparison.Ordinal))
            .Select(Text)
            .ToList();
        Assert.True(indexerWrites.Count == 0,
            "ConfigurePrompt indexes into the Rates dictionary that every concurrent payment prompt "
            + "shares; an in-place insert bumps its version and makes a sibling's in-flight enumeration "
            + $"throw, dropping that prompt from the issued invoice: {string.Join("; ", indexerWrites)}");

        var mutators = new[] { "Add", "Remove", "Clear", "TryAdd", "AddRate" };
        var inPlaceCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => mutators.Contains(NameOf(i)))
            .Where(i => i.Expression is MemberAccessExpressionSyntax m
                        && (Text(m.Expression).EndsWith("Rates", StringComparison.Ordinal)
                            || Text(m.Expression).EndsWith("InvoiceEntity", StringComparison.Ordinal)))
            .Select(Text)
            .ToList();
        Assert.True(inPlaceCalls.Count == 0,
            "ConfigurePrompt mutates the shared rate table through a method call rather than replacing "
            + $"it: {string.Join("; ", inPlaceCalls)}");

        var replacements = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => Text(a.Left) == "ctx.InvoiceEntity.Rates")
            .ToList();
        Assert.True(replacements.Count == 1,
            $"the pricing rate must be recorded by exactly one whole-dictionary replacement, found "
            + $"{replacements.Count}");
    }

    // P-E6 — closes the whole "rebind the local the pin reads" family in one clause. It does NOT close
    // mutation of a local's MEMBERS through an alias, nor any mutation inside GetAssetAsync.
    [Fact]
    public void PE6_TheLocalsTheOtherPinsRead_AreSingleAssignment()
    {
        RoslynPins.AssertNeverReassigned(ConfigurePrompt(), "asset", "plan", "invoiceCurrency");
    }

    // P-E5 — the listener must validate before even looking for an existing payment, then use the
    // validated current code for a newly inserted payment. Otherwise a legacy ticker payment could
    // still be updated to Settled after deployment.
    [Fact]
    public void PE5_TheRecordedPaymentCurrency_ComesFromThePreLookupIdentityGate()
    {
        var method = RoslynPins.Method(
            PluginCompilation.Shared.Tree(ListenerFile), "RGBInvoiceListener", "RecordOrUpdatePayment");
        var body = RoslynPins.BodyOf(method);

        var identity = Single(body, "ClassifyPromptPricingIdentity");
        Assert.Equal("rgbInv", Text(Argument(identity, "rgbInvoice", 0)));
        Assert.Equal("details", Text(Argument(identity, "details", 1)));
        var currencyArg = identity.ArgumentList.Arguments[2];
        Assert.Equal("out var paymentCurrency", Text(currencyArg));

        var existingPayment = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(d => d.Identifier.ValueText == "existingPayment");
        Assert.True(identity.SpanStart < existingPayment.SpanStart,
            "prompt identity must be rejected before an existing payment can be updated");

        var currencyAssignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.ValueText: "Currency" }
                        && a.Parent is InitializerExpressionSyntax)
            .ToList();

        var assignment = Assert.Single(currencyAssignments);
        Assert.Equal("paymentCurrency", Text(assignment.Right));
    }

    // P-E7 — 37b/37c pin the predicate, but nothing pins that production still USES it. Re-inlining
    // `a => a.AssetId == assetId` would drop the WalletId filter — the false-ACCEPT 37b exists to
    // catch — while both those tests stayed green.
    [Fact]
    public void PE7_GetAssetAsync_UsesTheExtractedPredicate()
    {
        var method = RoslynPins.Method(
            PluginCompilation.Shared.Tree(WalletServiceFile), "RGBWalletService", "GetAssetAsync");
        var body = RoslynPins.BodyOf(method);

        // Argument names discovered from GetAssetAsync's own parameter list rather than hardcoded, for
        // the same reason PricingCodeLocal discovers its identifier: renaming a parameter is
        // property-preserving and must not red a pin. What is pinned is that the predicate receives the
        // wallet id and the asset id, in that ORDER — swapping them would scope the lookup wrongly.
        var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToList();
        var call = Single(body, "AssetPredicate");
        Assert.Equal(parameters[0], Text(Argument(call, "walletId", 0)));
        Assert.Equal(parameters[1], Text(Argument(call, "assetId", 1)));

        var lambdas = body.DescendantNodes().OfType<LambdaExpressionSyntax>().Select(Text).ToList();
        Assert.True(lambdas.Count == 0,
            $"GetAssetAsync builds its own predicate instead of using AssetPredicate: {string.Join("; ", lambdas)}");
    }
}
