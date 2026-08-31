using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// No behavioural test observes the policy this code hands its signer: the signer tests build their
// own policies and the regtest fixture builds a mirror, so these pins are the only constraint on the
// shipped policy, and each binds the value at the call site that consumes it.
public class RgbVanillaInputGuardSourcePinTests
{
    const string SignerFile = "Services/MemoryWalletSigner.cs";
    const string ServiceFile = "Services/RGBWalletService.cs";
    const string SignerType = "MemoryWalletSigner";
    const string ServiceType = "RGBWalletService";
    const string Guard = "EnsureInputsOnRgbVanillaAccount";
    const string Flag = "RequireRgbVanillaKeychainInputs";
    const string CreateUtxos = "CreateColorableUtxosInternalAsync";
    const string SendBtc = "SendBtcInternalAsync";
    const string SendAsset = "SendAssetInternalAsync";
    const string ServiceSink = "SignPsbtWithSignerAsync";
    const string SignerSink = "SignPsbtAsync";
    const string LocalSink = "SignPsbtLocallyAsync";
    const string PolicyFullType = "BTCPayServer.Plugins.RgbUtexo.Services.SigningPolicy";

    static string WhyBoundAtTheSink(string method, string sink, int position) =>
        $"WHAT THIS PINS: the SigningPolicy whose members are asserted is the object {method} actually "
        + $"hands to {sink} at argument {position}, reached FROM that argument — not the first "
        + "`new SigningPolicy` the method happens to contain. WHY, and this is not hypothetical: a pin "
        + $"that reads the initializer alone is vacuous. MEASURED on this tree, inserting `policy.{Flag} "
        + "= false;` between the initializer and the signature left every member assertion intact, "
        + "preserved the authorization-check/signature adjacency other pins require, compiled, and kept "
        + "the whole managed suite green — while making a wallet-owned colored-keychain input signable "
        + "(RgbVanillaKeychainInputGuardTests.ColoredInput_WithGuardOff_Signs is that behaviour), so a "
        + "create-UTXOs PSBT carrying an input that holds an RGB allocation would be signed with no "
        + "asset-intent accounting and the allocation burned. Nothing behavioural can catch it: "
        + "RgbVanillaKeychainInputGuardTests.Policy builds its own policies and "
        + "RgbDryRunCreateUtxosRegtestTests.SignAsProductionCreateUtxosDoesAsync builds a parallel "
        + "mirror, so not even the live integration run observes this handoff.\n"
        + "THE PROPERTY, as a whitelist and not a list of ways to break it: the method constructs "
        + "exactly one SigningPolicy; and that object reaches the signer either AS the argument, or "
        + "through a local that is declared exactly once from that object-creation expression and is "
        + "MENTIONED EXACTLY ONCE in the whole method — as that argument. The single-mention rule is "
        + $"what makes every member value final. It refuses `policy.{Flag} = false`, a write to any "
        + "other member, `policy = somethingElse`, `policy.AllowedScripts.Add(...)`, `F(ref policy)` and "
        + "handing the object to a mutator, without enumerating any of them, and it refuses a second "
        + "signature in this method fed a policy of its own.\n"
        + "WHAT IT DELIBERATELY DOES NOT REFUSE, because a pin that reddens on a correct refactor gets "
        + "deleted and takes the protection with it: renaming the local; reordering the member "
        + "initializers; writing the policy inline at the call; passing it by name rather than by "
        + "position; and `new()` in place of `new SigningPolicy` — the construction and the parameter "
        + "are matched by BOUND TYPE, not by spelling. WHAT IT REDDENS ON THAT IS NOT AN ATTACK: a new "
        + "READ-ONLY mention such as a log line, and lifting the initializer into a helper method — the "
        + "member clauses read it in place. That is deliberate; widen the clause explicitly rather than "
        + "deleting it.\n"
        + "NAMED DEBT, deliberately open: the mirror policy in RgbDryRunCreateUtxosRegtestTests is not "
        + "pinned to this one, so the two can diverge. Graded and left open — divergence weakens an "
        + "integration row but cannot make production accept anything, and closing it needs either a "
        + "production factory (this initializer must stay inline for the member clauses to read it) or "
        + "a third proxy pin over a test helper, which is the very defect class this pin closes.";

