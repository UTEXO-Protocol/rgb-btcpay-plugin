using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins the wiring of the automatic UTXO-replenishment path. No test in this codebase constructs the
/// listener, so without these the shell could drop back to counting every Pending row, or ignore the
/// decision it just computed, while the whole suite stayed green.
///
/// Scope, stated so nobody mistakes it: these catch an ACCIDENTAL regression of the wiring — a refactor,
/// a merge, a well-meaning simplification. They are not a defence against a committer who intends to
/// remove the control, because whoever can edit the method can edit the pin. That case is caught by code
/// review and by the live end-to-end run.
/// </summary>
public class RgbListenerSourcePinTests
{
    const string ListenerFile = "Services/RGBInvoiceListener.cs";
    const string RgbLibFile = "Services/RgbLibService.cs";
    const string WalletServiceFile = "Services/RGBWalletService.cs";
    const string ControllerFile = "Controllers/RGBController.cs";
    const string StoreDataFullType = "BTCPayServer.Data.StoreData";
    const string ListenerType = "RGBInvoiceListener";
    // Fully qualified for symbol comparison: a same-simple-named type in another namespace, inherited
    // by the listener, supplies members that pass a simple-name compare — measured.
    const string ListenerFullType = "BTCPayServer.Plugins.RgbUtexo.Services.RGBInvoiceListener";
    const string Replenish = "ReplenishUtxosAsync";
    const string Recheck = "RecheckAutomaticReplenishmentAuthorizationAsync";
    const string PollLoop = "PollLoop";
    const string Refresh = "RefreshAllWallets";
    const string DemandFunction = "EvaluateReplenishDemand";
    const string ExactRequestCheck = "IsCurrentReplenishmentRequestAuthorized";
    const string GrantParameter = "standingAuthorizationGranted";
    const string GrantRead = "IsGrantedForWalletAsync";
    const string AuthorizationStoreType = "RgbAutoReplenishmentAuthorizationStore";
    const string AuthorizationStoreFullType =
        "BTCPayServer.Plugins.RgbUtexo.Services.RgbAutoReplenishmentAuthorizationStore";
    const string AuthorizationStoreField = "_authorizations";

