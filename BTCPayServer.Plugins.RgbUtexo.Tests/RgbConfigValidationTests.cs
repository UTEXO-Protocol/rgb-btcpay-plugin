using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbConfigValidationTests
{
    // WHY full display names and never ContainingType.Name: a `static class RgbConfigBounds` declared in
    // the enclosing BTCPayServer.Plugins.RgbUtexo.PaymentHandler namespace beats the using-imported
    // Services one by C# name resolution, so the validator would enforce the shadow's limits while a
    // short-name comparison stayed green.
    const string BoundsType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbConfigBounds";
    const string HandlerType = "BTCPayServer.Plugins.RgbUtexo.PaymentHandler.RGBPaymentMethodHandler";

    static RGBPaymentMethodHandler TestHandler() =>
        new(wallets: null!, rates: null!, pricingCodeGuard: null!, notices: null!,
            NullLogger<RGBPaymentMethodHandler>.Instance);

    // WHY the JToken overload: a config body that is a JSON *string* rather than an object is the only
    // case where JsonException.Path is empty and the error must be keyed "config". JObject.Parse cannot
    // express that, so the raw-token entry point is load-bearing.
    static Task<ModelStateDictionary> ValidateAsync(string json) => ValidateTokenAsync(JToken.Parse(json));

    static async Task<ModelStateDictionary> ValidateTokenAsync(JToken config)
    {
        var modelState = new ModelStateDictionary();
        await ValidateWithAsync(TestHandler(), config, modelState);
        return modelState;
    }

    // WHY await rather than ContinueWith: ContinueWith observes neither faults nor cancellation, so a
    // validator that threw would leave the caller asserting over an empty ModelState and the guard green.
    static async Task<ModelStateDictionary> ValidateWithAsync(
        RGBPaymentMethodHandler handler, string json)
    {
        var modelState = new ModelStateDictionary();
        await ValidateWithAsync(handler, JToken.Parse(json), modelState);
        return modelState;
    }

    static Task ValidateWithAsync(
        RGBPaymentMethodHandler handler, JToken config, ModelStateDictionary modelState) =>
        ((IPaymentMethodHandler)handler).ValidatePaymentMethodConfig(
            new PaymentMethodConfigValidationContext(
                authorizationService: null!, modelState, config, user: null!, previousConfig: null));

    const string W = "\"walletId\":\"w\"";

    [Fact]
    public async Task AcceptsConfigAtLowerBounds() =>
        Assert.True((await ValidateAsync(
            $"{{{W},\"utxoCount\":{RgbConfigBounds.UtxoCountMin},\"utxoSize\":{RgbConfigBounds.UtxoSizeMin},\"minConfirmations\":{RgbConfigBounds.MinConfirmationsMin}}}")).IsValid);

    [Fact]
    public async Task AcceptsConfigAtUpperBounds() =>
        Assert.True((await ValidateAsync(
            $"{{{W},\"utxoCount\":{RgbConfigBounds.UtxoCountMax},\"utxoSize\":{RgbConfigBounds.UtxoSizeMax},\"minConfirmations\":{RgbConfigBounds.MinConfirmationsMax}}}")).IsValid);

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(21)]
    public async Task RejectsUtxoCountOutOfRange(int v) =>
        Assert.True((await ValidateAsync($"{{{W},\"utxoCount\":{v}}}")).ContainsKey("utxoCount"));

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(545)] [InlineData(100001)]
    public async Task RejectsUtxoSizeOutOfRange(int v) =>
        Assert.True((await ValidateAsync($"{{{W},\"utxoSize\":{v}}}")).ContainsKey("utxoSize"));

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(101)]
    public async Task RejectsMinConfirmationsOutOfRange(int v) =>
        Assert.True((await ValidateAsync($"{{{W},\"minConfirmations\":{v}}}")).ContainsKey("minConfirmations"));

    // WHY separate from the theories above: this is the case that fails if the implementation
    // reverts to parsing with the blob serializer, where an explicit 0 is skipped and the property
    // initialiser default survives.
    [Fact]
    public async Task RejectsExplicitZeroNotSilentlyDefaulted() =>
        Assert.True((await ValidateAsync($"{{{W},\"utxoSize\":0}}")).ContainsKey("utxoSize"));

    // WHY the single most important behavioural test here: a raw-JToken implementation passes every
    // other test in this class and fails only this one.
    [Theory]
    [InlineData("\"UtxoSize\":5000000")]
    [InlineData("\"UTXOSIZE\":7000000")]
    [InlineData("\"utxoSize\":1000,\"UtxoSize\":9000000")]
    public async Task RejectsCaseVariantKeys(string fragment) =>
        Assert.True((await ValidateAsync($"{{{W},{fragment}}}")).ContainsKey("utxoSize"));

    [Fact]
    public async Task ReportsEveryViolationNotJustTheFirst()
    {
        var ms = await ValidateAsync($"{{{W},\"utxoCount\":99,\"utxoSize\":99,\"minConfirmations\":999}}");
        Assert.Equal(3, ms.ErrorCount);
        Assert.All(new[] { "utxoCount", "utxoSize", "minConfirmations" }, k => Assert.True(ms.ContainsKey(k)));
    }

    [Fact]
    public async Task ErrorMessageNamesFieldAndRange()
    {
        var ms = await ValidateAsync($"{{{W},\"utxoSize\":100001}}");
        var msg = ms["utxoSize"]!.Errors[0].ErrorMessage;
        Assert.Contains("utxoSize", msg);
        Assert.Contains(RgbConfigBounds.UtxoSizeMin.ToString(), msg);
        Assert.Contains(RgbConfigBounds.UtxoSizeMax.ToString(), msg);
    }

    // WHY assert the exact message and the absence of Newtonsoft's own wording: keying the error is
    // only half of "a model error rather than an escaped exception". Appending the caught exception's
    // text would keep a key-only assertion green while handing the API caller parser internals — which
    // for a reader exception include the offending literal echoed back verbatim.
    [Theory]
    [InlineData("\"utxoSize\":\"abc\"", "utxoSize")]
    [InlineData("\"utxoSize\":9999999999", "utxoSize")]
    [InlineData("\"utxoSize\":null", "utxoSize")]
    [InlineData("\"defaultAssetId\":{}", "defaultAssetId")]
    public async Task RejectsUnreadableValuesWithoutEscaping(string fragment, string key)
    {
        var ms = await ValidateAsync($"{{{W},{fragment}}}");

        Assert.True(ms.ContainsKey(key), $"expected a model error keyed '{key}', got: "
                                         + string.Join(", ", ms.Keys));

        var msg = ms[key]!.Errors[0].ErrorMessage;
        Assert.Equal($"{key} could not be read as a valid value", msg);
        Assert.All(
            new[] { "Unexpected token", "Error reading", "Could not convert", "Newtonsoft", "Path '" },
            marker => Assert.DoesNotContain(marker, msg));
    }

    // The empty-Path branch: a config body that is a JSON string, not an object.
    [Fact]
    public async Task RejectsNonObjectConfigBodyKeyedOnConfig()
    {
        var ms = await ValidateTokenAsync(JToken.Parse("\"not-an-object\""));
        Assert.True(ms.ContainsKey("config"));
        Assert.Contains("could not be read", ms["config"]!.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task NonIntegerMemberGetsTheGenericMessageNotAnIntegerOne()
    {
        var ms = await ValidateAsync($"{{{W},\"defaultAssetId\":{{}}}}");
        var msg = ms["defaultAssetId"]!.Errors[0].ErrorMessage;
        Assert.Contains("could not be read as a valid value", msg);
        Assert.DoesNotContain("integer", msg);
    }

    [Fact]
    public async Task AcceptsFloatThatRoundsIntoRange() =>
        Assert.True((await ValidateAsync($"{{{W},\"utxoSize\":1000.7}}")).IsValid);

    [Fact]
    public async Task AcceptsConfigWithFieldsAbsent() =>
        Assert.True((await ValidateAsync($"{{{W}}}")).IsValid);

    [Fact]
    public async Task LegacyConfigWithMaxAllocationsDeserializesCleanly() =>
        Assert.True((await ValidateAsync($"{{{W},\"maxAllocationsPerUtxo\":99}}")).IsValid);

    // WHY: walletId is deliberately unvalidated, so the hazard is an implementation that returns
    // early when it is missing and thereby skips the bounds entirely.
    [Theory]
    [InlineData("\"walletId\":\"\"")]
    [InlineData("\"walletId\":null")]
    [InlineData("\"walletId\":\"some-other-stores-wallet\"")]
    [InlineData("")]
    public async Task WalletIdVariationsDoNotSkipNumericBounds(string walletFragment)
    {
        var prefix = walletFragment.Length == 0 ? "" : walletFragment + ",";
        Assert.True((await ValidateAsync($"{{{prefix}\"utxoSize\":100001}}")).ContainsKey("utxoSize"));
    }

    // WHY assert on the SAME handler that ran the validator: Serializer is a per-instance property of
    // RGBPaymentMethodHandler, so building one handler and validating through another leaves the guard
    // green no matter what the validator does to its own serializer.
    [Fact]
    public async Task SharedSerializerIsNotMutated()
    {
        var handler = TestHandler();
        var before = (handler.Serializer.DefaultValueHandling, handler.Serializer.NullValueHandling);

        await ValidateWithAsync(handler, $"{{{W},\"utxoSize\":100001}}");

        Assert.Equal(before,
            (handler.Serializer.DefaultValueHandling, handler.Serializer.NullValueHandling));
        Assert.Equal(DefaultValueHandling.Ignore, handler.Serializer.DefaultValueHandling);
        Assert.Equal(NullValueHandling.Ignore, handler.Serializer.NullValueHandling);
    }

    // WHY dispatch through the interface: a source pin can only prove a method of that name is
    // declared. A signature drift leaves the interface default in force with the pin still green.
    [Fact]
    public async Task ValidateHookIsReachedThroughTheInterface()
    {
        IPaymentMethodHandler handler = TestHandler();
        var ms = new ModelStateDictionary();
        await handler.ValidatePaymentMethodConfig(new PaymentMethodConfigValidationContext(
            null!, ms, JObject.Parse("{\"walletId\":\"w\",\"utxoSize\":100001}"), null!, null));

        Assert.False(ms.IsValid);
    }

    [Fact]
    public void ValidateHookSignatureMatchesTheInterface()
    {
        var m = typeof(RGBPaymentMethodHandler).GetMethod(nameof(RGBPaymentMethodHandler.ValidatePaymentMethodConfig));
        Assert.NotNull(m);
        Assert.Equal(typeof(Task), m!.ReturnType);
        Assert.Equal(typeof(PaymentMethodConfigValidationContext), Assert.Single(m.GetParameters()).ParameterType);
    }

    // WHY pin the ARGUMENT: reverting to ParsePaymentMethodConfig inside the validator restores the
    // explicit-zero defect while every other source pin stays green.
    [Fact]
    public void ValidatorUsesStrictSerializerNotBlobSerializer()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("PaymentHandler/RGBPaymentMethodHandler.cs");
        var method = RoslynPins.Method(tree, "RGBPaymentMethodHandler", "ValidatePaymentMethodConfig");

        var call = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText == "ToObject");

        // The containing type is asserted by FULL display name for the same reason as the bounds below:
        // a short name cannot tell the audited handler apart from a same-named type reached through a
        // base class or another namespace.
        var argument = Assert.IsType<IdentifierNameSyntax>(call.ArgumentList.Arguments[0].Expression);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, argument);
        Assert.True(symbol.Name == "StrictSerializer"
                    && symbol.ContainingType?.ToDisplayString() == HandlerType,
            $"the validator must parse with {HandlerType}.StrictSerializer, found "
            + $"{symbol.ContainingType?.ToDisplayString()}.{symbol.Name}");
    }

    // WHY arguments are resolved by NAME first and only then by position: naming an argument, or
    // reordering the named ones, is a legal edit that changes nothing about which constant reaches which
    // parameter — the property this pin exists to protect. Positional indexing alone reddens on it. The
    // positional fallback is exact rather than approximate: C# requires an UNNAMED argument to sit at its
    // own parameter's position, so an argument with no NameColon is always at the index it binds to.
    static ExpressionSyntax BoundArgument(InvocationExpressionSyntax call, int index, string parameter)
    {
        var named = call.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter);
        return (named ?? call.ArgumentList.Arguments[index]).Expression;
    }

    // The validator itself is a semantic pin site: its Bound(...) calls are bound copies, and
    // ErrorMessageNamesFieldAndRange only asserts the MESSAGE contains both values, which hardcoded
    // literals satisfy.
    [Theory]
    [InlineData("utxoCount", "UtxoCountMin", "UtxoCountMax")]
    [InlineData("utxoSize", "UtxoSizeMin", "UtxoSizeMax")]
    [InlineData("minConfirmations", "MinConfirmationsMin", "MinConfirmationsMax")]
    public void ValidatorBoundArgumentsBindToRgbConfigBounds(string field, string minConst, string maxConst)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("PaymentHandler/RGBPaymentMethodHandler.cs");
        var method = RoslynPins.Method(tree, "RGBPaymentMethodHandler", "ValidatePaymentMethodConfig");

        // The call is identified by its key argument, which is subject to the same reordering as the
        // bounds — so it is resolved the same way rather than read off index 0.
        var call = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as IdentifierNameSyntax)?.Identifier.ValueText == "Bound"
                        && BoundArgument(i, 0, "key").ToString().Contains(field));

        foreach (var (index, parameter, expected) in
                 new[] { (2, "min", minConst), (3, "max", maxConst) })
        {
            var access = Assert.IsType<MemberAccessExpressionSyntax>(
                BoundArgument(call, index, parameter));
            var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
            Assert.True(symbol.Name == expected
                        && symbol.ContainingType?.ToDisplayString() == BoundsType,
                $"Bound(\"{field}\", ...) must pass {BoundsType}.{expected} as '{parameter}', found "
                + $"{symbol.ContainingType?.ToDisplayString()}.{symbol.Name}");
        }
    }

    // WHY scoped to the three int fields: the reference-typed and bool fields diverge between the
    // two parses WITHOUT being rejected, because they are not validated. Unscoped, this cannot pass.
    // WHY the disjunction is asserted unconditionally, for every field and every case: a body guarded
    // on divergence asserts nothing at all for the inputs where the two parses agree, so the claim
    // "they can only diverge in the reject direction" would be recorded without ever being tested.
    [Theory]
    [InlineData("\"utxoSize\":5000000")]
    [InlineData("\"UtxoSize\":5000000")]
    [InlineData("\"utxoSize\":0")]
    [InlineData("\"utxoSize\":1000.7")]
    [InlineData("\"utxoSize\":-5")]
    public async Task StrictParseDiffersFromStorageOnlyInRejectDirection(string fragment)
    {
        var json = $"{{\"walletId\":\"w\",{fragment}}}";
        var stored = JObject.Parse(json).ToObject<RGBPaymentMethodConfig>(
            BlobSerializer.CreateSerializer().Serializer)!;
        var strict = JObject.Parse(json).ToObject<RGBPaymentMethodConfig>(RGBPaymentMethodHandler.StrictSerializer)!;
        var rejected = !(await ValidateAsync(json)).IsValid;

        foreach (var (field, strictValue, storedValue) in new[]
                 {
                     ("utxoCount", strict.UtxoCount, stored.UtxoCount),
                     ("utxoSize", strict.UtxoSize, stored.UtxoSize),
                     ("minConfirmations", strict.MinConfirmations, stored.MinConfirmations)
                 })
            Assert.True(strictValue == storedValue || rejected,
                $"{json} was accepted, but the validator read {field}={strictValue} where storage "
                + $"reads {storedValue}");
    }
}