    static INamedTypeSymbol PolicyType(PluginCompilation plugin)
    {
        var type = plugin.Compilation.GetTypeByMetadataName(PolicyFullType);
        Assert.True(type != null, $"{PolicyFullType} does not resolve in the plugin compilation");
        return type!;
    }

    static List<BaseObjectCreationExpressionSyntax> PolicyConstructionsIn(
        PluginCompilation plugin, SyntaxTree tree, SyntaxNode scope)
    {
        var model = plugin.Model(tree);
        var policyType = PolicyType(plugin);
        return scope.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Where(o => SymbolEqualityComparer.Default.Equals(
                (model.GetSymbolInfo(o).Symbol as IMethodSymbol)?.ContainingType, policyType))
            .ToList();
    }

    static string EnclosingMethodName(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? $"<no enclosing method in {node.SyntaxTree.FilePath}>";

    static BaseObjectCreationExpressionSyntax PolicyReaching(
        PluginCompilation plugin, string methodName, string sink, int position)
    {
        var tree = plugin.Tree(ServiceFile);
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, ServiceType, methodName);
        var body = RoslynPins.BodyOf(method);
        var why = WhyBoundAtTheSink(methodName, sink, position);

        var policyType = PolicyType(plugin);
        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == sink,
                MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText == sink,
                IdentifierNameSyntax id => id.Identifier.ValueText == sink,
                _ => false
            })
            .ToList();
        Assert.True(calls.Count == 1,
            $"{methodName} invokes {sink} {calls.Count} time(s); exactly one is mandated. {why}");

        var constructions = PolicyConstructionsIn(plugin, tree, body);
        Assert.True(constructions.Count == 1,
            $"{methodName} constructs {constructions.Count} SigningPolicy object(s); exactly one is "
            + $"mandated, or the one {sink} receives is not the one these clauses read. {why}");
        var creation = constructions[0];

        var callee = model.GetSymbolInfo(calls[0]).Symbol as IMethodSymbol;
        Assert.True(callee != null && callee.Parameters.Length > position
                    && SymbolEqualityComparer.Default.Equals(callee.Parameters[position].Type, policyType),
            $"{methodName}: parameter {position} of the {sink} it calls is not of type SigningPolicy "
            + $"(`{callee?.ToDisplayString() ?? "unbound"}`). {why}");
        var byName = calls[0].ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == callee!.Parameters[position].Name);
        Assert.True(byName != null || calls[0].ArgumentList.Arguments.Count > position,
            $"{methodName}: the {sink} call passes {calls[0].ArgumentList.Arguments.Count} argument(s) "
            + $"and none of them is '{callee!.Parameters[position].Name}'. {why}");
        var argument = (byName ?? calls[0].ArgumentList.Arguments[position]).Expression;
        if (ReferenceEquals(argument, creation)) return creation;

        var identifier = argument as IdentifierNameSyntax;
        Assert.True(identifier != null,
            $"{methodName}: argument {position} of {sink} is `{argument}` — neither the SigningPolicy "
            + $"object-creation expression itself nor a bare local. {why}");

        var local = model.GetSymbolInfo(identifier!).Symbol as ILocalSymbol;
        Assert.True(local != null,
            $"{methodName}: `{identifier}` does not bind to a local of this method. {why}");

        var mentions = body.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(n => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(n).Symbol, local))
            .ToList();
        Assert.True(mentions.Count == 1 && ReferenceEquals(mentions[0], identifier),
            $"{methodName}: '{local!.Name}' is mentioned {mentions.Count} time(s) — "
            + $"[{string.Join(" | ", mentions.Select(m => m.Parent?.ToString().Trim()))}] — and must be "
            + $"mentioned exactly once, as argument {position} of {sink}. {why}");

        var declarators = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => SymbolEqualityComparer.Default.Equals(model.GetDeclaredSymbol(v), local))
            .ToList();
        Assert.True(declarators.Count == 1,
            $"{methodName}: '{local.Name}' has {declarators.Count} declarator(s); exactly one is "
            + $"mandated. {why}");
        Assert.True(ReferenceEquals(declarators[0].Initializer?.Value, creation),
            $"{methodName}: '{local.Name}' is initialized from `{declarators[0].Initializer?.Value}`, "
            + $"not from the SigningPolicy object-creation expression {sink} must receive. {why}");
        return creation;
    }

    [Fact]
    public void PoliciesReachingASigner_CarryTheGuardFlagTheirPathRequires()
    {
        var plugin = PluginCompilation.Shared;
        var mandated = new (string Method, string Sink, int Position, bool Flag)[]
        {
            (CreateUtxos, ServiceSink, 4, true),
            (SendBtc, SignerSink, 2, true),
            (SendAsset, LocalSink, 3, false)
        };

        foreach (var (methodName, sink, position, flag) in mandated)
        {
            var creation = PolicyReaching(plugin, methodName, sink, position);
            var set = creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == Flag && a.Right.ToString() == "true") == true;
            var direction = flag
                ? $"the policy {methodName} hands to {sink} must set {Flag} = true: it signs a PSBT it "
                  + "did not build, and without the flag a wallet-owned colored-keychain input carrying "
                  + "an RGB allocation is signable with no asset-intent accounting, which burns it."
                : $"the policy {methodName} hands to {sink} must NOT set {Flag} = true: spending colored "
                  + "inputs is that path's purpose, and setting it makes every RGB asset send refuse "
                  + "its own inputs — a PERMANENT false-reject, which is fund loss.";
            Assert.True(set == flag,
                direction
                + $"\n{nameof(Flag_IsSetOnExactlyTheTwoIntendedSigningPolicies)} decides WHICH methods "
                + "may set the flag, by enclosing method; this clause is what makes each of its rows "
                + "non-vacuous, by proving the initializer it read is the object the signer gets. "
                + WhyBoundAtTheSink(methodName, sink, position));
        }
    }

    [Fact]
    public void CreateUtxosPolicy_BindsEverySecurityCriticalValue()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ServiceFile);
        var method = RoslynPins.Method(tree, ServiceType, CreateUtxos);
        var policy = PolicyReaching(plugin, CreateUtxos, ServiceSink, 4);

        var assignments = policy.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().ToList()
            ?? [];
        var byMember = assignments.ToDictionary(a => a.Left.ToString(), a => a.Right);

        var expected = new Dictionary<string, string>
        {
            ["MaxUnknownOutputSats"] = "0",
            ["MaxFeeSats"] = "CreateUtxosMaxFeeSatsAtOneInput(count)",
            ["MaxFeeSatsPerAdditionalInput"] = "CreateUtxosMaxFeeSatsPerAdditionalInput(count)",
            ["MaxOutputCount"] = "count+1",
            [Flag] = "true"
        };

        Assert.True(byMember.Count == expected.Count + 1,
            $"{CreateUtxos}'s SigningPolicy assigns {byMember.Count} member(s): "
            + $"{string.Join(", ", byMember.Keys)}. It must assign exactly "
            + $"{string.Join(", ", expected.Keys)} and AllowedScripts — no more, no fewer. Most dropped "
            + "members fall back to a more permissive default; the one exception is "
            + "MaxFeeSatsPerAdditionalInput, whose zero default trips the MORE restrictive "
            + "value-proportional/absolute-floor branch instead. An added member is an unpinned policy "
            + "decision either way.");

        foreach (var (member, value) in expected)
        {
            Assert.True(byMember.TryGetValue(member, out var actual),
                member == "MaxFeeSatsPerAdditionalInput"
                    ? $"{CreateUtxos}'s SigningPolicy no longer assigns {member}; its zero default trips "
                      + "the value-proportional/absolute-floor branch instead of the shape-bounded pair, "
                      + "which is MORE restrictive on dust-valued inputs and can permanently false-reject "
                      + "an honest sweep — the opposite of a permissive default"
                    : $"{CreateUtxos}'s SigningPolicy no longer assigns {member}; its default is more "
                      + "permissive than the value this path requires");
            var normalized = string.Concat(actual!.ToString().Where(c => !char.IsWhiteSpace(c)));
            Assert.True(normalized == value,
                $"{CreateUtxos}: {member} must be `{value}`, it is `{normalized}`. These six values are "
                + "the whole of the Create-UTXOs signing policy; any drift is a security regression, not "
                + "a refactor. MaxFeeSats and MaxFeeSatsPerAdditionalInput must stay a PAIR: rgb-lib's "
                + "create_utxos_begin folds EVERY non-reserved vanilla UTXO of the wallet into the "
                + "transaction (create_utxos_begin_impl collects all of internal_unspents() and "
                + "create_split_tx calls add_utxos(inputs).manually_selected_only()), so `num` sets the "
                + "recipient count and NOT the input count. A ceiling of the single form "
                + "EstimateTaprootFee(count, count + 1, 2.0f) * 3 therefore models a one-input "
                + "transaction and refuses the honest fee of a wallet holding as few as seven separate "
                + "vanilla deposits — a PERMANENT false-reject that empties the colorable pool and stops "
                + "RGB payments. Collapsing the pair back into one absolute number reintroduces exactly "
                + "that; raising it to a constant instead makes the guard unfalsifiable.");
        }

        var allowed = Assert.IsType<ObjectCreationExpressionSyntax>(byMember["AllowedScripts"]);
        Assert.True(allowed.Type.ToString() == "HashSet<Script>",
            $"{CreateUtxos}: AllowedScripts must be a HashSet<Script>, it is `{allowed.Type}`");
        var element = Assert.Single(allowed.Initializer?.Expressions ?? default);
        var access = Assert.IsType<MemberAccessExpressionSyntax>(element);
        Assert.True(access.Name.Identifier.ValueText == "ScriptPubKey",
            $"{CreateUtxos}: the single allowed script must be a ScriptPubKey, it is `{element}`");

        var addressLocal = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        var declarator = RoslynPins.BodyOf(method).DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == addressLocal);
        var initializer = declarator.Initializer?.Value.ToString() ?? string.Empty;
        Assert.True(initializer.Contains("BitcoinAddress.Create", StringComparison.Ordinal)
                    && initializer.Contains("GetAddressAsync", StringComparison.Ordinal),
            $"{CreateUtxos}: the single allowed script must derive from this wallet's own address, "
            + $"obtained through GetAddressAsync; '{addressLocal}' is initialised from `{initializer}`");
    }

    [Fact]
    public void SendAssetPolicy_BindsEverySecurityCriticalValue()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ServiceFile);
        var method = RoslynPins.Method(tree, ServiceType, SendAsset);
        var policy = PolicyReaching(plugin, SendAsset, LocalSink, 3);

        var assignments = policy.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>().ToList()
            ?? [];
        var byMember = assignments.ToDictionary(a => a.Left.ToString(), a => a.Right);

        var expected = new Dictionary<string, string>
        {
            ["MaxUnknownOutputSats"] = "0",
            ["MaxFeeSats"] = "SendAssetMaxFeeSatsAtOneInput(sendAssetRoundedFeeRate)",
            ["MaxFeeSatsPerAdditionalInput"] = "SendAssetMaxFeeSatsPerAdditionalInput(sendAssetRoundedFeeRate)",
            ["MaxOutputCount"] = "10",
            ["RequireUnfinalizedWitnessProgramInputs"] = "true"
        };

        Assert.True(byMember.Count == expected.Count + 1,
            $"{SendAsset}'s SigningPolicy assigns {byMember.Count} member(s): "
            + $"{string.Join(", ", byMember.Keys)}. It must assign exactly "
            + $"{string.Join(", ", expected.Keys)} and AllowedScripts — no more, no fewer. Most dropped "
            + "members fall back to a more permissive default; the one exception is "
            + "MaxFeeSatsPerAdditionalInput, whose zero default trips the MORE restrictive "
            + "value-proportional/absolute-floor branch instead. An added member is an unpinned policy "
            + "decision either way. In particular, a MaxFeeSats with no MaxFeeSatsPerAdditionalInput "
            + "beside it is a flat ceiling that does not scale with input count: rgb-lib's send_begin "
            + "can select as many vanilla and colored inputs as the invoice needs, so a flat ceiling "
            + "refuses the honest fee of any send that needs enough inputs — permanently, because the "
            + "ceiling is bounded in input count while the honest fee is not, so some input count is "
            + "eventually refused at every feeRate even though the exact count where refusal starts is "
            + "feeRate-dependent.");

        foreach (var (member, value) in expected)
        {
            Assert.True(byMember.TryGetValue(member, out var actual),
                member == "MaxFeeSatsPerAdditionalInput"
                    ? $"{SendAsset}'s SigningPolicy no longer assigns {member}; its zero default trips "
                      + "the value-proportional/absolute-floor branch instead of the shape-bounded pair, "
                      + "which is MORE restrictive on dust-valued inputs and can permanently false-reject "
                      + "an honest send — the opposite of a permissive default"
                    : $"{SendAsset}'s SigningPolicy no longer assigns {member}; its default is more "
                      + "permissive than the value this path requires");
            var normalized = string.Concat(actual!.ToString().Where(c => !char.IsWhiteSpace(c)));
            Assert.True(normalized == value,
                $"{SendAsset}: {member} must be `{value}`, it is `{normalized}`. These five values plus "
                + "AllowedScripts (checked below) are the whole of the SendAsset signing policy; any "
                + "drift is a security regression, not a "
                + "refactor. MaxFeeSats and MaxFeeSatsPerAdditionalInput must stay a PAIR here exactly as "
                + "on the create-UTXOs path, and for the same reason: collapsing the pair back into one "
                + "absolute number reintroduces a ceiling that cannot scale with the actual number of "
                + "inputs rgb-lib selects, which is a PERMANENT false-reject for any send that needs "
                + "enough of them.");
        }

        var allowed = Assert.IsType<ObjectCreationExpressionSyntax>(byMember["AllowedScripts"]);
        Assert.True(allowed.Type.ToString() == "HashSet<Script>",
            $"{SendAsset}: AllowedScripts must be a HashSet<Script>, it is `{allowed.Type}`");
        var element = Assert.Single(allowed.Initializer?.Expressions ?? default);
        var access = Assert.IsType<MemberAccessExpressionSyntax>(element);
        Assert.True(access.Name.Identifier.ValueText == "ScriptPubKey",
            $"{SendAsset}: the single allowed script must be a ScriptPubKey, it is `{element}`");

        var addressLocal = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        var declarator = RoslynPins.BodyOf(method).DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == addressLocal);
        var initializer = declarator.Initializer?.Value.ToString() ?? string.Empty;
        Assert.True(initializer.Contains("BitcoinAddress.Create", StringComparison.Ordinal)
                    && initializer.Contains("GetAddressAsync", StringComparison.Ordinal),
            $"{SendAsset}: the single allowed script must derive from this wallet's own address, "
            + $"obtained through GetAddressAsync; '{addressLocal}' is initialised from `{initializer}`");
    }

    [Fact]
    public void SendAssetSendBeginFeeRateArgument_IsTheSameRoundedLocalTheFeeCeilingIsBuiltFrom()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ServiceFile);
        var method = RoslynPins.Method(tree, ServiceType, SendAsset);
        var body = RoslynPins.BodyOf(method);

        var roundedDeclarators = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                && v.Initializer.Value.ToString() == "SendAssetRoundedFeeRate(feeRate)")
            .ToList();
        Assert.True(roundedDeclarators.Count == 1,
            $"{SendAsset} must declare exactly one local initialised from "
            + $"SendAssetRoundedFeeRate(feeRate); found {roundedDeclarators.Count}. The defect this pins "
            + "against is NOT two independent (int)Math.Round(feeRate) calls disagreeing with each other "
            + "— given the same feeRate, Math.Round is deterministic and two such calls always agree. It "
            + "is one rounded local feeding both rgb-lib and the fee ceiling versus a SEPARATE call site "
            + "that instead used the UNROUNDED float: at feeRate 1.49, rgb-lib builds at "
            + "round(1.49)=1 while a ceiling built from the float 1.49 directly is computed at a "
            + "different rate than the one the signer was told to enforce.");
        var roundedLocal = roundedDeclarators[0].Identifier.ValueText;

        var sendBeginCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "RunNativeSendIsolatedAsync"
                && i.ArgumentList.Arguments.Any(a => a.Expression is LiteralExpressionSyntax lit
                    && lit.Token.ValueText == "send-begin"))
            .ToList();
        Assert.True(sendBeginCalls.Count == 1,
            $"{SendAsset} must call RunNativeSendIsolatedAsync with operation \"send-begin\" exactly "
            + $"once; found {sendBeginCalls.Count}");
        var feeRateArgument = sendBeginCalls[0].ArgumentList.Arguments[3].Expression.ToString();
        Assert.True(feeRateArgument == roundedLocal,
            $"{SendAsset} hands send-begin's feeRate argument `{feeRateArgument}`, not the rounded local "
            + $"`{roundedLocal}` the fee ceiling below is built from. If these differ, rgb-lib can build "
            + "the transaction at one rate while the signer's fee ceiling is computed at another, which "
            + "is the exact shape of the feeRate-mismatch defect this pin exists to close.");

        var bodyText = body.ToString();
        Assert.Contains($"SendAssetMaxFeeSatsAtOneInput({roundedLocal})", bodyText, StringComparison.Ordinal);
        Assert.Contains(
            $"SendAssetMaxFeeSatsPerAdditionalInput({roundedLocal})", bodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void SendAssetSendEndFeeRateArgument_IsTheSameRoundedLocalTheFeeCeilingIsBuiltFrom()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ServiceFile);
        var method = RoslynPins.Method(tree, ServiceType, SendAsset);
        var body = RoslynPins.BodyOf(method);

        var roundedDeclarators = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                && v.Initializer.Value.ToString() == "SendAssetRoundedFeeRate(feeRate)")
            .ToList();
        Assert.True(roundedDeclarators.Count == 1,
            $"{SendAsset} must declare exactly one local initialised from "
            + $"SendAssetRoundedFeeRate(feeRate); found {roundedDeclarators.Count}. "
            + $"{nameof(SendAssetSendBeginFeeRateArgument_IsTheSameRoundedLocalTheFeeCeilingIsBuiltFrom)} "
            + "already explains why this local must be single and shared.");
        var roundedLocal = roundedDeclarators[0].Identifier.ValueText;

        var sendEndCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString() == "RunNativeSendIsolatedAsync"
                && i.ArgumentList.Arguments.Any(a => a.Expression is LiteralExpressionSyntax lit
                    && lit.Token.ValueText == "send-end"))
            .ToList();
        Assert.True(sendEndCalls.Count == 1,
            $"{SendAsset} must call RunNativeSendIsolatedAsync with operation \"send-end\" exactly "
            + $"once; found {sendEndCalls.Count}");
        var feeRateArgument = sendEndCalls[0].ArgumentList.Arguments[3].Expression.ToString();
        Assert.True(feeRateArgument == roundedLocal,
            $"{SendAsset} hands send-end's feeRate argument `{feeRateArgument}`, not the rounded local "
            + $"`{roundedLocal}` the fee ceiling is built from and send-begin also receives. Send-begin's "
            + "own argument is checked by "
            + $"{nameof(SendAssetSendBeginFeeRateArgument_IsTheSameRoundedLocalTheFeeCeilingIsBuiltFrom)}, "
            + "but that pin never inspects the send-end call, so a send-end fed the raw unrounded "
            + "feeRate instead would pass every other test in this file: rgb-lib would build or verify "
            + "the transfer at a rate the signer's own fee ceiling was never computed against.");
    }

    [Fact]
    public void SendAssetFeeCeilingHelpers_PassTheShapeConstantNotALiteralToEstimateTaprootFee()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ServiceFile);
        var model = plugin.Model(tree);

        var shapeConstantField = plugin.Compilation
            .GetTypeByMetadataName("BTCPayServer.Plugins.RgbUtexo.Services." + ServiceType)
            ?.GetMembers("SendAssetFeeShapeOutputCount").OfType<IFieldSymbol>().SingleOrDefault();
        Assert.True(shapeConstantField != null,
            $"{ServiceType}.SendAssetFeeShapeOutputCount does not resolve in the plugin compilation");

        foreach (var methodName in new[]
                 { "SendAssetMaxFeeSatsAtOneInput", "SendAssetMaxFeeSatsPerAdditionalInput" })
        {
            var method = RoslynPins.Method(tree, ServiceType, methodName);
            var body = RoslynPins.BodyOf(method);

            var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression.ToString() == "EstimateTaprootFee")
                .ToList();
            Assert.True(calls.Count > 0, $"{methodName} must call EstimateTaprootFee; found none");

            foreach (var call in calls)
            {
                var outputArg = call.ArgumentList.Arguments[1].Expression;
                var bound = model.GetSymbolInfo(outputArg).Symbol as IFieldSymbol;
                Assert.True(
                    bound != null && SymbolEqualityComparer.Default.Equals(bound, shapeConstantField),
                    $"{methodName}: EstimateTaprootFee's output-count argument is `{outputArg}`, not the "
                    + "SendAssetFeeShapeOutputCount field. "
                    + $"{nameof(RgbSignerFeeCeilingTests.SendAssetFeeShapeOutputCount_IsExactlyTwoNotThree)} "
                    + "in RgbSignerFeeCeilingTests only pins the field's declared VALUE (2); nothing else "
                    + "pinned that these two helpers actually CONSUME that field rather than a hardcoded "
                    + "literal — replacing this argument with a literal 3 in both helpers left the "
                    + "field's own value unchanged, compiled, and kept the whole managed suite green, "
                    + "while silently inflating the fee ceiling's base term for a PSBT shape this plugin "
                    + "can never produce.");
            }
        }
    }

    // (a) The flag belongs on exactly the two paths that sign a PSBT they did not build, and must NOT
    // reach asset-send, whose purpose is spending colored inputs.
    //
    // Bound to the ENCLOSING METHOD, not counted. Counting placements passes a swap — moving the flag
    // off Create-UTXOs and onto asset-send keeps the total at two — and that swap both reopens the
    // input gap on the rgb-lib-supplied PSBT and makes every RGB send refuse its own colored inputs,
    // with the whole suite still green. Review caught the counting version doing exactly that.
    [Fact]
    public void Flag_IsSetOnExactlyTheTwoIntendedSigningPolicies()
    {
        var expected = new Dictionary<string, bool>
        {
            ["CreateColorableUtxosInternalAsync"] = true,
            ["SendBtcInternalAsync"] = true,
            ["SendAssetInternalAsync"] = false
        };

        var plugin = PluginCompilation.Shared;
        var initializers = plugin.AllTrees
            .SelectMany(t => PolicyConstructionsIn(plugin, t, t.GetRoot()))
            .ToList();
        Assert.True(initializers.Count == expected.Count,
            $"the plugin constructs {initializers.Count} SigningPolicy object(s) — "
            + $"[{string.Join(", ", initializers.Select(EnclosingMethodName))}]; exactly "
            + $"{expected.Count} are mandated. This enumeration is REPO-WIDE and by BOUND TYPE, not "
            + "file-scoped and matched on the spelling of the type name: `SigningPolicy p = new() { … }` "
            + "in another service class is a new signing path that a text-matched, single-file "
            + "enumeration never sees, so it would reach a signer with no flag decision recorded "
            + "anywhere. Test sources are outside the plugin compile set, so the regtest fixture's "
            + "mirror policy is deliberately not counted here.");

        var seen = new Dictionary<string, bool>();
        foreach (var init in initializers)
        {
            var method = init.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            Assert.NotNull(method);
            var name = method!.Identifier.ValueText;
            Assert.True(expected.ContainsKey(name),
                $"a SigningPolicy is constructed in unexpected method '{name}' — decide whether it needs the guard");
            Assert.False(seen.ContainsKey(name), $"more than one SigningPolicy in '{name}'");

            seen[name] = init.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == Flag && a.Right.ToString() == "true") == true;
        }

        Assert.Equal(expected.Count, seen.Count);
        foreach (var (method, mustHaveFlag) in expected)
            Assert.True(seen[method] == mustHaveFlag,
                mustHaveFlag
                    ? $"{method} must set {Flag} = true: it signs a PSBT it did not build"
                    : $"{method} must NOT set {Flag}: spending colored inputs is its purpose");
    }

    // (b) The guard must run after PopulateInputKeyPaths, which supplies the key paths it verifies, and
    // before ValidateOutputs so an input-side refusal wins. Ordering is not observable behaviourally.
    [Fact]
    public void Guard_RunsBetweenPopulateAndValidateOutputs()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var sign = RoslynPins.Method(tree, SignerType, "SignPsbtAsync");
        var body = RoslynPins.BodyOf(sign);

        int PositionOf(string name) => body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(name, StringComparison.Ordinal))
            .Select(i => (int?)i.SpanStart)
            .FirstOrDefault() ?? -1;

        var populate = PositionOf("PopulateInputKeyPaths");
        var guard = PositionOf(Guard);
        var validate = PositionOf("ValidateOutputs");

        Assert.True(populate >= 0 && guard >= 0 && validate >= 0,
            $"missing call: populate={populate} guard={guard} validate={validate}");
        Assert.True(populate < guard, "the guard must run after PopulateInputKeyPaths");
        Assert.True(guard < validate, "the guard must run before ValidateOutputs");
    }

    // (c) The guard must not consult IsOwnScript's positive cache. That cache is keyed on the script
    // alone and is populated by matches against EVERY account, so reading it would answer "owned" for a
    // colored script and invert the very invariant this guard enforces.
    [Fact]
    public void Guard_DoesNotTouchTheOwnScriptCache()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, Guard));
        Assert.DoesNotContain("_verifiedScripts", body.ToString());
        Assert.DoesNotContain("IsOwnScript", body.ToString());
    }

    // (d1) The fee ceiling must resolve every input through GetTxOut(). Reading WitnessUtxo directly is
    // what let a producer understate the input value while the signature committed to the real amount,
    // so the ceiling passed and the difference was paid to miners.
    [Fact]
    public void FeeCeiling_ResolvesOnlyThroughGetTxOut()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, "ValidateOutputs")).ToString();
        Assert.DoesNotContain(".WitnessUtxo", body);
        Assert.DoesNotContain(".NonWitnessUtxo", body);
        Assert.Contains("GetTxOut()", body);
    }

    // (d2) Inside the guard the only permitted direct reads of the two utxo fields are the pair that
    // detects a disagreeing utxo pair — no accessor exposes both candidate txouts, so that comparison
    // cannot be written any other way. Every value used for a DECISION must come from GetTxOut().
    [Fact]
    public void Guard_ReadsUtxoFieldsOnlyToDetectDisagreement()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, Guard));

        var reads = body.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText is "WitnessUtxo" or "NonWitnessUtxo")
            .ToList();

        // Every such read must sit inside the if-block whose condition tests both fields for null.
        var disagreementBlock = body.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .SingleOrDefault(s => s.Condition.ToString().Contains("WitnessUtxo != null")
                               && s.Condition.ToString().Contains("NonWitnessUtxo != null"));
        Assert.NotNull(disagreementBlock);

        foreach (var read in reads)
            Assert.True(disagreementBlock!.Span.Contains(read.Span),
                $"utxo field read outside the disagreement check at offset {read.SpanStart}: {read}");

        Assert.Contains("GetTxOut()", body.ToString());
    }

    // (d3) PopulateInputKeyPaths legitimately reads WitnessUtxo and is unchanged by this work; the pins
    // above are scoped per member rather than file-wide precisely so it stays exempt. This asserts the
    // exemption is still needed, so a future file-wide tightening cannot quietly assume otherwise.
    [Fact]
    public void PopulateInputKeyPaths_StillReadsWitnessUtxoDirectly()
    {
        var tree = PluginCompilation.Shared.Tree(SignerFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, SignerType, "PopulateInputKeyPaths")).ToString();
        Assert.Contains("WitnessUtxo", body);
    }
}