    static MethodDeclarationSyntax ReplenishMethod(PluginCompilation plugin) =>
        RoslynPins.Method(plugin.Tree(ListenerFile), ListenerType, Replenish);

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static List<InvocationExpressionSyntax> InvocationsNamed(SyntaxNode scope, string name) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == name)
            .ToList();

    static List<InvocationExpressionSyntax> RepoWideInvocationsNamed(PluginCompilation plugin, string name) =>
        plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == name)
            .ToList();

    static InvocationExpressionSyntax Single(SyntaxNode scope, string name)
    {
        var found = InvocationsNamed(scope, name);
        Assert.True(found.Count == 1, $"expected exactly one '{name}' invocation, found {found.Count}");
        return found[0];
    }

    static string ContainingMethod(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<none>";

    static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        AwaitExpressionSyntax await_ => Unwrap(await_.Expression),
        ParenthesizedExpressionSyntax paren => Unwrap(paren.Expression),
        _ => expression
    };

    static List<ReturnStatementSyntax> ReturnsYieldingToTheFrameItself(SyntaxNode body)
    {
        var found = new List<ReturnStatementSyntax>();
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.Count > 0)
            foreach (var child in pending.Pop().ChildNodes())
            {
                if (child is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) continue;
                if (child is ReturnStatementSyntax statement) found.Add(statement);
                pending.Push(child);
            }
        return found.OrderBy(statement => statement.SpanStart).ToList();
    }

    static bool IsFalseLiteral(ExpressionSyntax? expression) =>
        expression is LiteralExpressionSyntax literal && literal.Token.IsKind(SyntaxKind.FalseKeyword);

    static bool YieldsNothingButResultOf(PluginCompilation plugin, SyntaxTree tree,
        SyntaxNode body, ExpressionSyntax? value, InvocationExpressionSyntax producer)
    {
        if (ReferenceEquals(value, producer)) return true;
        if (value is not IdentifierNameSyntax name) return false;
        if (RoslynPins.BoundSymbol(plugin, tree, name) is not ILocalSymbol) return false;
        var local = name.Identifier.ValueText;
        if (IsWrittenAfterDeclaration(body, local)) return false;
        var declarators = body.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(declarator => declarator.Identifier.ValueText == local)
            .ToList();
        return declarators.Count == 1
               && declarators[0].Initializer?.Value is { } initializer
               && ReferenceEquals(Unwrap(initializer), producer);
    }

    static bool IsWrittenAfterDeclaration(SyntaxNode body, string local)
    {
        static bool Names(ExpressionSyntax expression, string local) =>
            expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == local;

        return body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                   .Any(assignment => Names(assignment.Left, local))
               || body.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                   .Any(unary => (unary.IsKind(SyntaxKind.PreIncrementExpression)
                                  || unary.IsKind(SyntaxKind.PreDecrementExpression))
                                 && Names(unary.Operand, local))
               || body.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
                   .Any(unary => (unary.IsKind(SyntaxKind.PostIncrementExpression)
                                  || unary.IsKind(SyntaxKind.PostDecrementExpression))
                                 && Names(unary.Operand, local))
               || body.DescendantNodes().OfType<ArgumentSyntax>()
                   .Any(argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None)
                                    && Names(argument.Expression, local));
    }

    /// <summary>The single declarator's initializer for a local, with `await` unwrapped.</summary>
    static ExpressionSyntax InitializerOf(MethodDeclarationSyntax method, string localName)
    {
        var declarators = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == localName)
            .ToList();
        Assert.True(declarators.Count == 1,
            $"expected exactly one declarator named '{localName}' in {method.Identifier.ValueText}, "
            + $"found {declarators.Count}");
        var value = declarators[0].Initializer?.Value;
        Assert.True(value != null, $"'{localName}' has no initializer");
        return Unwrap(value!);
    }

    /// <summary>
    /// Pins which member produced a local, and optionally on which receiver. `List&lt;T&gt;.Count` is a
    /// property, so the initializer may be a member access rather than an invocation; both are accepted.
    /// The receiver matters: without it, `colorableCount = walletIds.Count` satisfies "produced by Count"
    /// while making the signing target depend on how many wallets exist rather than on colorable UTXOs.
    /// </summary>
    /// <summary>
    /// Pins the WHOLE call chain from the named root to the producer, not just its two ends.
    ///
    /// WHY the intermediates are enumerated rather than skipped over: an earlier version resolved the
    /// receiver by recursing through any intervening invocation, so it pinned only the ROOT of a chain.
    /// `var nowUnix = now.AddMinutes(-30).ToUnixTimeSeconds();` satisfied "produced by ToUnixTimeSeconds,
    /// from now" while shifting the clock back half an hour, which makes rows that expired in the last
    /// 30 minutes count as active and raises automatic signing demand — a false-ACCEPT on audit clause 2,
    /// using the exact arithmetic the live E2E used to prove that clause. `colorable.Take(1).Count()`
    /// slipped through the same gap. Any hop not named in <paramref name="through"/> is now a failure.
    /// </summary>
    static void AssertProducedBy(MethodDeclarationSyntax method, string localName, string producer,
        string? receiver = null, params string[] through)
    {
        var initializer = InitializerOf(method, localName);
        var (actual, tail) = initializer switch
        {
            InvocationExpressionSyntax invocation => (NameOf(invocation), InnerOf(invocation.Expression)),
            MemberAccessExpressionSyntax access => (access.Name.Identifier.ValueText, access.Expression),
            _ => (initializer.ToString(), null)
        };
        Assert.True(actual == producer, $"'{localName}' must be produced by {producer}, found '{actual}'");
        if (receiver == null) return;

        var hops = new List<string>();
        while (tail is InvocationExpressionSyntax hop)
        {
            hops.Add(NameOf(hop));
            tail = InnerOf(hop.Expression);
        }

        var root = (tail as IdentifierNameSyntax)?.Identifier.ValueText;
        Assert.True(root == receiver,
            $"'{localName}' must be produced from '{receiver}', found '{root ?? tail?.ToString() ?? "<none>"}'");
        Assert.True(hops.SequenceEqual(through),
            $"'{localName}' must reach '{receiver}' through [{string.Join(", ", through)}], "
            + $"found [{string.Join(", ", hops)}] — an unpinned hop can transform the pinned input");
    }

    /// <summary>The expression a member access is invoked on, or null if the shape is not a member access.</summary>
    static ExpressionSyntax? InnerOf(ExpressionSyntax expression) =>
        expression is MemberAccessExpressionSyntax access ? access.Expression : null;

    static ArgumentSyntax NamedArgument(InvocationExpressionSyntax invocation, string name)
    {
        var argument = invocation.ArgumentList.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name);
        Assert.True(argument != null,
            $"'{NameOf(invocation)}' must pass '{name}' as a named argument; found: "
            + string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.NameColon?.Name.Identifier.ValueText ?? "<positional>")));
        return argument!;
    }

    /// <summary>
    /// Pins a named argument to a member access — both the bound symbol AND the receiver it is read from.
    /// The receiver is not optional: `new RGBConfiguration().MaxAutoColorableUtxos` binds to exactly the
    /// same symbol as `_cfg.MaxAutoColorableUtxos` while ignoring the operator's configured cap, and
    /// `walletIds.Count` has the same leaf name as `colorable.Count`. Binding the leaf alone pins nothing.
    /// </summary>
    static void AssertArgumentBindsTo(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string parameter, string containingType, string member,
        string receiver)
    {
        var expression = NamedArgument(invocation, parameter).Expression;
        var access = Assert.IsType<MemberAccessExpressionSyntax>(expression);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
        Assert.True(symbol.Name == member && symbol.ContainingType?.Name == containingType,
            $"'{parameter}:' must bind to {containingType}.{member}, found "
            + $"{symbol.ContainingType?.Name}.{symbol.Name}");
        var actualReceiver = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        Assert.True(actualReceiver == receiver,
            $"'{parameter}:' must be read from '{receiver}', found '{actualReceiver}'");
    }

    static void AssertArgumentIsLocal(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string parameter, string localName)
    {
        var expression = NamedArgument(invocation, parameter).Expression;
        var identifier = Assert.IsType<IdentifierNameSyntax>(expression);
        Assert.Equal(localName, identifier.Identifier.ValueText);
        Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, identifier));
    }

    // ---- P-C1: clause 1, the enabled gate --------------------------------------------------------

    [Fact]
    public void PC1_TheOnlyArgumentBearingPaymentMethodConfigsLookup_AsksForEnabledOnly()
    {
        var plugin = PluginCompilation.Shared;
        var all = RepoWideInvocationsNamed(plugin, "GetPaymentMethodConfigs");
        Assert.True(all.Count == 6,
            $"the plugin has {all.Count} GetPaymentMethodConfigs invocations; the mandated total is 6 — "
            + "a new call site must be reviewed against finding C before this count is updated");

        var argumentBearing = all.Where(i => i.ArgumentList.Arguments.Count > 0).ToList();
        Assert.True(argumentBearing.Count == 2,
            $"exactly the initial and final automatic authorization checks may pass an argument, found {argumentBearing.Count}");

        Assert.Equal(
            new[] { Replenish, "RecheckAutomaticReplenishmentAuthorizationAsync" }.OrderBy(x => x),
            argumentBearing.Select(ContainingMethod).OrderBy(x => x));
        foreach (var call in argumentBearing)
        {
            var argument = call.ArgumentList.Arguments[0];
            var literal = Assert.IsType<LiteralExpressionSyntax>(argument.Expression);
            Assert.True(literal.IsKind(SyntaxKind.TrueLiteralExpression),
                "both automatic authorization checks must call GetPaymentMethodConfigs(onlyEnabled: true) — "
                + "the default overload returns methods the merchant has excluded");
        }
    }

    // ---- P-C2: clause 2, the active-invoice predicate --------------------------------------------

    [Fact]
    public void PC2_AllPendingCounts_GoThroughTheSharedActivePredicate()
    {
        var plugin = PluginCompilation.Shared;
        var invocations = RepoWideInvocationsNamed(plugin, "ActivePendingInvoicePredicate");
        Assert.True(invocations.Count == 3,
            $"expected exactly three ActivePendingInvoicePredicate invocations, found {invocations.Count}");
        Assert.Equal(
            new[] { "Utxos", Replenish, "RecheckAutomaticReplenishmentAuthorizationAsync" }
                .OrderBy(x => x, StringComparer.Ordinal),
            invocations.Select(ContainingMethod).OrderBy(x => x, StringComparer.Ordinal));

        // The absence claim is scoped to the provably-unique declaration: RGBInvoiceListener is not
        // partial, and seven other RGBInvoiceStatus.Pending references legitimately exist elsewhere.
        var replenish = ReplenishMethod(plugin);
        var pendingReferences = RoslynPins.BodyOf(replenish).DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(m => RoslynPins.NamesBclMember(m, "RGBInvoiceStatus", "Pending"))
            .ToList();
        Assert.True(pendingReferences.Count == 0,
            $"{Replenish} must not test RGBInvoiceStatus.Pending inline — that is the unfiltered count "
            + $"finding C is about; found {pendingReferences.Count}");
    }

    // ---- P-C3: the cheap gates precede the expensive call ----------------------------------------

    [Fact]
    public void PC3_EligibilityIsDecidedBeforeAnyRgbLibWork()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        var listUnspents = Single(replenish, "ListUnspentsAsync");
        Assert.True(eligibility.SpanStart < listUnspents.SpanStart,
            "the eligibility gates must run before ListUnspentsAsync, so a wallet whose store never "
            + "enabled RGB costs no rgb-lib work");
    }

    // ---- P-C4: the signing call's arguments ------------------------------------------------------

    [Fact]
    public void PC4_TheCreationRequestsExactlyWhatTheDemandFunctionDecided()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);

        Single(replenish, "EvaluateReplenishDemand");
        var create = Single(replenish, "CreateColorableUtxosAutomaticallyAsync");

        // Named, because positionally (id, decision.UtxoSize, decision.RequestCount, ct) compiles and
        // asks for 1000 UTXOs — the signature is (walletId, count = 4, size = 1000, ct).
        AssertArgumentBindsTo(plugin, tree, create, "walletId", "RGBWallet", "Id", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, create, "count", "ReplenishDecision", "RequestCount", receiver: "decision");
        AssertArgumentBindsTo(plugin, tree, create, "size", "ReplenishDecision", "UtxoSize", receiver: "decision");

        // Dropping ct would let the creation sign and broadcast during shutdown. It must be THIS method's
        // own CancellationToken parameter — any parameter symbol would also be satisfied by an added
        // `signingCt = default`, which is never cancelled.
        var ctIdentifier = Assert.IsType<IdentifierNameSyntax>(NamedArgument(create, "ct").Expression);
        var ctSymbol = Assert.IsAssignableFrom<IParameterSymbol>(RoslynPins.BoundSymbol(plugin, tree, ctIdentifier));
        var declared = plugin.Model(tree).GetDeclaredSymbol(replenish);
        Assert.True(declared != null, $"{Replenish} does not bind to a method symbol");
        // The sweep takes exactly one parameter, and `ct:` must BE it. Checking only "some parameter of this
        // method, of type CancellationToken" is not enough: adding `signingCt = default` and passing that
        // satisfies it while handing the creation a token that is never cancelled.
        Assert.True(declared!.Parameters.Length == 1,
            $"{Replenish} must take exactly one parameter (its CancellationToken), found "
            + $"{declared.Parameters.Length}: {string.Join(", ", declared.Parameters.Select(p => p.Name))}");
        Assert.True(ctSymbol.Equals(declared.Parameters[0], SymbolEqualityComparer.Default),
            $"'ct:' must be {Replenish}'s own cancellation token, found '{ctSymbol.Name}'");
        Assert.True(ctSymbol.Type.Name == "CancellationToken",
            $"'ct:' must be a CancellationToken, found {ctSymbol.Type.Name}");

        // The receiver too: `decision with { RequestCount = 5000 }` would otherwise satisfy the above.
        foreach (var parameter in new[] { "count", "size" })
        {
            var access = (MemberAccessExpressionSyntax)NamedArgument(create, parameter).Expression;
            var receiver = Assert.IsType<IdentifierNameSyntax>(access.Expression);
            Assert.Equal("decision", receiver.Identifier.ValueText);
        }
        AssertProducedBy(replenish, "decision", "EvaluateReplenishDemand");
    }

    [Fact]
    public void PC4b_AutomaticCreationCarriesAFreshStoreAuthorizationCallback()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);
        var create = Single(replenish, "CreateColorableUtxosAutomaticallyAsync");

        var lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(NamedArgument(create, "authorize").Expression);
        var recheck = Single(lambda, "RecheckAutomaticReplenishmentAuthorizationAsync");
        var recheckSymbol = RoslynPins.BoundSymbol(plugin, tree, recheck.Expression);
        Assert.True(recheckSymbol.Name == "RecheckAutomaticReplenishmentAuthorizationAsync"
                    && recheckSymbol.ContainingType?.Name == ListenerType,
            $"the automatic callback must bind to {ListenerType}.RecheckAutomaticReplenishmentAuthorizationAsync. "
            + "SCOPE OF THIS TEST, stated because it was read for one round as covering more than it "
            + $"does: it pins that the delegate handed to CreateColorableUtxosAutomaticallyAsync CALLS "
            + $"{Recheck} with those six arguments, and NOTHING about the value the delegate yields. "
            + "`authorizationCt => { await Recheck(the same six arguments); return true; }` satisfies "
            + "every clause here — it is still a SimpleLambdaExpressionSyntax, still contains exactly one "
            + $"{Recheck} invocation, and every argument still binds — while discarding the grant read, "
            + "the store-disabled and archived checks, the config-change check and the exact-request "
            + "check all at once, so one unattended transaction is signed per sweep for a store that "
            + "never granted or has revoked. Measured: the whole suite stayed green. What the delegate "
            + $"yields is pinned by {nameof(PC4h_TheAuthorizeDelegateYieldsNothingButTheFreshAuthorizationResult)}, "
            + $"and {nameof(PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck)} "
            + $"covers the returns of the {Recheck} METHOD, one frame further in.");

        Assert.Equal(6, recheck.ArgumentList.Arguments.Count);
        foreach (var (position, member) in new[] { (0, "Id"), (1, "StoreId") })
        {
            var access = Assert.IsType<MemberAccessExpressionSyntax>(recheck.ArgumentList.Arguments[position].Expression);
            Assert.Equal(member, access.Name.Identifier.ValueText);
            Assert.Equal("w", Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText);
        }

        Assert.Equal("config",
            Assert.IsType<IdentifierNameSyntax>(recheck.ArgumentList.Arguments[2].Expression).Identifier.ValueText);
        foreach (var (position, member) in new[] { (3, "RequestCount"), (4, "UtxoSize") })
        {
            var access = Assert.IsType<MemberAccessExpressionSyntax>(recheck.ArgumentList.Arguments[position].Expression);
            Assert.Equal(member, access.Name.Identifier.ValueText);
            Assert.Equal("decision", Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText);
        }
        var callbackCt = Assert.IsType<IdentifierNameSyntax>(recheck.ArgumentList.Arguments[5].Expression);
        Assert.Equal(lambda.Parameter.Identifier.ValueText, callbackCt.Identifier.ValueText);
    }

    [Fact]
    public void PC4h_TheAuthorizeDelegateYieldsNothingButTheFreshAuthorizationResult()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);
        var create = Single(replenish, "CreateColorableUtxosAutomaticallyAsync");
        var function = Assert.IsAssignableFrom<AnonymousFunctionExpressionSyntax>(
            NamedArgument(create, "authorize").Expression);
        var recheck = Single(function, Recheck);

        var whitelist =
            $"WHAT THIS PINS, as a WHITELIST rather than a list of ways to break it: the DELEGATE passed "
            + $"as 'authorize:' must yield nothing but what its single {Recheck} call returned. Its "
            + $"expression body must BE that call; or, if it has a block body, every `return` belonging "
            + "to the delegate itself must be EITHER the literal `return false;` — refusal is always "
            + "allowed, it is the fail-closed direction — OR must yield nothing but that call's result, "
            + "either directly or through one bare local initialized from it and never written again. "
            + "Nothing else, and exactly one return must be the authorizing one.\n"
            + "WHY THIS FRAME AND NOT THE METHOD'S: the value "
            + "CreateColorableUtxosAutomaticallyAsync branches on before it signs is what this DELEGATE "
            + $"yields, not what {Recheck} returns. "
            + $"{nameof(PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck)} "
            + "pins the method's returns and deliberately exempts returns inside nested lambdas, which is "
            + "correct for lambdas written INSIDE that method — but this lambda is written OUTSIDE it, so "
            + "no clause of it reaches here. "
            + $"{nameof(PC4b_AutomaticCreationCarriesAFreshStoreAuthorizationCallback)} pins that this "
            + "lambda CALLS the method with the right six arguments and says nothing about its result.\n"
            + "WHAT IT REFUSES, and why each is a false-ACCEPT and not a style objection: the "
            + $"demonstrated disarm `async authorizationCt => {{ await {Recheck}(…); return true; }}`, "
            + "which was MEASURED, before this clause existed, to leave every other pin green and the "
            + "whole suite passing while the grant read, the "
            + "revoke check, the store-disabled and archived checks, the config-change check and the "
            + "exact-request check are all computed and thrown away — one unattended signature per sweep "
            + "for a store that never granted or has revoked; an early `return true;` guarded by any "
            + $"condition; `return await {Recheck}(…) || true;`; `return !await {Recheck}(…);`; and "
            + $"wrapping the call as `return Wrapped(await {Recheck}(…));`, which lets the wrapper decide.\n"
            + "WHAT IT DELIBERATELY DOES NOT REFUSE, so the boundary is auditable rather than guessed: "
            + "either lambda body form; `async` or not; renaming the lambda's cancellation-token "
            + "parameter; assigning the call's result to a local and returning that local; and a "
            + "`return false;` added anywhere, which stops replenishment but cannot sign anything.";

        if (function is LambdaExpressionSyntax { ExpressionBody: { } expressionBody })
        {
            Assert.True(ReferenceEquals(Unwrap(expressionBody), recheck),
                $"{ListenerFile} {Replenish}: the 'authorize:' lambda's expression body is "
                + $"`{expressionBody}`, which is not the {Recheck} call itself. {whitelist}");
            return;
        }

        var block = function.Block;
        Assert.True(block != null,
            $"{ListenerFile} {Replenish}: the 'authorize:' delegate has neither an expression body nor "
            + $"a block body. {whitelist}");
        var returns = ReturnsYieldingToTheFrameItself(block!);
        Assert.True(returns.Count > 0,
            $"{ListenerFile} {Replenish}: the 'authorize:' lambda has a block body and no `return` of "
            + $"its own, so it cannot be yielding the authorization result. {whitelist}");

        var authorizing = new List<ReturnStatementSyntax>();
        foreach (var statement in returns)
        {
            var value = statement.Expression is null ? null : Unwrap(statement.Expression);
            if (IsFalseLiteral(value)) continue;
            if (YieldsNothingButResultOf(plugin, tree, block!, value, recheck))
            {
                authorizing.Add(statement);
                continue;
            }
            Assert.Fail(
                $"{ListenerFile} {Replenish}: `{statement.ToString().Trim()}` is not a legal return of "
                + $"the 'authorize:' lambda. {whitelist}");
        }

        Assert.True(authorizing.Count == 1,
            $"{ListenerFile} {Replenish}: {authorizing.Count} of the 'authorize:' lambda's "
            + $"{returns.Count} `return`(s) yield the result of {Recheck}; exactly one must. Zero means "
            + "the call is made and its value never leaves the delegate, so the delegate authorizes "
            + $"unconditionally while every clause of "
            + $"{nameof(PC4b_AutomaticCreationCarriesAFreshStoreAuthorizationCallback)} still holds. "
            + $"{whitelist}");
    }

    [Fact]
    public void PC4i_TheDelegateTheSignatureGateInvokesIsTheOneTheSweepConstructed()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletServiceFile);
        const string automatic = "CreateColorableUtxosAutomaticallyAsync";
        const string shared = "CreateColorableUtxosWithAuthorizationAsync";
        const string inner = "CreateColorableUtxosInternalAsync";
        const string parameter = "authorize";

        var reason =
            $"WHAT THIS PINS: the delegate {inner} invokes at its two authorization gates is the SAME "
            + $"object the sweep constructed. {nameof(PC4d_ManualAdminPathBypassesOnlyTheAutomaticAuthorizationCallback)} "
            + $"pins the hop from {automatic} into {shared}; "
            + $"{nameof(PC4c_AuthorizationIsRecheckedImmediatelyBeforeBothTheNativeBeginAndTheSignature)} "
            + $"pins that {inner} invokes it twice, each time as the statement immediately before "
            + "create_utxos_begin and the signature; and "
            + $"{nameof(PC4h_TheAuthorizeDelegateYieldsNothingButTheFreshAuthorizationResult)} pins what "
            + $"the delegate yields. This clause is the remaining hop — {shared} forwarding its own "
            + $"'{parameter}' parameter into {inner} — plus the requirement that no method on the chain "
            + $"WRITES that parameter. Without it, `{inner}(walletId, count, size, null, ct)` disarms the "
            + "whole gate silently: the parameter is nullable, both invocations inside "
            + $"{inner} are guarded by `{parameter} != null`, so no clause of the pins above notices. "
            + "MEASURED: that substitution reddens THIS clause and no other test in the suite, while each "
            + "sweep would sign and broadcast one unattended transaction. A THIRD caller of either "
            + "forwarding method is refused here for the same "
            + $"reason: {shared} accepts null to mean 'operator-driven, already authorized by the "
            + "request', so a new internal caller that passes null is a new unattended signing path with "
            + "no authorization at all.";

        var innerCalls = RepoWideInvocationsNamed(plugin, inner);
        Assert.True(innerCalls.Count == 1
                    && ContainingMethod(innerCalls[0]) == shared,
            $"{inner} is invoked {innerCalls.Count} time(s) "
            + $"({string.Join(", ", innerCalls.Select(ContainingMethod))}); exactly one call, from "
            + $"{shared}, is mandated. {reason}");

        var sharedCalls = RepoWideInvocationsNamed(plugin, shared);
        Assert.True(sharedCalls.Count == 2
                    && sharedCalls.Select(ContainingMethod).OrderBy(name => name, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "CreateColorableUtxosAsync", automatic }),
            $"{shared} is invoked from [{string.Join(", ", sharedCalls.Select(ContainingMethod))}]; "
            + $"exactly CreateColorableUtxosAsync and {automatic} are mandated. {reason}");

        var forward = Single(RoslynPins.Method(tree, "RGBWalletService", shared), inner);
        var forwarded = forward.ArgumentList.Arguments
            .Where(argument => Unwrap(argument.Expression) is IdentifierNameSyntax identifier
                               && identifier.Identifier.ValueText == parameter)
            .ToList();
        Assert.True(forwarded.Count == 1,
            $"{shared} passes `{forward.ArgumentList}` to {inner}; exactly one argument must be the bare "
            + $"'{parameter}' parameter. {reason}");
        Assert.IsAssignableFrom<IParameterSymbol>(
            RoslynPins.BoundSymbol(plugin, tree, Unwrap(forwarded[0].Expression)));

        foreach (var name in new[] { automatic, shared })
        {
            var method = RoslynPins.Method(tree, "RGBWalletService", name);
            Assert.False(IsWrittenAfterDeclaration(RoslynPins.BodyOf(method), parameter),
                $"{name} writes its '{parameter}' parameter. A reassignment to a delegate that returns "
                + $"true leaves every other clause green and removes the gate entirely. {reason}");
        }
    }

    [Fact]
    public void PC4e_FinalAuthorizationRecomputesFreshDemandAndRequiresAnExactRequest()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var recheck = RoslynPins.Method(
            tree, ListenerType, "RecheckAutomaticReplenishmentAuthorizationAsync");

        var list = Single(recheck, "ListUnspentsAsync");
        var count = Single(recheck, "CountAsync");
        var predicate = Single(recheck, "ActivePendingInvoicePredicate");
        var demand = Single(recheck, "EvaluateReplenishDemand");
        var exact = Single(recheck, ExactRequestCheck);
        var finalStore = Single(recheck, "FindStore");
        var grant = Single(recheck, "IsGrantedForWalletAsync");
        Assert.True(finalStore.SpanStart < grant.SpanStart,
            "the operator's standing-authorization read must come AFTER the final store read, and be the "
            + "LAST await in the callback. This is a PRIORITY CHOICE, not an invariant: exactly one of the "
            + "two reads can be last, and whichever runs first can be invalidated while the later one is in "
            + "flight. FindStore is a real Postgres round-trip on a fresh DbContext with no cache, so "
            + "reading the grant first left a revoke POST — the operator's emergency stop, which takes "
            + "neither the per-wallet send lock nor the native send lease — able to commit inside that "
            + "round-trip while this callback still returned true. The enabled/config snapshot loses the "
            + "tighter window instead, because invalidating it takes a deliberate settings edit. SCOPE OF "
            + "THIS CLAUSE, stated because an earlier wording overclaimed it: it pins WHERE the read "
            + $"happens and nothing about what is done with the value. `await …{GrantRead}(…) || true` "
            + "satisfies it, and satisfies every other clause in this test, while signing for a store that "
            + "never granted. That the value gates this callback's return is asserted in THREE HOPS, and "
            + "no hop alone suffices: "
            + $"{nameof(PC4f_BothAutomaticDemandDecisionsGateOnTheOperatorsStandingAuthorizationValue)} "
            + $"binds the grant local to {DemandFunction}'s '{GrantParameter}:' argument; the "
            + $"'currentDecision:' clause at the end of THIS test binds {ExactRequestCheck}'s "
            + $"'currentDecision:' ARGUMENT to the result of that same {DemandFunction} call — it says "
            + "nothing about what this callback returns; and "
            + $"{nameof(PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck)} "
            + $"binds the value this callback yields to the result of that {ExactRequestCheck} call. Each "
            + "hop was unpinned in turn and each gap was independently demonstrated to leave the whole "
            + "suite green while one unattended transaction was signed per sweep.");
        Assert.True(list.SpanStart < demand.SpanStart && count.SpanStart < demand.SpanStart,
            "the final demand decision must use UTXOs and invoices re-read inside the authorization callback");
        Assert.True(list.SpanStart < finalStore.SpanStart && count.SpanStart < finalStore.SpanStart,
            "slow UTXO and invoice reads must finish before the final enabled/config store read");
        Assert.True(demand.SpanStart < exact.SpanStart,
            "the freshly recomputed demand must feed the exact-request authorization");
        Assert.False(
            RoslynPins.BodyOf(recheck).DescendantNodes().OfType<AwaitExpressionSyntax>()
                .Any(await_ => await_.SpanStart > grant.Span.End),
            "nothing may await after the standing-authorization read. This NARROWS the revocation window "
            + "to the synchronous tail; it does not close it. ACCEPTED RESIDUAL: a sub-millisecond "
            + "check-then-act interval always remains — the demand evaluation, this callback's return, the "
            + "caller's branch on it and the await on the signature all run after the last grant read, so a "
            + "revoke that commits inside that interval is still followed by one signature. Closing it "
            + "would require the revoke path to take the per-wallet send lock and the native send lease. "
            + "This clause, like the ordering clause above, is about POSITION only: the narrowing it "
            + "describes is a property of the pair (read here, value consumed), and the second half is "
            + $"asserted by {nameof(PC4f_BothAutomaticDemandDecisionsGateOnTheOperatorsStandingAuthorizationValue)}, "
            + "the 'currentDecision:' clause below and "
            + $"{nameof(PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck)} "
            + "together.");

        var countArgument = count.ArgumentList.Arguments[0].Expression;
        Assert.Same(predicate, countArgument);
        Assert.Equal("walletId",
            Assert.IsType<IdentifierNameSyntax>(predicate.ArgumentList.Arguments[0].Expression).Identifier.ValueText);
        Assert.Equal("freshNowUnix",
            Assert.IsType<IdentifierNameSyntax>(predicate.ArgumentList.Arguments[1].Expression).Identifier.ValueText);

        var freshClock = InitializerOf(recheck, "freshNowUnix");
        var toUnix = Assert.IsType<InvocationExpressionSyntax>(freshClock);
        Assert.Equal("ToUnixTimeSeconds", NameOf(toUnix));
        var utcNow = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<MemberAccessExpressionSyntax>(toUnix.Expression).Expression);
        Assert.True(RoslynPins.NamesBclMember(utcNow, "DateTimeOffset", "UtcNow"),
            $"the final invoice predicate must use a fresh DateTimeOffset.UtcNow, found '{freshClock}'");

        foreach (var parameter in new[] { "expectedRequestCount", "expectedUtxoSize" })
        {
            var identifier = Assert.IsType<IdentifierNameSyntax>(NamedArgument(exact, parameter).Expression);
            Assert.Equal(parameter, identifier.Identifier.ValueText);
            Assert.IsAssignableFrom<IParameterSymbol>(RoslynPins.BoundSymbol(plugin, tree, identifier));
        }

        var decisionArgument = NamedArgument(exact, "currentDecision").Expression;
        Assert.True(decisionArgument is IdentifierNameSyntax,
            $"'currentDecision:' is written as '{decisionArgument}'. It must be a bare local holding "
            + $"nothing but what {DemandFunction} returned. SCOPE OF THIS CLAUSE, stated because an "
            + $"earlier wording overclaimed it: it constrains the ARGUMENT handed to {ExactRequestCheck} "
            + "and nothing about what this callback returns — "
            + $"{nameof(PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck)} "
            + "is what does that, and this clause was shipped for one round claiming to. Every other "
            + $"clause of this test constrains where the grant is READ and where {DemandFunction} is "
            + "CALLED. `currentDecision: new ReplenishDecision(ReplenishOutcome.Create, "
            + "expectedRequestCount, expectedUtxoSize)` satisfies all of them — the grant read still "
            + "happens last, the demand call still happens after the fresh UTXO and invoice reads, and "
            + "PC4f still binds that call's grant argument — while making the final pre-signature "
            + "authorization unconditionally true, so a revoked or disabled store gets one unattended "
            + "signature per sweep. Measured: the whole suite stayed green.");
        var decision = (IdentifierNameSyntax)decisionArgument;
        Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, decision));
        RoslynPins.AssertNeverReassigned(recheck, decision.Identifier.ValueText);
        Assert.Same(demand, InitializerOf(recheck, decision.Identifier.ValueText));
    }

    [Fact]
    public void PC4g_EveryReturnInTheFinalAuthorizationCallbackIsRefusalOrTheExactRequestCheck()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var recheck = RoslynPins.Method(tree, ListenerType, Recheck);
        var exact = Single(recheck, ExactRequestCheck);

        var exactSymbol = RoslynPins.BoundSymbol(plugin, tree, exact.Expression);
        Assert.True(exactSymbol.Name == ExactRequestCheck
                    && exactSymbol.ContainingType?.ToDisplayString() == ListenerFullType,
            $"the exact-request check must bind to {ListenerFullType}.{ExactRequestCheck}, not to a "
            + $"same-simple-named member reached through a base type or another namespace; it bound to "
            + $"'{exactSymbol.ContainingType?.ToDisplayString()}.{exactSymbol.Name}'");

        foreach (var argument in exact.ArgumentList.Arguments)
        {
            if (Unwrap(argument.Expression) is not IdentifierNameSyntax name) continue;
            if (RoslynPins.BoundSymbol(plugin, tree, name) is not IParameterSymbol) continue;
            Assert.False(IsWrittenAfterDeclaration(RoslynPins.BodyOf(recheck), name.Identifier.ValueText),
                $"{ListenerFile} {Recheck}: parameter '{name.Identifier.ValueText}' is written inside the "
                + $"method before it reaches {ExactRequestCheck}. The sweep's already-decided request "
                + "count and UTXO size arrive here as parameters and are the ONLY record of what "
                + "CreateColorableUtxosAutomaticallyAsync is about to sign; the exact-request check is "
                + "meaningful precisely because it compares the freshly recomputed demand against them "
                + "and nothing else. A clamp, a reassignment or a `++` on one of them makes the check "
                + "compare the recomputed demand against a value this method invented, so the stale "
                + "request the sweep decided is authorized anyway — the exact gap this callback exists "
                + "to close. An adjustment you believe is needed belongs in the sweep, before the "
                + "authorize callback is built.");
        }

        var whitelist =
            $"WHAT THIS PINS, as a WHITELIST rather than a list of ways to break it: every `return` "
            + $"belonging to {Recheck} itself must be EITHER the literal `return false;` — refusal is "
            + $"always allowed, it is the fail-closed direction — OR must yield nothing but what the "
            + $"single {ExactRequestCheck} call returned, either directly or through one bare local that "
            + "is initialized from that call and never written again. Nothing else. That is the whole "
            + "legal shape of this callback's authorization tail.\n"
            + "WHY A SHAPE AND NOT ONE MORE POSITION: three separate rounds each closed one more link of "
            + $"this same chain — the grant's '{GrantParameter}:' argument, then {ExactRequestCheck}'s "
            + "'currentDecision:' argument, then this return — and each fix left the next link open, "
            + "because a pin on a position says nothing about the positions beside it. The set of "
            + "positions a value can be discarded at is unbounded; the set of legal return shapes is "
            + "not. Enumerating the shapes closes the sequence: with the grant local bound to the demand "
            + "call (PC4f), the demand result bound to the exact check's argument (PC4e) and every "
            + $"return bound here, the value THE {Recheck} METHOD RETURNS is a pure function of the "
            + "freshly read grant, the freshly recomputed demand and the two unwritable expected-request "
            + "parameters, and no statement in the method can bypass, discard, override or invert it. "
            + "THAT IS A CLAIM ABOUT THE METHOD, NOT ABOUT THE DELEGATE THE SIGNING PATH BRANCHES ON, "
            + "and an earlier wording of this sentence said 'this callback', which licensed stopping "
            + "here: the 'authorize:' argument is a lambda written in "
            + $"{Replenish}, one frame further out, and `async ct => {{ await {Recheck}(…); return true; }}` "
            + "satisfies every clause of this test — the method is byte-identical — while discarding its "
            + "result. That frame is pinned by "
            + $"{nameof(PC4h_TheAuthorizeDelegateYieldsNothingButTheFreshAuthorizationResult)}, and the "
            + "forwarding of the delegate from the sweep to the signature gate by "
            + $"{nameof(PC4i_TheDelegateTheSignatureGateInvokesIsTheOneTheSweepConstructed)}. SCOPE, so "
            + $"this is not read as more than it is: the NON-grant arguments of {DemandFunction} in this "
            + "method are NOT pinned, and are on the debt list rather than closed. A wrong value there "
            + "can only make the recomputed demand disagree with the sweep's, which this check turns "
            + "into a refusal, and the grant is still ANDed ahead of it inside "
            + $"{DemandFunction} — so that gap cannot authorize anything the operator did not grant.\n"
            + "WHAT IT REFUSES, and why each is a false-ACCEPT and not a style objection: `return true;` "
            + $"anywhere — including after calling {ExactRequestCheck} as a bare expression statement, "
            + "which is the exact shape that was demonstrated to keep the whole suite green while "
            + "discarding the grant read, the fresh UTXO and invoice re-reads and the exact-request "
            + $"check all at once; `return Wrapped({ExactRequestCheck}(…));`, which lets a wrapper "
            + $"decide instead; `return !{ExactRequestCheck}(…);`; `return {ExactRequestCheck}(…) || "
            + "true;`; and a second conditional `return` yielding anything but `false`.\n"
            + "IF THIS FIRES ON A CHANGE YOU BELIEVE IS CORRECT: an extra condition you want ANDed onto "
            + "the result belongs above the tail as its own `if (…) return false;`, which this clause "
            + "accepts unchanged. Do not relax the clause to admit a compound return expression: `… || "
            + "true` and `!…` are both compound, and both authorize where they must not.\n"
            + "WHAT IT DELIBERATELY DOES NOT REFUSE, so the boundary is auditable rather than guessed: a "
            + "`return false;` inserted anywhere, which stops replenishment outright but cannot sign "
            + "anything; renaming any local; extracting the call into a local; and any `return` inside a "
            + $"nested lambda or local function DECLARED IN THIS METHOD, which yields to that function "
            + $"and not to {Recheck}. The 'authorize:' lambda is NOT such a case — it is declared in "
            + $"{Replenish}, outside this method, so nothing here reaches it and "
            + $"{nameof(PC4h_TheAuthorizeDelegateYieldsNothingButTheFreshAuthorizationResult)} is what "
            + "pins it.";

        var returns = ReturnsYieldingToTheFrameItself(RoslynPins.BodyOf(recheck));
        Assert.True(returns.Count > 0, $"{Recheck} has no `return` of its own to pin. {whitelist}");

        var authorizing = new List<ReturnStatementSyntax>();
        foreach (var statement in returns)
        {
            var value = statement.Expression is null ? null : Unwrap(statement.Expression);
            if (IsFalseLiteral(value)) continue;
            if (YieldsNothingButResultOf(plugin, tree, RoslynPins.BodyOf(recheck), value, exact))
            {
                authorizing.Add(statement);
                continue;
            }
            Assert.Fail(
                $"{ListenerFile} {Recheck}: `{statement.ToString().Trim()}` is not a legal return of the "
                + $"authorization tail. {whitelist}");
        }

        Assert.True(authorizing.Count == 1,
            $"{ListenerFile} {Recheck}: {authorizing.Count} of its {returns.Count} `return`(s) yield the "
            + $"result of {ExactRequestCheck}; exactly one must. Zero means the check is computed and its "
            + "value never leaves the method — the call is still present, so every clause of "
            + $"{nameof(PC4e_FinalAuthorizationRecomputesFreshDemandAndRequiresAnExactRequest)} still "
            + "holds, and the callback authorizes unconditionally. Moving the call into a delegate that "
            + $"is never invoked counts as zero here for the same reason. {whitelist}");
    }

    [Fact]
    public void PC4f_BothAutomaticDemandDecisionsGateOnTheOperatorsStandingAuthorizationValue()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);

        var demands = RepoWideInvocationsNamed(plugin, DemandFunction);
        Assert.True(demands.Count == 2,
            $"the plugin has {demands.Count} {DemandFunction} invocation(s) "
            + $"({string.Join(", ", demands.Select(ContainingMethod))}); exactly two are mandated — the "
            + $"sweep's initial decision in {Replenish} and the final pre-signature recheck in {Recheck}. "
            + "Any further site also decides whether an unattended signature happens, so it has to appear "
            + "here and bind its own grant explicitly rather than inherit this pin's silence.");
        Assert.Equal(
            new[] { Replenish, Recheck }.OrderBy(x => x, StringComparer.Ordinal),
            demands.Select(ContainingMethod).OrderBy(x => x, StringComparer.Ordinal));

        foreach (var demand in demands)
        {
            var site = ContainingMethod(demand);
            var where = $"{ListenerFile} {site}";
            var method = demand.Ancestors().OfType<MethodDeclarationSyntax>().First();

            var argument = NamedArgument(demand, GrantParameter).Expression;
            Assert.True(argument is IdentifierNameSyntax,
                $"{where}: '{GrantParameter}:' is written as '{argument}'. It must be a bare local holding "
                + $"nothing but what {AuthorizationStoreType}.{GrantRead} returned. A literal, or any "
                + "expression containing one, decouples the decision from the operator's grant while the "
                + $"whole suite stays green: ReplenishDecisionTests drives {DemandFunction} directly and "
                + "never observes the value a call site hands it. Neutered here, the sweep re-arms "
                + "unattended colorable-UTXO creation for every store that never granted — the shipped "
                + "default — and the recheck signs one transaction after a Revoke lands mid-flight.");
            var identifier = (IdentifierNameSyntax)argument;
            var local = identifier.Identifier.ValueText;
            Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, identifier));
            RoslynPins.AssertNeverReassigned(method, local);

            var initializer = InitializerOf(method, local);
            Assert.True(initializer is InvocationExpressionSyntax,
                $"{where}: '{local}' is initialized from '{initializer}'. It must be initialized from the "
                + $"awaited {GrantRead} call and from nothing else. `await …{GrantRead}(…) || true` leaves "
                + "the read, its position and its last-await placement all intact, so every clause of "
                + $"{nameof(PC4e_FinalAuthorizationRecomputesFreshDemandAndRequiresAnExactRequest)} is "
                + "satisfied by it, and the callback still authorizes a signature for a store that has "
                + "revoked.");
            var read = (InvocationExpressionSyntax)initializer;
            RoslynPins.AssertBindsToMemberOf(plugin, tree, read.Expression, SymbolKind.Method,
                AuthorizationStoreFullType, GrantRead, where);
            var access = Assert.IsType<MemberAccessExpressionSyntax>(read.Expression);
            RoslynPins.AssertBindsToMemberOf(plugin, tree, access.Expression, SymbolKind.Field,
                ListenerFullType, AuthorizationStoreField, where);

            Assert.True(read.ArgumentList.Arguments.Count == 3,
                $"{where}: the grant read is written {GrantRead}{read.ArgumentList}. It must pass the "
                + "store, the configured wallet and a cancellation token: the grant is recorded per store "
                + "FOR a named wallet, and dropping the wallet argument would let a grant made for a "
                + "replaced wallet authorize signing for its successor.");
            foreach (var passed in read.ArgumentList.Arguments)
                Assert.True(passed.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax,
                    $"{where}: the grant read is passed '{passed.Expression}'. Every argument must be a "
                    + "reference to this site's own state; a constant identity reads whatever grant some "
                    + "other store recorded, or none at all.");
        }
    }

    [Fact]
    public void PC4c_AuthorizationIsRecheckedImmediatelyBeforeBothTheNativeBeginAndTheSignature()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(WalletServiceFile);
        var internalCreate = RoslynPins.Method(
            tree, "RGBWalletService", "CreateColorableUtxosInternalAsync");
        var authorizations = InvocationsNamed(internalCreate, "authorize");
        Assert.True(authorizations.Count == 2,
            "CreateColorableUtxosInternalAsync must invoke the authorization callback exactly twice — once "
            + "immediately before create_utxos_begin and once immediately before the signature — and it "
            + $"invokes it {authorizations.Count} time(s). The second recheck is what NARROWS the window in "
            + "which a disable, a revocation or a settings edit lands after the unsigned transaction has "
            + "been built; without it that window spans the whole of the slow native create_utxos_begin and "
            + "yields one signed transaction the operator no longer authorized. It does not narrow that "
            + "window to nothing: the recheck's own synchronous tail and the await on the signature remain "
            + "after its last read, which is the accepted residual PC4e records.");

        var nativeBegin = Single(internalCreate, "CreateUtxosBeginAsync");
        var signature = Single(internalCreate, "SignPsbtWithSignerAsync");
        Assert.True(nativeBegin.SpanStart < signature.SpanStart,
            "create_utxos_begin must precede the signature");

        var guards = authorizations
            .Select(a => a.Ancestors().OfType<IfStatementSyntax>().First())
            .OrderBy(g => g.SpanStart)
            .ToList();

        foreach (var guard in guards)
        {
            Assert.Empty(guard.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>());
            var refusal = guard.Statement.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Single();
            var creation = Assert.IsType<ObjectCreationExpressionSyntax>(refusal.Expression);
            Assert.Equal("RgbAutomaticReplenishmentNotAuthorizedException", creation.Type.ToString());
        }

        void AssertImmediatelyFollowedBy(IfStatementSyntax guard, InvocationExpressionSyntax gated,
            string what)
        {
            var block = RoslynPins.BodyOf(internalCreate).DescendantNodes()
                .OfType<BlockSyntax>()
                .Single(b => b.Statements.Contains(guard));
            var gatedStatement = gated.Ancestors().OfType<StatementSyntax>()
                .FirstOrDefault(s => s.Parent == block);
            Assert.True(gatedStatement != null,
                $"{what} must sit in the same statement block as the authorization guard that gates it, so "
                + "no statement can be inserted between them");
            Assert.True(block.Statements.IndexOf(gatedStatement!) == block.Statements.IndexOf(guard) + 1,
                $"the authorization guard must be the statement IMMEDIATELY before {what}. Anything "
                + "between them — in particular any await — reopens the window this pin exists to close.");
        }

        AssertImmediatelyFollowedBy(guards[0], nativeBegin, "create_utxos_begin");
        AssertImmediatelyFollowedBy(guards[1], signature, "the signature");
    }

    [Fact]
    public void PC4f_ArchivedIsReadOnlyOnTheUnattendedSigningAuthorizationPath()
    {
        var plugin = PluginCompilation.Shared;
        var listenerTree = plugin.Tree(ListenerFile);
        var controllerTree = plugin.Tree(ControllerFile);
        const string recheckName = "RecheckAutomaticReplenishmentAuthorizationAsync";
        const string predicateName = "IsAutomaticReplenishmentAuthorized";
        const string settingsViewModelPopulator = "PopulateSettingsViewModel";
        var authorizationMethods = new[] { recheckName, predicateName };
        var allowedSites = new[]
        {
            (Tree: listenerTree, Method: recheckName),
            (Tree: listenerTree, Method: predicateName),
            (Tree: controllerTree, Method: settingsViewModelPopulator)
        };
        const string allowedSitesReason =
            "Only RGBInvoiceListener.RecheckAutomaticReplenishmentAuthorizationAsync and "
            + "RGBInvoiceListener.IsAutomaticReplenishmentAuthorized — the unattended-signing "
            + "authorization decision, the one place archiving is allowed to pause anything — plus "
            + "RGBController.PopulateSettingsViewModel, whose read only fills the settings page's "
            + "StoreArchived display flag and gates no payment at all, may consult it. This clause binds the SYMBOL behind "
            + "every name spelled 'Archived', not a MemberAccess node shape, because store?.Archived "
            + "parses as ConditionalAccess + MemberBinding and a property pattern parses as neither — "
            + "both were invisible to the shape-based scan this replaced.";

        var everySpellingOfTheName = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<SimpleNameSyntax>()
                .Where(n => n.Identifier.ValueText == "Archived")
                .Select(n => (tree, node: (SyntaxNode)n)))
            .ToList();

        var reads = new List<(SyntaxTree Tree, SyntaxNode Node)>();
        foreach (var (tree, node) in everySpellingOfTheName)
        {
            if (RoslynPins.BoundSymbol(plugin, tree, node) is IPropertySymbol property
                && property.Name == "Archived"
                && property.ContainingType?.ToDisplayString() == StoreDataFullType)
                reads.Add((tree, node));
        }

        Assert.True(reads.Any(r => r.Tree == listenerTree && ContainingMethod(r.Node) == recheckName),
            $"{StoreDataFullType}.Archived is never read inside {recheckName}, so archiving no longer "
            + "pauses unattended replenishment at all");

        foreach (var (tree, node) in reads)
        {
            var member = ContainingMethod(node);
            Assert.True(allowedSites.Any(s => s.Tree == tree && s.Method == member),
                $"{tree.FilePath}: {StoreDataFullType}.Archived is read in '{member}'. "
                + allowedSitesReason
                + " Archiving is a store-LIST visibility flag in BTCPay: an archived store's RGB payment "
                + "method is still enabled and its checkout still works. Extending this check to the "
                + "receive or settlement path would stop the listener detecting or settling payments on "
                + "invoices that already exist, stranding real customer money.");
        }

        var parameterUses = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(i => i.Identifier.ValueText == "storeArchived")
                .Select(i => (tree, node: (SyntaxNode)i)))
            .ToList();
        Assert.True(parameterUses.All(u => u.tree == listenerTree
                                           && authorizationMethods.Contains(ContainingMethod(u.node))),
            "the archived flag is threaded only through the unattended-signing authorization predicate: "
            + string.Join(", ", parameterUses.Select(u => ContainingMethod(u.node))));

        var recheck = RoslynPins.Method(listenerTree, ListenerType, recheckName);
        var archivedWarnings = RoslynPins.BodyOf(recheck).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "LogWarning"
                        && i.ArgumentList.Arguments.Count > 0
                        && i.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax message
                        && ((string?)message.Token.Value ?? string.Empty)
                            .Contains("archived", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(archivedWarnings.Count == 1,
            $"{recheckName} must log exactly one Warning naming archiving as the reason replenishment "
            + $"stopped; it logs {archivedWarnings.Count}. Each refusal cause — disabled, config changed, "
            + "revoked authorization, archived — must be separately identifiable, because a silent stop "
            + "drains the colorable pool and makes the RGB option vanish from checkout with nothing "
            + "pointing at the cause.");
    }

    [Fact]
    public void PC4d_ManualAdminPathBypassesOnlyTheAutomaticAuthorizationCallback()
    {
        var tree = PluginCompilation.Shared.Tree(WalletServiceFile);
        var root = tree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var manual = methods.Single(m => m.Identifier.ValueText == "CreateColorableUtxosAsync");
        var automatic = methods.Single(m => m.Identifier.ValueText == "CreateColorableUtxosAutomaticallyAsync");

        var manualCommon = Single(manual, "CreateColorableUtxosWithAuthorizationAsync");
        Assert.True(manualCommon.ArgumentList.Arguments[3].Expression.IsKind(SyntaxKind.NullLiteralExpression),
            "the explicit admin method must remain usable without the listener-only authorization callback");
        var automaticCommon = Single(automatic, "CreateColorableUtxosWithAuthorizationAsync");
        Assert.Equal("authorize",
            Assert.IsType<IdentifierNameSyntax>(automaticCommon.ArgumentList.Arguments[3].Expression).Identifier.ValueText);
    }

    // ---- P-C5: no second automatic path, and the tracker is actually wired ------------------------

    [Fact]
    public void PC5_OnlyTheListenerAndTheAdminButtonCreateUtxos()
    {
        var plugin = PluginCompilation.Shared;
        var automatic = RepoWideInvocationsNamed(plugin, "CreateColorableUtxosAutomaticallyAsync");
        Assert.Single(automatic);
        Assert.Equal("RGBWalletService",
            RoslynPins.BoundSymbol(plugin, automatic[0].SyntaxTree, automatic[0].Expression)
                .ContainingType?.Name);

        var manual = RepoWideInvocationsNamed(plugin, "CreateColorableUtxosAsync");
        Assert.Single(manual);
        Assert.Equal("IRGBWalletService",
            RoslynPins.BoundSymbol(plugin, manual[0].SyntaxTree, manual[0].Expression)
                .ContainingType?.Name);

        foreach (var member in new[]
                 {
                     "NextEligibleAt", "RecordAttemptSucceeded", "RecordAttemptFailed",
                     "RecordNoActionNeeded", "Prune"
                 })
        {
            var found = RepoWideInvocationsNamed(plugin, member);
            Assert.True(found.Count == 1,
                $"expected exactly one '{member}' invocation in the plugin, found {found.Count}");
        }
    }

    // ---- P-C6: the decision reads a freshly-read row ---------------------------------------------

    [Fact]
    public void PC6_TheWalletRowIsReReadBeforeTheDecision()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);
        var fresh = Single(replenish, "FirstOrDefaultAsync");
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        Assert.True(fresh.SpanStart < eligibility.SpanStart,
            "the wallet row must be re-read before eligibility is decided — the sweep-start list is a "
            + "snapshot, and a concurrent send can quarantine a wallet inside the same sweep");
    }

    // ---- P-C8: a failed UTXO listing must not look like an empty wallet --------------------------

    [Fact]
    public void PC8_AFailedUnspentsListing_ThrowsRatherThanReportingZeroUtxos()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var list = RoslynPins.Method(tree, "RgbLibService", "InterpretListUnspents");
        var body = RoslynPins.BodyOf(list);

        // Returning an empty list on a failed native call made an error indistinguishable from "this wallet
        // has no UTXOs". The replenishment sweep then saw zero colorable UTXOs, computed zero free slots and
        // signed a creation *because of the failure* — observed live on 2026-08-04 against a wallet holding
        // 23 UTXOs. A genuinely empty wallet returns Ok with "[]", so a null payload only ever means failure.
        // Pin the null-payload BRANCH, not merely "a throw exists somewhere": accepting any throw anywhere
        // in the method would let a silent revert of this very branch pass. The behavioural counterpart is
        // RgbNativeSiteTests.ListUnspents_ThrowsOnFailure_InsteadOfReportingAnEmptyPool, which runs it.
        var nullChecks = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is BinaryExpressionSyntax bin
                        && bin.IsKind(SyntaxKind.EqualsExpression)
                        && bin.Left is IdentifierNameSyntax
                        && bin.Right.IsKind(SyntaxKind.NullLiteralExpression))
            .ToList();
        Assert.True(nullChecks.Count == 1,
            $"InterpretListUnspents must test its payload for null exactly once, found {nullChecks.Count}");
        var guarded = nullChecks[0].Statement;
        var throwsInBranch = guarded is ThrowStatementSyntax
                             || guarded.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any();
        Assert.True(throwsInBranch,
            "the null-payload branch must throw — returning any value there makes a failed native call "
            + "indistinguishable from a wallet with no UTXOs, which drove a real signed creation");

        // …and no value may be produced for that failure, in either the `new List<…>()` or the collection
        // expression form (`return [];`), which is the prevailing style in this very method.
        var manufacturedEmpty = guarded.DescendantNodesAndSelf().Where(n =>
            (n is ObjectCreationExpressionSyntax o && o.Type.ToString().Contains("List<UnspentOutput>"))
            || n is CollectionExpressionSyntax).ToList();
        Assert.True(manufacturedEmpty.Count == 0,
            $"the null-payload branch must not manufacture a UTXO list, found {manufacturedEmpty.Count}");
    }

    // ---- P-C7: provenance, mutation and structure ------------------------------------------------

    [Fact]
    public void PC7_EveryDecisionInputComesFromWhereItMust()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);
        var eligibility = Single(replenish, "EvaluateReplenishEligibility");
        var demand = Single(replenish, "EvaluateReplenishDemand");

        // A local function shadowing any pinned name compiles without a warning and satisfies every node
        // assertion below while the real member never runs (phase 1a's standing rule 2b).
        RoslynPins.AssertNoLocalShadow(replenish,
            "EvaluateReplenishEligibility", "EvaluateReplenishDemand", "ActivePendingInvoicePredicate",
            "ListUnspentsAsync", "FirstOrDefaultAsync", "CountAsync", "TryGetValue", "FindStore",
            "GetPaymentMethodConfigs", "CreateColorableUtxosAutomaticallyAsync",
            "NextEligibleAt", "RecordAttemptSucceeded", "RecordAttemptFailed", "RecordNoActionNeeded", "Prune");

        // Provenance: each pinned local is produced by the call that must produce it.
        AssertProducedBy(replenish, "w", "FirstOrDefaultAsync");
        AssertProducedBy(replenish, "store", "FindStore");
        AssertProducedBy(replenish, "configs", "GetPaymentMethodConfigs", receiver: "store");
        // `config` is built by the single `enabled && tok is not null ? … : null` declarator. Without this,
        // a config parsed from another store's token supplies that store's UtxoCount/UtxoSize.
        var configInit = InitializerOf(replenish, "config");
        var configTernary = Assert.IsType<ConditionalExpressionSyntax>(configInit);
        // Nodes, not text: a `Contains("enabled")` on the condition's ToString() is exactly the
        // node-not-text evasion the standing rules forbid (a comment or a renamed local defeats it).
        var condition = Assert.IsType<BinaryExpressionSyntax>(configTernary.Condition);
        Assert.True(condition.IsKind(SyntaxKind.LogicalAndExpression),
            $"config's condition must be a logical AND, found {condition.Kind()}");
        var enabledOperand = Assert.IsType<IdentifierNameSyntax>(condition.Left);
        Assert.IsAssignableFrom<ILocalSymbol>(RoslynPins.BoundSymbol(plugin, tree, enabledOperand));
        Assert.Equal("enabled", enabledOperand.Identifier.ValueText);
        var tokPattern = Assert.IsType<IsPatternExpressionSyntax>(condition.Right);
        Assert.Equal("tok", Assert.IsType<IdentifierNameSyntax>(tokPattern.Expression).Identifier.ValueText);
        Assert.Equal("ToObject", NameOf(Assert.IsType<InvocationExpressionSyntax>(configTernary.WhenTrue)));
        Assert.True(configTernary.WhenFalse.IsKind(SyntaxKind.NullLiteralExpression),
            "config must be null when the payment method is not enabled");
        AssertProducedBy(replenish, "enabled", "TryGetValue");
        AssertProducedBy(replenish, "utxos", "ListUnspentsAsync");
        AssertProducedBy(replenish, "colorable", "ToList", receiver: "utxos", through: "Where");
        AssertProducedBy(replenish, "colorableCount", "Count", receiver: "colorable");
        AssertProducedBy(replenish, "usedByColorings", "Sum", receiver: "colorable");
        AssertProducedBy(replenish, "activePendingInvoices", "CountAsync");
        AssertProducedBy(replenish, "nowUnix", "ToUnixTimeSeconds", receiver: "now");
        // `var now = _lastUtxoCheck;` would keep every other pin green while making rows that expired during
        // the previous sweep count as active, raising demand.
        var nowInit = InitializerOf(replenish, "now");
        var nowAccess = Assert.IsType<MemberAccessExpressionSyntax>(nowInit);
        Assert.True(RoslynPins.NamesBclMember(nowAccess, "DateTimeOffset", "UtcNow"),
            $"'now' must be DateTimeOffset.UtcNow, found '{nowInit}'");

        // The store lookup and the config key: a wrong store or a wrong payment method would authorise
        // signing from configuration unrelated to this wallet.
        var findStore = Single(replenish, "FindStore");
        var storeIdAccess = Assert.IsType<MemberAccessExpressionSyntax>(findStore.ArgumentList.Arguments[0].Expression);
        var storeIdSymbol = RoslynPins.BoundSymbol(plugin, tree, storeIdAccess);
        // Leaf name alone would let `otherEntity.StoreId` through and supply another store's UtxoCount.
        Assert.True(storeIdSymbol.Name == "StoreId" && storeIdSymbol.ContainingType?.Name == "RGBWallet",
            $"FindStore's argument must be RGBWallet.StoreId, found "
            + $"{storeIdSymbol.ContainingType?.Name}.{storeIdSymbol.Name}");
        Assert.True(Assert.IsType<IdentifierNameSyntax>(storeIdAccess.Expression).Identifier.ValueText == "w",
            "FindStore's argument must be read from the fresh wallet local 'w'");
        var tryGetValue = Single(replenish, "TryGetValue");
        var keyAccess = Assert.IsType<MemberAccessExpressionSyntax>(tryGetValue.ArgumentList.Arguments[0].Expression);
        var keySymbol = RoslynPins.BoundSymbol(plugin, tree, keyAccess);
        // Bound, not name-matched: `AnyOtherClass.RGBPaymentMethodId` would satisfy a syntactic comparison,
        // which is the standing semantic-binding rule this file is required to obey.
        Assert.True(keySymbol.Name == "RGBPaymentMethodId" && keySymbol.ContainingType?.Name == "RGBPlugin",
            $"the config key must be RGBPlugin.RGBPaymentMethodId, found "
            + $"{keySymbol.ContainingType?.Name}.{keySymbol.Name}");

        // Eligibility's arguments.
        AssertArgumentBindsTo(plugin, tree, eligibility, "walletId", "RGBWallet", "Id", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "isActive", "RGBWallet", "IsActive", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "needsRecovery", "RGBWallet", "NeedsRecovery", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, eligibility, "maxAllocationsPerUtxo", "RGBWallet", "MaxAllocationsPerUtxo", receiver: "w");
        AssertArgumentIsLocal(plugin, tree, eligibility, "paymentMethodEnabled", "enabled");
        AssertArgumentIsLocal(plugin, tree, eligibility, "now", "now");

        // The active RGBWallet row is authoritative. Greenfield replacement updates may omit the
        // legacy config pointer, so replenishment must not silently unbind the store in that case.
        AssertArgumentBindsTo(plugin, tree, eligibility, "configuredWalletId", "RGBWallet", "Id", receiver: "w");

        // The cooldown read itself, not just the wallet id inside it.
        var nextEligible = NamedArgument(eligibility, "nextEligibleAt").Expression;
        Assert.Equal("NextEligibleAt", NameOf(Assert.IsType<InvocationExpressionSyntax>(nextEligible)));

        // Demand's arguments.
        AssertArgumentIsLocal(plugin, tree, demand, "colorableCount", "colorableCount");
        AssertArgumentIsLocal(plugin, tree, demand, "usedByColorings", "usedByColorings");
        AssertArgumentIsLocal(plugin, tree, demand, "activePendingInvoices", "activePendingInvoices");
        AssertArgumentBindsTo(plugin, tree, demand, "maxAllocationsPerUtxo", "RGBWallet", "MaxAllocationsPerUtxo", receiver: "w");
        AssertArgumentBindsTo(plugin, tree, demand, "minFreeSlots", "RGBPaymentMethodConfig", "UtxoCount", receiver: "config");
        AssertArgumentBindsTo(plugin, tree, demand, "utxoSize", "RGBPaymentMethodConfig", "UtxoSize", receiver: "config");
        AssertArgumentBindsTo(plugin, tree, demand, "maxAutoColorableUtxos", "RGBConfiguration", "MaxAutoColorableUtxos", receiver: "_cfg");

        // The predicate's own two arguments: a literal 0 or another wallet's id reverts clause 2.
        var predicate = Single(replenish, "ActivePendingInvoicePredicate");

        // The predicate invocation must BE the CountAsync argument, not merely exist in the method. Pinning
        // "activePendingInvoices comes from CountAsync" and "a predicate call exists with the right
        // arguments" separately leaves a compiling hole: keep the call as a discard and pass
        // `i => i.WalletId == w.Id` to CountAsync, and every pin stays green while expired and settled rows
        // count toward automatic signing demand — the false-ACCEPT direction, on the audit's own clause 2.
        // One level of naming is allowed — `var p = ActivePendingInvoicePredicate(...); CountAsync(p, ct)`
        // preserves the property exactly — but the value passed must still resolve to the pinned invocation.
        // A pin that fails on a correct refactor teaches people to delete pins.
        var countCall = Assert.IsType<InvocationExpressionSyntax>(
            InitializerOf(replenish, "activePendingInvoices"));
        Assert.Equal("CountAsync", NameOf(countCall));
        var countArgument = countCall.ArgumentList.Arguments[0].Expression;
        if (countArgument is IdentifierNameSyntax named)
            countArgument = InitializerOf(replenish, named.Identifier.ValueText);
        Assert.Same(predicate, countArgument);
        var predicateWallet = Assert.IsType<MemberAccessExpressionSyntax>(predicate.ArgumentList.Arguments[0].Expression);
        var predicateWalletSymbol = RoslynPins.BoundSymbol(plugin, tree, predicateWallet);
        Assert.True(predicateWalletSymbol.Name == "Id" && predicateWalletSymbol.ContainingType?.Name == "RGBWallet",
            "the predicate's wallet id must be RGBWallet.Id");
        Assert.True(Assert.IsType<IdentifierNameSyntax>(predicateWallet.Expression).Identifier.ValueText == "w",
            "the predicate's wallet id must be read from the fresh wallet local 'w'");
        Assert.Equal("nowUnix",
            Assert.IsType<IdentifierNameSyntax>(predicate.ArgumentList.Arguments[1].Expression).Identifier.ValueText);

        // Every LINQ selector: Sum(u => u.RgbAllocations.Count + 1) would inflate demand on every wallet,
        // and Where(u => true) would mis-count the colorable set. The two Where clauses are the sweep's
        // IsActive filter and the colorable filter — both load-bearing, so both are pinned.
        var wheres = InvocationsNamed(RoslynPins.BodyOf(replenish), "Where");
        Assert.True(wheres.Count == 2, $"expected exactly two Where clauses, found {wheres.Count}");
        Assert.Equal(
            new[] { "Colorable", "IsActive" },
            wheres.Select(w => SelectorMember(plugin, tree, w)).OrderBy(x => x, StringComparer.Ordinal));
        // The colorable filter gets the same whole-path treatment as Sum: a leaf named "Colorable" reached
        // from anything other than the lambda's own parameter would mis-count the colorable set.
        AssertSelectorPath(plugin, tree,
            wheres.Single(w => SelectorMember(plugin, tree, w) == "Colorable"), "Utxo", "Colorable");
        AssertSelectorPath(plugin, tree, Single(replenish, "Sum"), "RgbAllocations", "Count");

        // Every wallet-id argument inside the loop is w.Id. Two carve-outs, both structural: the fresh
        // read keys on the loop's id because w is what it produces, and the outer catch logs id because
        // w is scoped inside the try.
        foreach (var call in new[] { "ListUnspentsAsync", "NextEligibleAt", "RecordAttemptSucceeded",
                     "RecordAttemptFailed", "RecordNoActionNeeded" })
        {
            var invocation = Single(replenish, call);
            var access = Assert.IsType<MemberAccessExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
            var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
            // Leaf name alone pins nothing: `store.Id` is also named "Id", and keying the tracker off the
            // store would mean gate 2 never fires for the wallet — the retry storm, with every pin green.
            Assert.True(symbol.Name == "Id" && symbol.ContainingType?.Name == "RGBWallet",
                $"{call}'s wallet id must be RGBWallet.Id, found "
                + $"{symbol.ContainingType?.Name}.{symbol.Name}");
            Assert.True(Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText == "w",
                $"{call}'s wallet id must be read from the fresh wallet local 'w', found '{access.Expression}'");
        }

        // Each Record* reads the clock AT THE MOMENT IT STAMPS, not the decision instant: the wallet's own
        // rgb-lib work can outlast the cooldown, and `now` would then stamp an already-elapsed instant,
        // leaving the wallet immediately eligible again. RecordAttemptFailed(w.Id, DateTimeOffset.MinValue)
        // would do the same thing outright, defeating the backoff while every count still matched.
        foreach (var call in new[] { "RecordAttemptSucceeded", "RecordAttemptFailed", "RecordNoActionNeeded" })
        {
            var stamp = Single(replenish, call).ArgumentList.Arguments[1].Expression;
            var access = Assert.IsType<MemberAccessExpressionSyntax>(stamp);
            Assert.True(RoslynPins.NamesBclMember(access, "DateTimeOffset", "UtcNow"),
                $"{call} must stamp DateTimeOffset.UtcNow read at the call, found '{stamp}'");
        }

        // …and the decision instant must still be captured INSIDE the loop. Hoisting it above the foreach
        // restores the cross-wallet drift: later wallets judged against the sweep's start, counting invoices
        // that expired while it ran.
        var nowDecl = replenish.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "now");
        Assert.True(nowDecl.Ancestors().OfType<ForEachStatementSyntax>().Any(),
            "'now' must be declared inside the per-wallet foreach, not once per sweep");

        // Prune: before the loop, over the very collection the loop iterates. A filtered prune set with an
        // unfiltered work set evicts a wallet immediately before processing it, so NextEligibleAt returns
        // null and its cooldown and backoff are gone — the false-ACCEPT direction.
        var prune = Single(replenish, "Prune");
        var loop = RoslynPins.BodyOf(replenish).DescendantNodes().OfType<ForEachStatementSyntax>().Single();
        Assert.True(prune.SpanStart < loop.SpanStart, "Prune must run before the per-wallet loop");
        var pruneArgument = Assert.IsType<IdentifierNameSyntax>(prune.ArgumentList.Arguments[0].Expression);
        var iterated = Assert.IsType<IdentifierNameSyntax>(loop.Expression);
        Assert.True(pruneArgument.Identifier.ValueText == iterated.Identifier.ValueText,
            $"Prune's argument ('{pruneArgument.Identifier.ValueText}') must be the collection the loop "
            + $"iterates ('{iterated.Identifier.ValueText}')");
        AssertProducedBy(replenish, iterated.Identifier.ValueText, "ToListAsync");

        // No mutation of any pinned value, in any form the harness's own helper cannot see.
        var body = RoslynPins.BodyOf(replenish);
        // Object-initializer assignments (`new Foo { Bar = x }`) are excluded: they populate a fresh object
        // and cannot replace a pinned local, so counting them would fail a correct refactor — the kind of
        // false failure that teaches maintainers to delete pins.
        var mutations = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Parent is not InitializerExpressionSyntax)
            .ToList();
        Assert.True(mutations.Count == 0,
            "ReplenishUtxosAsync must contain no assignment outside an object initializer — every value it "
            + "needs is introduced by a declarator, and an assignment is how a pinned input gets quietly "
            + $"replaced. Found: {string.Join("; ", mutations.Select(m => m.ToString()))}");
        Assert.True(body.DescendantNodes().Count(n =>
            n is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
                or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression }) == 0,
            "ReplenishUtxosAsync must contain no ++/-- — activePendingInvoices++ raises demand on every wallet");

        // No entity snapshot to regress to: only the fresh read may be RGBWallet-typed.
        var model = plugin.Model(tree);
        var walletLocals = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => model.GetDeclaredSymbol(v) as ILocalSymbol)
            .Where(s => s != null && MentionsWallet(s!.Type))
            .Select(s => s!.Name)
            .ToList();
        Assert.Equal(new[] { "w" }, walletLocals);

        // The tracker's construction: FromSeconds, or base and ceiling swapped, collapses the backoff.
        var construction = body.Parent!.Ancestors().OfType<ClassDeclarationSyntax>().First()
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(o => (o.Type as IdentifierNameSyntax)?.Identifier.ValueText == "ReplenishCooldownTracker")
            .ToList();
        Assert.True(construction.Count == 1, $"expected one ReplenishCooldownTracker construction, found {construction.Count}");
        AssertMinutesOf(plugin, tree, construction[0], "baseCooldown", "AutoUtxoCooldownMinutes");
        AssertMinutesOf(plugin, tree, construction[0], "maxBackoff", "AutoUtxoMaxBackoffMinutes");

        // Rebinding the fields is a compile error rather than something a scan must catch.
        foreach (var field in new[] { "_cfg", "_cooldowns" })
            AssertReadonlyField(plugin.Tree(ListenerFile), ListenerType, field);
    }

    static bool MentionsWallet(ITypeSymbol type) =>
        type.Name == "RGBWallet"
        || (type is IArrayTypeSymbol array && MentionsWallet(array.ElementType))
        || (type is INamedTypeSymbol named && named.TypeArguments.Any(MentionsWallet));

    /// <summary>
    /// The member a Where/Sum selector reads. A body that ANDs further conditions onto it — narrowing the
    /// set, e.g. `x => x.IsActive &amp;&amp; x.Network == n` — is accepted, because narrowing can only reduce what
    /// the sweep acts on. `||` is not: widening is how an unpinned wallet re-enters the set. A raw
    /// Assert.IsType here used to fail a correct narrowing with no message at all.
    /// </summary>
    static string SelectorMember(PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax invocation)
    {
        var lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
        var candidates = Conjuncts(lambda.Body).OfType<MemberAccessExpressionSyntax>().ToList();
        Assert.True(candidates.Count > 0,
            $"selector '{lambda.Body}' must read a member of '{lambda.Parameter.Identifier.ValueText}', "
            + "optionally ANDed with further narrowing conditions; '||' is not accepted because widening "
            + "lets an unpinned item back into the set");
        return RoslynPins.BoundSymbol(plugin, tree, candidates[0]).Name;
    }

    /// <summary>Splits an `&amp;&amp;` chain into its operands; any other expression is a single operand.</summary>
    static IEnumerable<ExpressionSyntax> Conjuncts(CSharpSyntaxNode body) =>
        body is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression } and_
            ? Conjuncts(and_.Left).Concat(Conjuncts(and_.Right))
            : body is ExpressionSyntax e ? [e] : [];

    /// <summary>
    /// Pins a selector's whole path from the lambda's own parameter — `u.Outer.Leaf` — not just the leaf.
    /// `colorable.Sum(u => walletIds.Count)` reads a member named `Count` too, and would inflate demand on
    /// every wallet while a leaf-only assertion stayed green.
    /// </summary>
    static void AssertSelectorPath(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax invocation, string outer, string leaf)
    {
        var lambda = Assert.IsType<SimpleLambdaExpressionSyntax>(invocation.ArgumentList.Arguments[0].Expression);
        // Same contract as SelectorMember: an AND-narrowed body is property-preserving and must pass. Round 9
        // relaxed SelectorMember and left this helper doing a raw cast on the SAME lambda, so a correct
        // narrowing still died here with a message-free type error.
        var leafAccess = Conjuncts(lambda.Body).OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        Assert.True(leafAccess != null,
            $"the selector '{lambda.Body}' must read a member path from "
            + $"'{lambda.Parameter.Identifier.ValueText}', optionally ANDed with narrowing conditions");
        Assert.True(RoslynPins.BoundSymbol(plugin, tree, leafAccess!).Name == leaf,
            $"the selector's leaf must be '{leaf}', found '{lambda.Body}'");
        var outerAccess = Assert.IsType<MemberAccessExpressionSyntax>(leafAccess!.Expression);
        Assert.True(RoslynPins.BoundSymbol(plugin, tree, outerAccess).Name == outer,
            $"the selector must read '{outer}' before '{leaf}', found '{lambda.Body}'");
        var root = Assert.IsType<IdentifierNameSyntax>(outerAccess.Expression).Identifier.ValueText;
        Assert.True(root == lambda.Parameter.Identifier.ValueText,
            $"the selector must start from the lambda parameter '{lambda.Parameter.Identifier.ValueText}', "
            + $"found '{root}'");
    }

    static void AssertMinutesOf(PluginCompilation plugin, SyntaxTree tree,
        ObjectCreationExpressionSyntax creation, string parameter, string knob)
    {
        var argument = creation.ArgumentList!.Arguments
            .FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter);
        Assert.True(argument != null, $"the tracker must be constructed with a named '{parameter}' argument");
        var call = Assert.IsType<InvocationExpressionSyntax>(argument!.Expression);
        Assert.Equal("FromMinutes", NameOf(call));
        var access = Assert.IsType<MemberAccessExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        Assert.Equal(knob, RoslynPins.BoundSymbol(plugin, tree, access).Name);
        // The receiver, for the same reason AssertArgumentBindsTo pins one: `new RGBConfiguration().Knob`
        // binds to the identical property symbol while ignoring everything the operator configured. Building
        // the tracker that way would silently run at the 30/160 defaults, so an operator holding unattended
        // signing to once a day would get it every 30 minutes instead.
        var receiver = Assert.IsType<IdentifierNameSyntax>(access.Expression).Identifier.ValueText;
        Assert.True(receiver == "_cfg",
            $"the tracker's '{parameter}' must read {knob} from the injected '_cfg', found '{access.Expression}'");
    }

    static void AssertReadonlyField(SyntaxTree tree, string typeName, string fieldName)
    {
        var field = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.ValueText == typeName)
            .SelectMany(t => t.Members.OfType<FieldDeclarationSyntax>())
            .Single(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == fieldName));
        Assert.True(field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword),
            $"'{fieldName}' must be readonly so rebinding it is a compile error");
    }

    // P-C9. The two outcome-recording calls must sit in the blocks their names imply. PC5 counts one of each
    // and PC7 pins their arguments, so SWAPPING them — success stamps a failure, the catch stamps a success —
    // leaves every pin and the whole suite green while making a failing creation reset to the base cooldown and
    // a succeeding one back off. That is the retry storm the backoff exists to stop, so it is the permissive
    // direction, and no ablation covered it.
    [Fact]
    public void PC9_OutcomeRecordingSitsInTheBranchItsNameClaims()
    {
        var plugin = PluginCompilation.Shared;
        var replenish = ReplenishMethod(plugin);

        var creation = Single(replenish, "CreateColorableUtxosAutomaticallyAsync");
        var creationTry = creation.Ancestors().OfType<TryStatementSyntax>().First();

        var succeeded = Single(replenish, "RecordAttemptSucceeded");
        Assert.Same(creationTry.Block, succeeded.Ancestors().OfType<BlockSyntax>().First());
        Assert.Empty(succeeded.Ancestors().OfType<CatchClauseSyntax>());
        // Position, not just block. Moving the success stamp ABOVE the creation compiles and satisfies every
        // count, argument and block assertion, but each failing creation would then Settle first — clearing
        // _failures — and the catch would re-increment from 1, pinning the backoff at the base forever.
        Assert.True(succeeded.SpanStart > creation.SpanStart,
            "RecordAttemptSucceeded must follow CreateColorableUtxosAsync, not precede it");

        var failed = Single(replenish, "RecordAttemptFailed");
        var catchClause = failed.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        Assert.True(catchClause != null && catchClause.Parent == creationTry,
            "RecordAttemptFailed must sit in the catch guarding CreateColorableUtxosAsync");

        // …and the no-action stamp must NOT be inside that try/catch, or a skipped wallet would be recorded
        // as an attempt.
        var noAction = Single(replenish, "RecordNoActionNeeded");
        Assert.DoesNotContain(creationTry, noAction.Ancestors());
    }

    // P-C10. Nothing pinned the branch that gates the signing call. Flipping `!=` to `==` compiles, keeps
    // RecordNoActionNeeded present so PC5's counts hold, and routes every refused outcome — SkipCapReached
    // included — into CreateColorableUtxosAsync. Bounded by count: 0, but it is a signing call on a decision
    // that was refused, and it is the most consequential branch in the method.
    [Fact]
    public void PC10_CreationIsGatedOnTheCreateOutcome()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod(plugin);

        var gates = replenish.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition is BinaryExpressionSyntax b
                        && b.Left is MemberAccessExpressionSyntax l
                        && l.Name.Identifier.ValueText == "Outcome")
            .ToList();
        Assert.True(gates.Count == 1, $"expected exactly one decision.Outcome gate, found {gates.Count}");

        var condition = (BinaryExpressionSyntax)gates[0].Condition;
        Assert.True(condition.IsKind(SyntaxKind.NotEqualsExpression),
            $"the creation gate must skip when the outcome is NOT Create, found '{condition}'");

        var left = (MemberAccessExpressionSyntax)condition.Left;
        Assert.Equal("decision", Assert.IsType<IdentifierNameSyntax>(left.Expression).Identifier.ValueText);

        var right = Assert.IsType<MemberAccessExpressionSyntax>(condition.Right);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, right);
        Assert.True(symbol.Name == "Create" && symbol.ContainingType?.Name == "ReplenishOutcome",
            $"the gate must compare against ReplenishOutcome.Create, found "
            + $"{symbol.ContainingType?.Name}.{symbol.Name}");

        // The refused branch must leave the iteration UNCONDITIONALLY. Merely containing a `continue`
        // somewhere is not enough: `if (decision.Outcome != SkipCapReached) continue;` inside the block
        // satisfies containment while letting SkipCapReached fall through to the creation call.
        AssertLeavesTheIterationUnconditionally(gates[0], "the refused-demand gate");

        // …and it must stand BEFORE the creation. Shape alone is not enough: hoisting the creation's
        // try/catch above this gate keeps every pin green while routing SkipCapReached and
        // SkipEnoughFreeSlots straight into CreateColorableUtxosAsync.
        Assert.True(gates[0].SpanStart < Single(replenish, "CreateColorableUtxosAutomaticallyAsync").SpanStart,
            "the refused-demand gate must precede CreateColorableUtxosAsync");
    }

    // P-C11. The eligibility gate, the twin of P-C10 and the one that carries the cooldown, the quarantine
    // and the wrong-wallet refusal. Nothing pinned it: narrowing the condition to
    // `if (skip.HasValue && skip.Value != ReplenishOutcome.SkipCooldown)` compiles without a warning and
    // leaves all ten other pins green — it is not a BinaryExpression with an `Outcome` left operand, so
    // P-C10's filter still finds exactly one gate — while making SkipCooldown stop nothing, so an eligible
    // wallet signs every sweep instead of every cooldown. The same edit disposes of SkipQuarantined and
    // SkipWalletNotConfigured. EvaluateReplenishEligibility is unit-tested for its RETURN VALUE; only this
    // pin tests that the caller acts on it.
    [Fact]
    public void PC11_EligibilityRefusalIsUnconditionalAndLeavesTheIteration()
    {
        var replenish = ReplenishMethod(PluginCompilation.Shared);

        // `skip.HasValue`, `skip is not null` and `skip is { }` are the same test; all three are accepted, so
        // a maintainer tidying the null-check does not hit a false failure and reach for the one repair that
        // would reinstate the hole — widening this filter to "any `if` mentioning skip", which would let a
        // narrowed condition back in.
        bool TestsSkipForPresence(ExpressionSyntax c) => c switch
        {
            MemberAccessExpressionSyntax m =>
                m.Name.Identifier.ValueText == "HasValue" && Names(m.Expression, "skip"),
            IsPatternExpressionSyntax p => Names(p.Expression, "skip") && p.Pattern is
                UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax } }
                or RecursivePatternSyntax { PropertyPatternClause.Subpatterns.Count: 0 },
            _ => false
        };

        var gates = replenish.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => TestsSkipForPresence(i.Condition))
            .ToList();
        Assert.True(gates.Count == 1,
            $"expected exactly one gate testing `skip` for presence and nothing else, found {gates.Count} — "
            + "a condition that also excludes particular outcomes is the round-10 false-ACCEPT");

        AssertLeavesTheIterationUnconditionally(gates[0], "the eligibility gate");
    }

    static bool Names(ExpressionSyntax e, string identifier) =>
        e is IdentifierNameSyntax id && id.Identifier.ValueText == identifier;

    /// <summary>
    /// The gate's block must `continue` as a direct statement, not inside a nested condition. A `continue`
    /// buried under another `if` lets the outcomes that condition excludes fall through to the signing call.
    /// </summary>
    static void AssertLeavesTheIterationUnconditionally(IfStatementSyntax gate, string what)
    {
        var block = Assert.IsType<BlockSyntax>(gate.Statement);
        Assert.True(block.Statements.OfType<ContinueStatementSyntax>().Any(),
            $"{what} must end in an unconditional `continue`; found "
            + $"[{string.Join("; ", block.Statements.Select(st => st.Kind().ToString()))}]");
    }

    /// <summary>
    /// The predicate's answer is HEEDED: <paramref name="operation"/> happens when and only when
    /// <paramref name="predicate"/> says so. Three clauses, each written after a compiling counter-example
    /// defeated a weaker version — and clause C added last, after the others were found to constrain only
    /// what comes at or after the gate.
    ///
    /// A — the condition IS the invocation (positive gate) or IS its negation (negated early exit), and
    /// nothing else. It once admitted a top-level `&amp;&amp;`/`||` chain containing the invocation, on the
    /// rationale that narrowing is safe; narrowing is NOT safe at ingress, because a suppressed enqueue on
    /// a wallet the periodic sweep skips is a lost payment. Defeated otherwise:
    /// `ShouldEnqueue(inv); TryWrite(...)` (result discarded), `var ok = ...; if (other)` (gate reads
    /// something else), `if (P || x)` (widened), `if (P &amp;&amp; x)` (narrowed — suppresses every enqueue).
    ///
    /// B — the operation occurs exactly once TEXTUALLY, the guard dominates it, AND the write is reached
    /// whenever the guard admits: it is a direct statement of the admitted branch with nothing exiting
    /// before it. Dominance alone proves the predicate NECESSARY, never SUFFICIENT — a nested condition or
    /// an early `return` inside the accepted branch narrows the filter again while every other clause
    /// passes. "Exactly once" counts occurrences, not executions — a write inside a `foreach` is one
    /// occurrence and is correct.
    ///
    /// C — nothing unpinned precedes the gate ANYWHERE on the path from the method body down to it, nothing
    /// but blocks and loops encloses it, and any enclosing loop both iterates the collection itself
    /// (`pending.Take(1)` drops invoices before the predicate sees them) and runs to completion (a `break`
    /// after the pre-warm queues only the first). Counting the preamble in the gate's own block alone leaves
    /// the statement that produces the collection unguarded. A narrowing early exit ABOVE it, or a `using`/
    /// `try`/`lock` wrapped AROUND it, suppresses the write while clauses A and B stay green. Only INERT
    /// local declarations are exempt — an identifier, or a single member access on one; an arbitrary chain
    /// still evaluates property getters that can throw. The caller declares how many branching statements
    /// may legitimately precede the gate, and pins their shape itself.
    /// </summary>
    static InvocationExpressionSyntax AssertGuardedBy(PluginCompilation plugin, MethodDeclarationSyntax method,
        string predicate, string operation, string queueField, int allowedPreamble)
    {
        var tree = plugin.Tree(ListenerFile);
        var body = RoslynPins.BodyOf(method);
        var where = $"{method.Identifier.ValueText}";

        // Rule 2(b): a local function or local named like the predicate would satisfy every node
        // assertion below while the real predicate never runs.
        // The FIELD is shadowable too, and that was measured GREEN before this line named it: `var _queue =
        // somethingElse;` is an identifier-initialised declaration, so `Inert` exempts it from the preamble
        // count AND from AssertShape, and every remaining clause then verifies a write to a dead object.
        // Enqueueing onto a local channel nobody drains is residual R6 with a green suite. Shadowing the
        // predicate name was already pinned; shadowing the receiver was not.
        RoslynPins.AssertNoLocalShadow(method, predicate, queueField);

        // Not `Single(...)`: its "found 0" message reads like the pin is broken when the real cause is a
        // spelling this rule deliberately excludes, and a maintainer who believes a pin is broken deletes it.
        var calls = InvocationsNamed(body, predicate);
        Assert.True(calls.Count == 1,
            $"{where}: '{predicate}' must be INVOKED exactly once, inline in the condition of the `if` that "
            + $"guards '{operation}'; found {calls.Count}. Spellings that move the decision elsewhere are not "
            + $"admitted — a method group (`pending.Where({predicate})`), or a local bound first and gated on "
            + "later — because admitting them means additionally pinning that the value the gate consumes is "
            + "the one the predicate produced. The inline form costs nothing; use it.");
        var call = calls[0];

        // KIND, not just name and containing type. A delegate-typed local reports both identically to the
        // static method it shadows, so the earlier two-field check accepted `out var ShouldEnqueue` in the
        // fetch's argument list — a local returning false, every enqueue suppressed, whole suite green.
        RoslynPins.AssertBindsToMemberOf(plugin, tree, call.Expression,
            SymbolKind.Method, ListenerFullType, predicate, where);

        // The RECEIVER too: `InvocationsNamed` matches the member name alone, so a `TryWrite` on any other
        // channel would satisfy every operand and dominance clause while enqueueing onto nothing.
        var writes = InvocationsNamed(body, operation)
            .Where(i => i.Expression is MemberAccessExpressionSyntax m
                        && RootOf(m.Expression) == queueField)
            .ToList();
        // A local function is a DECLARATION, so its body can sit late in the block while its invocation runs
        // early: `Enqueue(); if (!ShouldEnqueue(inv)) continue; void Enqueue() => TryWrite(inv.Id);` satisfies
        // every structural check below — one textual write, in a later sibling of the guard's own block — while
        // the write executes BEFORE the predicate is consulted. Source order only tracks execution order for
        // straight-line statements, so the write must not be hidden inside one.
        // ...and the same is true of every OTHER deferred body. `Action enqueue = () => TryWrite(id);` inside
        // the guarded branch satisfies containment and the write count while enqueueing NOTHING, because the
        // lambda is never invoked. Local functions, lambdas and anonymous methods all decouple where a write
        // is WRITTEN from whether it RUNS, which is the only thing these clauses can see.
        // WHITELIST, not blacklist — the same lesson `Inert` above already records, relearned here the hard
        // way. Enumerating the node kinds that decouple "written" from "run" does not terminate: the list
        // began as local functions and lambdas, and reviewers then produced a ternary, a switch expression,
        // `&&`/`||` short-circuit and `??`, each of which keeps the enclosing ExpressionStatement so every
        // other clause — count, operand, dominance, nothing-between, both shape pins — stays green while the
        // write runs for some invoices only. That is residual R6 on the very write this finding exists to
        // guarantee. One positive rule closes the whole family: the write's parent must BE the statement.
        //
        // SCOPE, stated exactly, because an earlier version of this comment over-claimed. This clause rejects
        // every EXPRESSION-shaped decoupling — expression-bodied lambdas and local functions, ternary, switch
        // expression, `&&`/`||`/`??` — measured on all of them. It does NOT reject a BLOCK-bodied deferred
        // body: in `Action a = () => { TryWrite(id); };` the call's parent is the inner ExpressionStatement,
        // so it passes here and is caught below, by dominance and same-block, because that statement is not a
        // member of the guarded branch. Both halves have ablation rows. Saying "this subsumes lambdas" would
        // have been the same closing over-generalisation that has cost this finding five review rounds.
        var notPlain = writes.Where(w => PlainStatementOf(w) is null).ToList();
        Assert.True(notPlain.Count == 0,
            $"{where}: '{operation}' must be a plain expression statement — its parent must be the statement "
            + "itself, not a lambda, local function, ternary, switch expression, or `&&`/`||`/`??` operand. "
            + "Every one of those decouples where the write is WRITTEN from whether it RUNS, which is the "
            + "only thing a syntactic pin can see; found it under a "
            + $"{(notPlain.Count > 0 ? notPlain[0].Parent?.Kind().ToString() : "-")}.");

        Assert.True(writes.Count == 1,
            $"{where}: '{operation}' must occur exactly once; found {writes.Count}. A second occurrence "
            + "is unguarded no matter what the condition says.");
        var write = writes[0];

        // The receiver was matched by SPELLING alone above. Bind it: `out var _queue` of channel type in the
        // fetch's argument list gives the write a receiver with the right name that nothing ever drains.
        RoslynPins.AssertBindsToMemberOf(plugin, tree,
            LeftmostIdentifierOf(((MemberAccessExpressionSyntax)write.Expression).Expression),
            SymbolKind.Field, ListenerFullType, queueField, where);

        var gates = method.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Condition.Contains(call))
            .ToList();
        Assert.True(gates.Count == 1,
            $"{where}: '{predicate}' must be invoked in the condition of exactly one `if`, found "
            + $"{gates.Count} — binding it to a local and gating on something else is the shape this rejects");
        var gate = gates[0];

        // Clause A used to admit a top-level `&&` chain (positive) or `||` chain (negated exit), on the
        // inherited rationale that narrowing "can only reduce what the sweep acts on". **That rationale is
        // dead here.** `if (ShouldEnqueue(e.Invoice) && e.Invoice.Id.Length == 0) TryWrite(...)` keeps the
        // conjunct, suppresses every enqueue, and stays green — and on a wallet the sweep skips (M4a) a
        // suppressed enqueue is a lost payment, which is exactly what killed residuals R6 and R8. At ingress
        // the predicate must be the SOLE gate, so the condition is the invocation or its negation, nothing
        // else. This also makes Conjuncts/Disjuncts unnecessary here.
        var condition = Unwrap(gate.Condition);
        var positive = condition == call;
        var negated = condition is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } not
                      && Unwrap(not.Operand) == call;

        Assert.True(positive ^ negated,
            $"{where}: the guard's condition must be exactly '{predicate}(…)' or exactly '!{predicate}(…)', "
            + $"with nothing ANDed or ORed onto it; found '{gate.Condition}'. Narrowing is not safe at ingress: "
            + "a suppressed enqueue on a wallet the periodic sweep skips is a lost payment, not a delay.");

        // The call is pinned; the OPERAND was not. `if (ShouldEnqueue(new InvoiceEntity())) TryWrite(e.Invoice.Id);`
        // satisfies every clause above while enqueueing nothing, because the predicate is asked about the wrong
        // object. Comparing only the leftmost identifier is not enough either — `TryWrite(e.Invoice.StoreId)`
        // roots on the same `e`. The write must be exactly the subject's `.Id`.
        // Compared with SyntaxFactory.AreEquivalent, not rendered text: `ToString()` keeps interior trivia,
        // so a line-broken or commented member access would false-red on correct code.
        var subjectExpr = Unwrap(call.ArgumentList.Arguments[0].Expression);
        var writtenExpr = Unwrap(write.ArgumentList.Arguments[0].Expression);
        var expected = SyntaxFactory.ParseExpression(subjectExpr.ToString() + ".Id");
        var subject = subjectExpr.ToString();
        var written = writtenExpr.ToString();
        Assert.True(SyntaxFactory.AreEquivalent(writtenExpr, expected),
            $"{where}: '{operation}' must write the id of the very object '{predicate}' was asked about — "
            + $"expected '{subject}.Id', found '{written}'. A different object, a different member, or a "
            + "freshly constructed entity satisfies every other clause while the guard decides nothing.");

        // NECESSARY is not SUFFICIENT. Every clause so far proves the predicate must say yes before the
        // write can run; none proves the write runs WHENEVER it says yes. Nest a second condition around the
        // write — `if (ShouldEnqueue(e.Invoice)) { if (e.Invoice.GetPaymentPrompt(pmi)?.Details != null)
        // TryWrite(...); }` — and every clause passes while the lazily-activated invoices this predicate
        // exists to admit are dropped again. That is R6, restored, with a green suite. So the write must be a
        // DIRECT statement of the branch the guard admits, never nested in a further conditional.
        // Sufficiency is also defeated from ABOVE. `if (e.Invoice.Id.Length == 0) return …;` placed before
        // the gate suppresses enqueues with every other clause green. The gate must therefore be the first
        // statement of its block, except for a preamble the caller pins explicitly — `OnInvoice` legitimately
        // has one, the `e.Name != InvoiceEvent.Created` early return, and P-H1 pins that separately.
        // Only INERT local declarations are exempt. `var invoice = e.Invoice;` cannot suppress the write and
        // spec §8 mandates it stays green — but `var x = Validate(e.Invoice);` can throw, so exempting every
        // declaration reopens the hole this clause exists to close. Inert = the initializer invokes nothing,
        // constructs nothing and awaits nothing.
        // WHITELIST, not blacklist. Listing the node kinds that can throw is open-ended — a cast, an element
        // access, a property getter, a `throw` expression and `??` all can, and the previous blacklist missed
        // every one. An initializer is inert only if it is an identifier or a plain member-access chain rooted
        // at one: `e.Invoice`, `inv`. Anything else counts toward the preamble.
        // Depth-ONE only. An arbitrary chain still evaluates property getters — `e.Invoice.Metadata.ItemCode`
        // can throw before the gate — so recursing through member accesses was still too permissive. The one
        // shape §8's refactor B needs is `var invoice = e.Invoice;`: an identifier, or a single member access
        // on an identifier. Anything deeper counts toward the preamble.
        static bool InertInitializer(ExpressionSyntax? e) => e switch
        {
            IdentifierNameSyntax => true,
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax } => true,
            _ => false
        };

        static bool Inert(StatementSyntax st) =>
            st is LocalDeclarationStatementSyntax local
            && local.Declaration.Variables.All(v => InertInitializer(v.Initializer?.Value));

        // Counting only the gate's own block is not enough: wrap the gate in `using (Logger.Scope()) { … }`
        // and the preamble budget is spent inside that block while the `using`'s own initialiser — and any
        // statement beside it in the outer block — runs first, unseen. Everything enclosing the gate up to the
        // method body must therefore be a plain block or a loop.
        var enclosingChain = gate.Ancestors().TakeWhile(n => n != method).ToList();
        var illegalEnclosure = enclosingChain.FirstOrDefault(n =>
            n is not (BlockSyntax or ForEachStatementSyntax or ForStatementSyntax
                      or WhileStatementSyntax or DoStatementSyntax));
        Assert.True(illegalEnclosure == null,
            $"{where}: the guard may only be enclosed by blocks and loops up to the method body — found it "
            + $"inside a {illegalEnclosure?.Kind()}. A `using`, `try`, `lock`, `if` or `switch` around the "
            + "gate runs code before it that no preamble count can see.");

        // ...and, where the guard sits in a loop, the loop's SOURCE. `foreach (var inv in pending.Take(1))`
        // passes every clause below while silently abandoning every later pending invoice — an edit someone
        // makes while debugging and forgets. The source must be the bare collection identifier.
        // Every loop kind, not just `foreach`: the enclosure clause admits `for`/`while`/`do`, so pinning only
        // `foreach` left the same holes one rewrite away.
        foreach (var loop in enclosingChain.Where(n =>
                     n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax
                          or DoStatementSyntax))
        {
            if (loop is ForEachStatementSyntax each)
                Assert.True(each.Expression is IdentifierNameSyntax,
                    $"{where}: the loop feeding the guard must iterate the collection itself, not a projection "
                    + $"of it — found '{each.Expression}'. `.Take(…)`, `.Where(…)` or `.Skip(…)` there drops "
                    + "invoices before the predicate ever sees them.");

            // ...and it must run to completion. A `break;` after the pre-warm satisfies every other clause and
            // queues only the first invoice. Each transfer is resolved to its ACTUAL target: a `switch`'s own
            // `break`, a lambda's `return` and an inner loop's `break` do not leave this loop, and rejecting
            // them would be a false red on ordinary code.
            var loopBody = ((StatementSyntax)loop).ChildNodes().OfType<StatementSyntax>().Last();
            var truncations = loopBody.DescendantNodesAndSelf().Where(n =>
            {
                if (n is not (BreakStatementSyntax or ReturnStatementSyntax or GotoStatementSyntax)) return false;
                foreach (var up in n.Ancestors())
                {
                    if (up == loop) return true;
                    if (up is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) return false;
                    if (n is BreakStatementSyntax && up is SwitchStatementSyntax) return false;
                    if (n is BreakStatementSyntax && up is ForEachStatementSyntax or ForStatementSyntax
                            or WhileStatementSyntax or DoStatementSyntax) return false;
                }
                return false;
            }).ToList();
            Assert.True(truncations.Count == 0,
                $"{where}: the loop must run to completion — found {truncations.Count} statement(s) that leave "
                + "it early. Anything that exits queues a prefix of the pending set and abandons the rest, "
                + "which every other clause is blind to. (`continue` is the guard's own exit and is allowed; a "
                + "`switch`'s own `break`, a lambda's `return` and an inner loop's `break` are not counted.)");
        }

        // Counted along the WHOLE PATH from the method body down to the gate, not just the gate's own block.
        // With a gate inside a `foreach`, counting only the loop body leaves everything above the loop
        // unguarded — including the statement that produces the collection. Both of these passed every clause:
        //   var pending = (await _invoices.GetMonitoredInvoices(pmi, ct)).Take(1).ToArray();
        //   if (pending.Length > 200) return;
        // the same forgotten-debug truncation as `.Take(1)` in the loop header, one statement further up.
        var preamble = 0;
        SyntaxNode onPath = gate;
        foreach (var ancestor in enclosingChain)
        {
            if (ancestor is BlockSyntax b)
                preamble += b.Statements.TakeWhile(st => st != onPath).Count(st => !Inert(st));
            onPath = ancestor;
        }
        Assert.True(preamble == allowedPreamble,
            $"{where}: exactly {allowedPreamble} statement(s) may precede the guard; found {preamble}. A "
            + "narrowing early exit above the gate suppresses enqueues while every clause below still passes.");

        var writeStatement = write.FirstAncestorOrSelf<StatementSyntax>();
        Assert.True(writeStatement != null, $"{where}: '{operation}' is not inside a statement");

        if (positive)
        {
            var admitted = gate.Statement is BlockSyntax consequence
                ? consequence.Statements.Contains(writeStatement!)
                : gate.Statement == writeStatement;
            Assert.True(admitted,
                $"{where}: '{operation}' must be a direct statement of the guarded branch — found it wrapped "
                + $"in '{writeStatement!.Parent}'. Any statement around the write — a further condition, a "
                + "`try`, a `lock`, a `using` — makes the predicate necessary but not sufficient, which is how "
                + "a filter silently narrows again.");

            // NOTHING may stand between the guard's yes and the write — not an exit, not a call. An earlier
            // clause only rejected explicit `return`/`continue`/`break`/`throw`/`goto`, and a plain
            // `Validate(inv);` that throws for some prompt-bearing invoice walked straight through it. No
            // syntactic rule can tell whether a call throws, so the branch must simply contain the write and
            // nothing else.
            var branchStatements = gate.Statement is BlockSyntax consequenceBlock
                ? consequenceBlock.Statements.ToList()
                : [gate.Statement];
            Assert.True(branchStatements[0] == writeStatement,
                $"{where}: '{operation}' must be the FIRST statement of the guarded branch — found "
                + $"'{branchStatements[0]}' before it. Anything ahead of the write can suppress it (an early "
                + "exit, or simply a call that throws), which makes the predicate necessary but not "
                + "sufficient. Statements AFTER the write are fine: they cannot un-enqueue it.");

        }
        else
        {
            var inLoop = gate.Ancestors().TakeWhile(n => n != method)
                .Any(n => n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax
                               or DoStatementSyntax);
            Assert.True(LeavesTheBlockUnconditionally(gate.Statement, inLoop),
                $"{where}: the negated guard must leave unconditionally — "
                + (inLoop ? "`continue`, not `return`: inside a loop a `return` abandons every remaining "
                          + "item rather than skipping one"
                          : "`continue` or `return`")
                + $"; found '{gate.Statement}'");
            // Dominance must be STRUCTURAL, not "some enclosing block happens to contain both". Asking
            // only for the nearest enclosing BlockSyntax lets the guard sit on a branch the write does not
            // depend on: a `switch` section is not a BlockSyntax, and neither is an unbraced nested `if`,
            // so in both cases the search walks past the branch to the method's own block, finds the write
            // there, sees it later in source order, and passes — while every other path reaches the write
            // without ever consulting the predicate. Requiring the guard to be a DIRECT statement of a
            // block, and the write to live in that same statement list after it, is what makes "the write
            // follows the guard" true on every path rather than merely in source order.
            // A counted clause, not a precondition. An earlier draft called it unreachable and stopped
            // ablating it; that was wrong, and relabelling an unproven guard is worse than leaving it
            // unproven. An UNBRACED loop body makes `gate.Parent` a `ForEachStatementSyntax` — an enclosure
            // the clause above explicitly allows — with the write intact, so it is reachable and has a row.
            var block = gate.Parent as BlockSyntax;
            Assert.True(block != null,
                $"{where}: the early-exit guard must be a direct statement of a block; found it inside a "
                + $"{gate.Parent?.Kind()}. A guard nested in a branch — a switch section, an unbraced "
                + $"`if` — skips nothing on the paths that branch does not cover.");

            // Same on this polarity: the write's own statement must be a direct member of the block, not
            // something nested inside a later conditional in it.
            var sibling = block!.Statements.FirstOrDefault(st => st == writeStatement);
            Assert.True(sibling != null,
                $"{where}: '{operation}' must be a DIRECT statement of the same block as the guard — found it "
                + "wrapped in another statement. Any wrapper — a further condition, a `try`, a `lock`, a "
                + "`using` — makes the predicate necessary but not sufficient and silently narrows the filter.");

            // ...and nothing may stand between the guard and the write here either, for the same reason.
            var between = block.Statements
                .SkipWhile(st => st != gate).Skip(1).TakeWhile(st => st != writeStatement).ToList();
            Assert.True(between.Count == 0,
                $"{where}: nothing may stand between the guard and '{operation}' — found {between.Count} "
                + "statement(s). An early exit, or a call that throws, suppresses the write while every other "
                + "clause still passes.");

        }

        return write;
    }

    // `return` is accepted only OUTSIDE a loop. Inside `EnqueuePendingInvoices`' `foreach`, an early
    // `return` abandons every remaining invoice rather than skipping one — a far larger false-REJECT than
    // the guard is for, and it satisfied every other clause. The file's own
    // AssertLeavesTheIterationUnconditionally already required `continue` for this reason.
    static bool LeavesTheBlockUnconditionally(StatementSyntax statement, bool inLoop)
    {
        bool Exits(StatementSyntax s) => inLoop
            ? s is ContinueStatementSyntax
            : s is ContinueStatementSyntax or ReturnStatementSyntax;
        return statement switch
        {
            BlockSyntax block => block.Statements.Any(Exits),
            _ => Exits(statement)
        };
    }

    /// <summary>
    /// The WHOLE SHAPE of a method, as a sequence of statement kinds with inert local declarations removed.
    ///
    /// Three separate loop-integrity defects reached the same conclusion by different routes — a projected
    /// loop source, a `break` in the body, a truncating fetch above the loop — and each was closed by adding
    /// a clause for that POSITION. Positions are unbounded; the shape is not. If the method is exactly the
    /// statements it is supposed to be, there is nowhere for a fourth position to hide.
    ///
    /// Inert declarations are skipped so §8's refactor B (`var invoice = e.Invoice;`) stays green.
    /// </summary>
    static void AssertShape(MethodDeclarationSyntax method, params SyntaxKind[] expected)
    {
        static bool Inert(StatementSyntax st) =>
            st is LocalDeclarationStatementSyntax local
            && local.Declaration.Variables.All(v => v.Initializer?.Value is IdentifierNameSyntax
                or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax });

        var actual = ((BlockSyntax)method.Body!).Statements.Where(st => !Inert(st)).ToList();
        Assert.True(actual.Count == expected.Length
                    && actual.Select(st => st.Kind()).SequenceEqual(expected),
            $"{method.Identifier.ValueText}: the method's shape is pinned — expected "
            + $"[{string.Join(", ", expected)}], found [{string.Join(", ", actual.Select(st => st.Kind()))}]. "
            + "An extra statement anywhere on the path to the enqueue can suppress it, and the clause-level "
            + "guards see only the position they were written for.");
    }

    /// <summary>
    /// The first statement that can actually do something — inert local declarations skipped, exactly as
    /// <see cref="AssertShape"/> and AssertGuardedBy's preamble count skip them.
    ///
    /// The two caller-pinned preamble clauses used to read `Statements[0]` raw, so hoisting the very shape
    /// the inert exemption exists for (`var invoice = e.Invoice;` above the event filter) reddened them
    /// while every other clause accepted it. A guard that exempts a shape in one place and rejects it in
    /// another is not pinning a property; it is pinning where you happened to put it.
    /// </summary>
    static StatementSyntax FirstMeaningful(MethodDeclarationSyntax method)
    {
        static bool Inert(StatementSyntax st) =>
            st is LocalDeclarationStatementSyntax local
            && local.Declaration.Variables.All(v => v.Initializer?.Value is IdentifierNameSyntax
                or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax });

        return ((BlockSyntax)method.Body!).Statements.First(st => !Inert(st));
    }

    /// <summary>The same, for the body of the single loop a method is allowed to contain.</summary>
    static void AssertLoopShape(MethodDeclarationSyntax method, params SyntaxKind[] expected)
    {
        var loops = method.DescendantNodes().OfType<ForEachStatementSyntax>().ToList();
        Assert.True(loops.Count == 1, $"{method.Identifier.ValueText}: expected exactly one `foreach`, "
            + $"found {loops.Count}");
        var body = ((BlockSyntax)loops[0].Statement).Statements.ToList();
        Assert.True(body.Select(st => st.Kind()).SequenceEqual(expected),
            $"{method.Identifier.ValueText}: the loop body's shape is pinned — expected "
            + $"[{string.Join(", ", expected)}], found [{string.Join(", ", body.Select(st => st.Kind()))}].");
    }

    /// <summary>
    /// The statement a call runs as, if it runs unconditionally as one: either the call IS the statement,
    /// or it is the whole right-hand side of a discard assignment that is the statement.
    ///
    /// The discard form is admitted because this codebase writes it (`TransportEndpointValidator`,
    /// `RGBController`), so `_ = _queue.Writer.TryWrite(inv.Id);` is an ordinary spelling here, semantically
    /// identical, and rejecting it would be the kind of false red that gets a pin deleted. It is admitted
    /// STRUCTURALLY, not by name: the call must be the assignment's entire RHS, so any conditional wrapping
    /// (`_ = cond ? TryWrite(id) : false;`, a switch expression, `&amp;&amp;`) puts a node in between and is still
    /// rejected — which the ablation rows for those shapes demonstrate.
    /// </summary>
    static ExpressionStatementSyntax? PlainStatementOf(SyntaxNode call) => call.Parent switch
    {
        ExpressionStatementSyntax statement => statement,
        AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment
            when assignment.Right == call
                 && assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "_" }
            => assignment.Parent as ExpressionStatementSyntax,
        _ => null
    };

    /// <summary>The leftmost identifier a member-access chain roots on: `_queue.Writer` → "_queue".</summary>
    // `this.` roots there too. Without that arm `this._queue.Writer.TryWrite(inv.Id)` — what several IDE
    // "qualify member access" settings produce automatically — was filtered out of the write list entirely
    // and reported as "found 0", blaming a missing write rather than the qualifier. A maintainer who reads
    // that message concludes the pin is broken, and a pin believed broken gets deleted. Since the binding
    // assertion now checks the symbol, admitting the spelling costs nothing.
    static string RootOf(ExpressionSyntax expression) => Unwrap(expression) switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n }
            => n.Identifier.ValueText,
        MemberAccessExpressionSyntax m => RootOf(m.Expression),
        _ => string.Empty
    };

    /// <summary>The same node rather than its spelling, so the receiver can be bound and not merely read.</summary>
    static IdentifierNameSyntax LeftmostIdentifierOf(ExpressionSyntax expression) => Unwrap(expression) switch
    {
        IdentifierNameSyntax id => id,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n } => n,
        MemberAccessExpressionSyntax m => LeftmostIdentifierOf(m.Expression),
        var other => throw new InvalidOperationException(
            $"'{other}' roots on no identifier; callers reach this only after RootOf matched one")
    };

    /// <summary>
    /// The mirror of <see cref="AssertNeverTouches"/>: a cache write that MUST be present. The startup
    /// pre-warm is a fund-safety property, not tidiness — without it the drain must hit the database, its
    /// catch swallows a failure, and the queue entry is a one-shot already consumed, so on a wallet whose
    /// sweep is broken by a failing durability flush the invoice is never processed. A "uniform cache
    /// provenance" refactor deleted it once; nothing else would stop that recurring with a green suite.
    ///
    /// Accepts `Set` and nothing else, and pins the key and the value as well as the call. `CreateEntry`
    /// caches nothing until Value is assigned and the entry disposed; `GetOrCreate`/`GetOrCreateAsync` cache
    /// whatever the factory returns, including null. Each of those, and a right-call/wrong-key or
    /// right-key/null-value write, was measured to keep an earlier version of this clause green while the
    /// pre-warm was effectively gone.
    /// </summary>
    // Accepted member list of ONE, on an effect argument rather than a spelling preference: `Set` is the
    // only member that unconditionally caches the value handed to it. `CreateEntry` caches nothing until
    // Value is assigned and the entry disposed; `GetOrCreate`/`GetOrCreateAsync` cache whatever the factory
    // returns, including null. Either would let this clause stay green while the pre-warm is gone.
    static void AssertCachesOn(PluginCompilation plugin, MethodDeclarationSyntax method, string receiver,
        string keyFragment, string enqueueSubject, InvocationExpressionSyntax enqueueWrite)
    {
        var tree = plugin.Tree(ListenerFile);

        // Same shadow hole as the queue receiver: `var _cache = someOtherCache;` is inert, so it slips past
        // the preamble count and AssertShape while every clause below verifies a pre-warm into an object the
        // drain never reads — R8 with a green suite.
        // `ComputeExpiry` too: a local function of that name would satisfy the expiry clause while returning
        // an already-elapsed instant. Without this it died on AssertShape instead, with a message about the
        // method's shape rather than the pre-warm — a misdiagnosis of the kind that gets pins deleted.
        // Kept for its message: it names the offender precisely for the two forms it does see. The binding
        // assertions below are what make these names safe against the local declaration forms no clause
        // enumerates, and against an inherited member of a same-simple-named type. Neither rule reaches the
        // callee's BODY — see the expiry clause, and R11 in the spec.
        RoslynPins.AssertNoLocalShadow(method, receiver, "ComputeExpiry");

        // `this.`-qualified here TOO. Round 9 fixed the queue write's receiver match and left this one
        // keyed on a bare identifier — the same half-applied fix the queue write suffered at round 3, and
        // the campaign's refactor E caught it: `this._cache.Set(...)` reported "found 0", blaming a deleted
        // pre-warm for a qualifier. Deliberately not `RootOf`: that would also admit `_cache.Inner.Set(...)`.
        var writes = RoslynPins.BodyOf(method).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax m
                        && m.Name.Identifier.ValueText == "Set"
                        && (m.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == receiver
                            || m.Expression is MemberAccessExpressionSyntax
                               { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax q }
                               && q.Identifier.ValueText == receiver))
            .ToList();
        Assert.True(writes.Count == 1,
            $"{method.Identifier.ValueText}: must call {receiver}.Set exactly once; found {writes.Count}. "
            + "Removing the startup pre-warm reinstates a permanent-false-REJECT path — see spec §5.2. "
            + "`CreateEntry`, `GetOrCreate`/`GetOrCreateAsync` and extraction into a helper are excluded "
            + "deliberately: the first caches nothing until Value is set and disposed, the second two cache "
            + "whatever the factory returns including null, and the third is invisible here. If you rewrite "
            + "the pre-warm, update this clause in the same commit.");

        // The receiver was matched by SPELLING alone above, exactly as the queue receiver was: bind it.
        RoslynPins.AssertBindsToMemberOf(plugin, tree,
            ((MemberAccessExpressionSyntax)writes[0].Expression).Expression,
            SymbolKind.Field, ListenerFullType, receiver, method.Identifier.ValueText);

        // ...and the SUBJECT and the PATH. Caching `pending[0]`, correctly keyed to itself, satisfies the key
        // and value clauses for every admitted `inv` while leaving almost every queued invoice cold — R8
        // again. The pre-warm must cache the same identifier the enqueue writes, in the same block as it.
        //
        // WRITTEN is not RUN — the same necessary/sufficient gap, one guard over. `Action prewarm = () =>
        // _cache.Set(key, inv, …);` keeps every clause below green while no invoice is ever pre-warmed, and
        // R8 returns. Codex's no-deferred-body fix was applied to the queue write and never propagated here.
        //
        // DEFERRED BODIES, AND EXPRESSION-SHAPED CONDITIONALS. The distinction is STATEMENT vs EXPRESSION,
        // and getting it wrong cost a round each way.
        //
        // This clause once rejected any enclosing `if`/`switch` STATEMENT too. That half was harmful: on the
        // positive polarity, which AssertGuardedBy deliberately supports, the enclosing `if` IS the guard, so
        // a stock IDE "invert if" reddened here with a message misdiagnosing it as a conditional pre-warm —
        // and a guard whose message sends a maintainer hunting the wrong defect is worse than one that stays
        // silent. It was also redundant, because a statement-shaped wrapper always reparents the `Set`, so the
        // same-block clause below catches it. Measured: the `if`-wrapped ablation row reddens there.
        //
        // But "redundant" holds ONLY for statement-shaped wrappers, and removing the expression kinds with
        // them was a real hole. A switch expression or a ternary keeps the enclosing ExpressionStatement, so
        // `_ = cond switch { true => _cache.Set(key, inv, …), _ => inv };` sits directly in the loop block with
        // the right parent, nothing between it and the enqueue, and the loop shape intact — every clause green
        // while the pre-warm runs for some invoices only, which is R8 again. An expression can never be the
        // guard, so rejecting these two costs nothing and reintroduces no false red.
        // Same WHITELIST as the queue write, and for the same reason — this clause went through a blacklist
        // of node kinds twice (round 1 removed the `if`/`switch` statement kinds, round 2 restored the two
        // expression kinds it had removed with them) before the enumeration was recognised as the defect
        // rather than its contents. `Parent is ExpressionStatementSyntax` covers lambdas, local functions,
        // ternaries, switch expressions and `&&`/`||`/`??` operands at once.
        //
        // Statement-shaped wrappers are deliberately NOT rejected here: `if (cond) { _cache.Set(…); }`
        // reparents the Set, so the same-block clause below catches it with an accurate message, whereas
        // rejecting `if` here misdiagnosed the positive-polarity guard — where the enclosing `if` IS the
        // guard — as a conditional pre-warm.
        var setStatement = PlainStatementOf(writes[0]);
        Assert.True(setStatement != null,
            $"{method.Identifier.ValueText}: the pre-warm must be a plain expression statement — its parent "
            + "must be the statement itself, not a lambda, local function, ternary, switch expression, or "
            + "`&&`/`||`/`??` operand. Written is not run: a deferred pre-warm may never be invoked, and an "
            + "expression-shaped conditional runs it for some invoices only — either way the drain hits the "
            + "database, and on a wallet the periodic sweep skips that is a lost payment (spec §5.2, residual "
            + $"R8); found it under a {writes[0].Parent?.Kind()}.");

        // The KEY and the VALUE, tied to each other. Pinning them separately is not enough:
        // `_cache.Set($"rgb:inv:{inv.StoreId}", inv, …)` has a well-formed key and the right value and still
        // leaves `rgb:inv:{inv.Id}` — the key CheckSingleInvoice actually reads — cold, so the drain misses,
        // hits the database, and R8 is back. The value must be an identifier, and the key must interpolate
        // exactly that identifier's `.Id`.
        var cached = writes[0].ArgumentList.Arguments[1].Expression;
        Assert.True(cached is IdentifierNameSyntax,
            $"{method.Identifier.ValueText}: the pre-warm must cache the invoice entity itself, as a plain "
            + $"identifier; found '{cached}'. Caching null, an id, or an expression leaves the drain's lookup "
            + "useless while every other clause stays green.");
        var subject = ((IdentifierNameSyntax)cached).Identifier.ValueText;
        Assert.True(subject == enqueueSubject,
            $"{method.Identifier.ValueText}: the pre-warm must cache the same invoice the enqueue writes — "
            + $"the queue writes '{enqueueSubject}.Id' and the cache stores '{subject}'. Caching a different "
            + "entity leaves every other queued invoice cold while every clause here stays green.");

        var enqueueStatement = enqueueWrite.FirstAncestorOrSelf<StatementSyntax>();
        Assert.True(setStatement!.Parent == enqueueStatement!.Parent,
            $"{method.Identifier.ValueText}: the pre-warm must sit in the same block as the enqueue, so it "
            + "runs on exactly the invoices that are queued — found them in different blocks.");

        // ...and nothing may stand between them. `if (inv.Type == BTCPayServer.Client.Models.InvoiceType.TopUp) continue;` after the
        // enqueue and before the `Set` leaves the one-shot queue entry un-pre-warmed, which is R8 for exactly
        // those invoices, while every clause above stays green.
        var enclosing = (BlockSyntax)setStatement.Parent!;
        var betweenWriteAndSet = enclosing.Statements
            .SkipWhile(st => st != enqueueStatement).Skip(1).TakeWhile(st => st != setStatement).ToList();
        Assert.True(betweenWriteAndSet.Count == 0,
            $"{method.Identifier.ValueText}: nothing may stand between the enqueue and the pre-warm — found "
            + $"{betweenWriteAndSet.Count} statement(s). An exit there queues the invoice and leaves it cold.");

        var key = writes[0].ArgumentList.Arguments[0].Expression;
        var interpolated = key as InterpolatedStringExpressionSyntax;
        var texts = interpolated?.Contents.OfType<InterpolatedStringTextSyntax>()
            .Select(t => t.TextToken.ValueText) ?? [];
        var holes = interpolated?.Contents.OfType<InterpolationSyntax>()
            .Select(i => Unwrap(i.Expression).ToString()).ToList() ?? [];
        // EQUALITY, not containment. `$"v2:rgb:inv:{inv.Id}"`, `$"rgb:inv:{inv.Id}-v2"` and
        // `$"rgb:inv:{inv.Id,10}"` all *contain* the fragment and interpolate the right expression, and all
        // three leave `CheckSingleInvoice`'s key cold — R8 again. The key must be exactly two contents: the
        // literal fragment, then the interpolation, with no alignment or format clause.
        var interpolation = interpolated?.Contents.OfType<InterpolationSyntax>().FirstOrDefault();
        Assert.True(interpolated != null
                    && interpolated.Contents.Count == 2
                    && texts.Count() == 1 && texts.First() == keyFragment
                    && holes.Count == 1 && holes[0] == subject + ".Id"
                    && interpolation is { AlignmentClause: null, FormatClause: null },
            $"{method.Identifier.ValueText}: the pre-warm must be keyed on an interpolated string containing "
            + $"'{keyFragment}' and interpolating exactly '{subject}.Id' — the key CheckSingleInvoice reads; "
            + $"found '{key}'. A key bound to a local first, or built from a shared constant or helper, is not "
            + "admitted: proving it produces the same key would require tracing it.");

        // ...and the EXPIRY. Right receiver, right key, right value — and an already-elapsed third argument
        // makes the entry evict before the drain ever reads it, so the drain misses on every invoice and R8
        // returns with every clause above green. Measured: `_cache.Set(key, inv, <any expiry>)` was accepted.
        // Pinned as `ComputeExpiry(<the same subject>)`, the one expression whose contract is "this invoice's
        // remaining lifetime, floored" — a literal, a local, or ComputeExpiry of a different invoice all fail.
        var expiry = writes[0].ArgumentList.Arguments.Count > 2
            ? Unwrap(writes[0].ArgumentList.Arguments[2].Expression) as InvocationExpressionSyntax
            : null;
        Assert.True(expiry != null
                    // BARE identifier, not `X.ComputeExpiry(...)`. Measured at round 7: the clause was
                    // name-keyed, so `CacheKeys.ComputeExpiry(inv)` — some other type's method returning an
                    // arbitrary instant — passed it. The claim that stood here at round 7 — that a bare
                    // identifier could only be the listener's own method, because a same-named local is
                    // rejected by the shadow clause above — was measured FALSE at round 8: an `out var
                    // ComputeExpiry` of delegate type is bare, is no local DECLARATION the shadow clause
                    // counts, and made the pre-warm expire an already-elapsed entry with the suite green.
                    // The binding assertion after this one is what closes that; the syntactic form stays
                    // because it is what rejects the qualified spelling. Neither reaches the callee's BODY:
                    // `ComputeExpiry` returning an elapsed instant passes every clause here and evicts every
                    // pre-warmed entry before the drain reads it. That is closed BEHAVIOURALLY, by the
                    // contract tests on `ComputeExpiry`, not by any source pin — a syntactic pin cannot see
                    // what a method returns.
                    && expiry.Expression is IdentifierNameSyntax
                    && NameOf(expiry) == "ComputeExpiry"
                    && expiry.ArgumentList.Arguments.Count == 1
                    && Unwrap(expiry.ArgumentList.Arguments[0].Expression) is IdentifierNameSyntax expirySubject
                    && expirySubject.Identifier.ValueText == subject,
            $"{method.Identifier.ValueText}: the pre-warm's expiry must be `ComputeExpiry({subject})` — the "
            + "invoice's own remaining lifetime. An already-elapsed constant, or the expiry of a different "
            + "invoice, evicts the entry before the drain reads it and reinstates R8. The "
            + "`MemoryCacheEntryOptions { AbsoluteExpiration = ComputeExpiry(inv) }` overload is a "
            + "deliberately EXCLUDED spelling, not an oversight: admitting it would mean also pinning that "
            + $"no other option on that object shortens the entry's life. Found "
            + $"'{(writes[0].ArgumentList.Arguments.Count > 2 ? writes[0].ArgumentList.Arguments[2].ToString() : "<no third argument>")}'.");

        RoslynPins.AssertBindsToMemberOf(plugin, tree, expiry!.Expression,
            SymbolKind.Method, ListenerFullType, "ComputeExpiry", method.Identifier.ValueText);
    }

    /// <summary>
    /// The identifier does not appear in the method at all. Stated as absence-of-identifier rather than
    /// absence-of-`Set`, because `_cache.CreateEntry(key)` with Value/AbsoluteExpiration set is the literal
    /// desugaring of `Set`, `GetOrCreate` caches too, and `var c = _cache; c.Set(...)` evades any
    /// receiver-keyed rule. A name-keyed absence pin is pinned to a spelling.
    /// </summary>
    static void AssertNeverTouches(MethodDeclarationSyntax method, string identifier)
    {
        var uses = RoslynPins.BodyOf(method).DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.ValueText == identifier)
            .ToList();
        Assert.True(uses.Count == 0,
            $"{method.Identifier.ValueText}: must not touch '{identifier}' ({uses.Count} use(s): "
            + $"{string.Join("; ", uses.Select(u => u.Parent?.ToString()))}). An enqueue path that caches "
            + "retains a full InvoiceEntity for at least five minutes per invoice, with no bound.");
    }

    // P-H1 (finding H2b). The subscription is server-wide: it fires for every invoice created anywhere on
    // the instance. Nothing pinned that OnInvoice heeds the predicate, and nothing pinned that it keeps its
    // hands off the shared cache — the two together are the whole finding.
    [Fact]
    public void PH1_OnInvoiceEnqueuesOnlyRgbInvoicesAndNeverCaches()
    {
        var plugin = PluginCompilation.Shared;
        var onInvoice = RoslynPins.Method(plugin.Tree(ListenerFile), ListenerType, "OnInvoice");

        AssertGuardedBy(plugin, onInvoice, "ShouldEnqueue", "TryWrite", "_queue", allowedPreamble: 1);

        // The one allowed preamble statement, pinned: OnInvoice's event filter. Anything else there would be
        // an unpinned narrowing above the gate.
        // Structural, not string containment: `if (e.Name != InvoiceEvent.Created || e.Invoice.Type ==
        // InvoiceType.TopUp) return …;` *contains* the right text and narrows ingress on top of it. The
        // condition must be exactly the one comparison, with no further operands.
        var preambleStatement = FirstMeaningful(onInvoice);
        Assert.True(preambleStatement is IfStatementSyntax { Statement: ReturnStatementSyntax } head
                    && head.Condition is BinaryExpressionSyntax
                        { RawKind: (int)SyntaxKind.NotEqualsExpression } cmp
                    && cmp.Left is MemberAccessExpressionSyntax name
                    && name.Name.Identifier.ValueText == "Name"
                    && cmp.Right is MemberAccessExpressionSyntax created
                    && created.Name.Identifier.ValueText == "Created"
                    && RoslynPins.BoundSymbol(plugin, plugin.Tree(ListenerFile), created)
                        .ContainingType?.Name == "InvoiceEvent",
            $"OnInvoice: the only statement allowed before the guard is the bare "
            + $"`if (e.Name != InvoiceEvent.Created) return …;` early return, with nothing ANDed or ORed onto "
            + $"it; found '{preambleStatement}'. Extra operands there narrow ingress above the guard — and "
            + "BOTH sides are pinned: `e.Invoice.Id != InvoiceEvent.Created` also compiles and would suppress "
            + "every enqueue while matching the operator and the right-hand side.");
        AssertNeverTouches(onInvoice, "_cache");

        // Whole-shape pin: the event filter, the gate, the return. Nothing else.
        AssertShape(onInvoice,
            SyntaxKind.IfStatement, SyntaxKind.IfStatement, SyntaxKind.ReturnStatement);
    }

    // P-H2. Startup subscribes before requesting the durable sweep. The previous fetch-then-subscribe order
    // had a permanent event gap, and materializing every monitored invoice recreated an unbounded list before
    // the bounded channel was even consulted.
    [Fact]
    public void PH2_StartSubscribesBeforeDurableRecoveryAndDoesNotMaterializeABacklog()
    {
        var plugin = PluginCompilation.Shared;
        var start = RoslynPins.Method(plugin.Tree(ListenerFile), ListenerType, "StartAsync");
        var model = plugin.Model(plugin.Tree(ListenerFile));
        var subscription = RoslynPins.BodyOf(start).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "Subscribe",
                ContainingType.Name: "EventAggregator"
            } symbol && symbol.TypeArguments.SingleOrDefault()?.Name == "InvoiceEvent");
        Assert.Equal("OnInvoice", subscription.ArgumentList.Arguments.Single().Expression.ToString());
        var text = start.Body!.ToString();
        var subscribe = text.IndexOf("Subscribe<InvoiceEvent>", StringComparison.Ordinal);
        var recovery = text.IndexOf("RequestRecovery", StringComparison.Ordinal);

        Assert.True(subscribe >= 0 && recovery > subscribe,
            "StartAsync must subscribe before requesting the initial durable recovery sweep");
        Assert.DoesNotContain("SubscribeAsync<InvoiceEvent>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetMonitoredInvoices", text);
        Assert.DoesNotContain("EnqueuePendingInvoices", text);
    }

    [Fact]
    public void PD4_RefreshPrecedesReplenishInsideTheSamePollLoopBody()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var poll = RoslynPins.Method(tree, ListenerType, PollLoop);

        RoslynPins.AssertNoLocalShadow(poll, Refresh, Replenish);

        var refresh = Single(poll, Refresh);
        var replenish = Single(poll, Replenish);

        RoslynPins.AssertBindsToMemberOf(plugin, tree, refresh.Expression, SymbolKind.Method,
            ListenerFullType, Refresh, PollLoop);
        RoslynPins.AssertBindsToMemberOf(plugin, tree, replenish.Expression, SymbolKind.Method,
            ListenerFullType, Replenish, PollLoop);

        foreach (var (name, invocation) in new[] { (Refresh, refresh), (Replenish, replenish) })
            Assert.True(invocation.Ancestors().OfType<AwaitExpressionSyntax>()
                    .Any(a => a.Expression.Span.Contains(invocation.Span)),
                $"{PollLoop}: '{name}' must be awaited; a fire-and-forget call establishes no order at all");

        Assert.True(refresh.SpanStart < replenish.SpanStart,
            $"{PollLoop}: 'await {Refresh}' must precede 'await {Replenish}'. {Refresh} is the call that "
            + $"reconciles each wallet's rgb-lib state, and {Replenish} decides how many colorable UTXOs to "
            + "sign for from that state, so the refresh comes first. Do not swap them.");

        var loop = RoslynPins.BodyOf(poll).DescendantNodes().OfType<WhileStatementSyntax>()
            .Single(w => w.Parent == RoslynPins.BodyOf(poll));
        foreach (var (name, invocation) in new[] { (Refresh, refresh), (Replenish, replenish) })
            Assert.True(loop.Statement.Span.Contains(invocation.Span),
                $"{PollLoop}: '{name}' must stay inside the SAME while-loop body as the other. Moving either "
                + "out of the loop body — including into a helper invoked after the loop — leaves both calls "
                + $"present and textually ordered while destroying the per-iteration order this pin asserts.");
    }
}
