using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbDryRunSourcePinTests
{
    const string RgbLibFile = "Services/RgbLibService.cs";
    const string RgbLibType = "RgbLibService";
    const string HelperFile = "RgbRestoreHelper/RgbNativeSend.cs";
    const string HelperType = "RgbNativeSend";
    const string HelperMember = "InvokeNative";
    const string CreateUtxosBeginNative = "rgblib_create_utxos_begin";
    const string CreateUtxosBeginMember = "CreateUtxosBeginAsync";
    const string SendBeginNative = "rgblib_send_begin";
    const string SendBeginMember = "SendBeginAsync";
    const string DryRun = "dry_run";
    const string SkipSync = "skip_sync";
    const string UpTo = "up_to";
    const string ManualBoundMember = "EnsureStandingColorableRoom";
    const string WalletServiceType = "RGBWalletService";
    const string ConfigurationType = "RGBConfiguration";
    const string ConfigurationReceiver = "_cfg";
    const string ManualCeilingMember = "MaxManualColorableUtxos";
    const string AutoCapMember = "MaxAutoColorableUtxos";

    const string Asymmetry =
        "The dry_run asymmetry is deliberate and is NOT an inconsistency to tidy up. "
        + "create_utxos_begin MUST pass dry_run = true: its reservation covers 100% of the wallet's "
        + "vanilla balance, rgb-lib exposes no release the plugin can call, and the only release it does "
        + "expose broadcasts the transaction the signing gate declined — so an abandoned begin is a "
        + "permanent fund lockout. send_begin MUST pass dry_run = false: its durable batch_transfer row "
        + "at status Initiated(5) is the only hook by which an abandoned asset send can be found and "
        + "failed, so suppressing it removes recovery. Making the two 'consistent' breaks one of them.";

    static int ParameterIndex(string nativeMethod, string parameterName, int expectedArity)
    {
        var nativeMethods = typeof(RgbLibWallet).Assembly.GetType("RgbLib.NativeMethods");
        Assert.True(nativeMethods != null,
            "RgbLib.NativeMethods is absent from the shipped RgbLib assembly; the args arrays below are "
            + "matched against that signature by reflection and cannot be checked without it");
        var method = nativeMethods!.GetMethod(nativeMethod);
        Assert.True(method != null,
            $"RgbLib.NativeMethods.{nativeMethod} is absent from the shipped RgbLib assembly");
        var parameters = method!.GetParameters();
        Assert.True(parameters.Length == expectedArity,
            $"{nativeMethod} takes {parameters.Length} parameters, not {expectedArity}: "
            + string.Join(", ", parameters.Select((p, i) => $"[{i}] {p.ParameterType.Name} {p.Name}"))
            + ". Re-derive the args array against the real ABI before adjusting this pin.");
        var index = Array.FindIndex(parameters, p => p.Name == parameterName);
        Assert.True(index >= 0,
            $"{nativeMethod} has no parameter named '{parameterName}'; its parameters are "
            + string.Join(", ", parameters.Select((p, i) => $"[{i}] {p.Name}")));
        return index;
    }

    static SyntaxTree HelperTree()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, HelperFile);
        Assert.True(File.Exists(path), $"{HelperFile} is missing; it holds the live send_begin call site");
        return CSharpSyntaxTree.ParseText(File.ReadAllText(path),
            new CSharpParseOptions(LanguageVersion.Latest), path);
    }

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static string ReceiverOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Expression.ToString(),
        MemberBindingExpressionSyntax => invocation.Ancestors()
            .OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression.ToString()
            ?? string.Empty,
        _ => string.Empty
    };

    static int StringLiteralOccurrences(SyntaxNode root, string value) =>
        root.DescendantTokens()
            .Count(t => t.IsKind(SyntaxKind.StringLiteralToken) && (string?)t.Value == value);

    static string EnclosingMember(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? "<none>";

    static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression switch
    {
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang
            => Unwrap(bang.Operand),
        ParenthesizedExpressionSyntax paren => Unwrap(paren.Expression),
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } coalesce
            => Unwrap(coalesce.Left),
        _ => expression
    };

    static IReadOnlyList<InvocationExpressionSyntax> GetMethodCalls(SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "GetMethod" && i.ArgumentList.Arguments.Count >= 1)
            .ToList();

    static Dictionary<(string Member, string Local), string> LiteralBoundMethodInfoLocalsByDeclaringMember(
        SyntaxNode root) =>
        root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && Unwrap(v.Initializer.Value) is InvocationExpressionSyntax call
                        && NameOf(call) == "GetMethod"
                        && call.ArgumentList.Arguments.Count >= 1
                        && Unwrap(call.ArgumentList.Arguments[0].Expression)
                            is LiteralExpressionSyntax { Token.Value: string })
            .GroupBy(v => (EnclosingMember(v), v.Identifier.ValueText))
            .Select(g => (g.Key, Natives: g
                .Select(v => (string)((LiteralExpressionSyntax)Unwrap(
                        ((InvocationExpressionSyntax)Unwrap(v.Initializer!.Value))
                            .ArgumentList.Arguments[0].Expression)).Token.Value!)
                .Distinct(StringComparer.Ordinal)
                .ToList()))
            .Where(x => x.Natives.Count == 1)
            .ToDictionary(x => x.Key, x => x.Natives[0]);

    static bool NativeMethodDeclaresNoDryRunParameter(string nativeMethod)
    {
        var nativeMethods = typeof(RgbLibWallet).Assembly.GetType("RgbLib.NativeMethods");
        Assert.True(nativeMethods != null,
            "RgbLib.NativeMethods is absent from the shipped RgbLib assembly; a literal-bound native "
            + "call site cannot be cleared of dry_run without it");
        var method = nativeMethods!.GetMethod(nativeMethod);
        Assert.True(method != null,
            $"RgbLib.NativeMethods.{nativeMethod} is absent from the shipped RgbLib assembly, so the "
            + "literal-bound call site naming it resolves to null at run time");
        return method!.GetParameters().All(p => p.Name != DryRun);
    }

    static string SoleDynamicallyDispatchedMethodInfoLocal(SyntaxNode root, string where)
    {
        var literalBoundBeginNatives = GetMethodCalls(root)
            .Select(i => i.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Select(l => (string?)l.Token.Value)
            .Where(name => name != null && name.EndsWith("_begin", StringComparison.Ordinal))
            .ToList();
        Assert.True(literalBoundBeginNatives.Count == 0,
            $"{where}: a native '*_begin' method is bound by literal name here "
            + $"({string.Join(", ", literalBoundBeginNatives)}); that is a call site whose dry_run this pin "
            + "does not inspect. " + Asymmetry);

        var locals = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && Unwrap(v.Initializer.Value) is InvocationExpressionSyntax call
                        && NameOf(call) == "GetMethod"
                        && call.ArgumentList.Arguments.Count >= 1
                        && call.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax)
            .Select(v => v.Identifier.ValueText)
            .Distinct()
            .ToList();
        Assert.True(locals.Count == 1,
            $"{where}: expected exactly one local holding a native MethodInfo resolved from a non-literal "
            + $"method name, found {locals.Count} ({string.Join(", ", locals)}). A second one is a call "
            + "site whose dry_run this pin does not inspect. " + Asymmetry);
        return locals[0];
    }

    static string NativeMethodBackingField(SyntaxTree rgbLibTree, string nativeMethod)
    {
        var bindings = rgbLibTree.GetRoot().DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => Unwrap(a.Right) is InvocationExpressionSyntax call
                        && NameOf(call) == "GetMethod"
                        && call.ArgumentList.Arguments.Count == 1
                        && call.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
                        && (string?)literal.Token.Value == nativeMethod)
            .ToList();
        Assert.True(bindings.Count == 1,
            $"expected exactly one `GetMethod(\"{nativeMethod}\")` binding in {RgbLibFile}, found "
            + $"{bindings.Count}. A second binding is a second call site this pin does not inspect.");
        var target = Assert.IsType<IdentifierNameSyntax>(bindings[0].Left);
        return target.Identifier.ValueText;
    }

    const int MaxIndirectionHops = 8;

    static ExpressionSyntax ResolveConstant(ExpressionSyntax expression, SyntaxNode anchor, string where)
    {
        var member = anchor.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        var type = anchor.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var current = Unwrap(expression);
        for (var hop = 0; current is IdentifierNameSyntax identifier; hop++)
        {
            Assert.True(hop < MaxIndirectionHops,
                $"{where}: resolving '{expression}' followed more than {MaxIndirectionHops} levels of "
                + $"indirection and never reached a constant. Keep the value reachable in at most "
                + $"{MaxIndirectionHops} hops from the call site. " + Asymmetry);
            var name = identifier.Identifier.ValueText;
            var declarators = (member?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .Where(v => v.Identifier.ValueText == name
                                && !v.Ancestors().OfType<FieldDeclarationSyntax>().Any())
                    .ToList() ?? [])
                .Concat(type?.Members.OfType<FieldDeclarationSyntax>()
                    .SelectMany(f => f.Declaration.Variables)
                    .Where(v => v.Identifier.ValueText == name) ?? [])
                .ToList();
            Assert.True(declarators.Count == 1,
                $"{where}: '{name}' resolves to {declarators.Count} declarator(s) among the locals of "
                + $"{EnclosingMember(anchor)} and the fields of "
                + $"{(type?.Identifier.ValueText ?? "<no type>")}, so this pin cannot tell which value "
                + $"reaches the call. Give it a uniquely named local or field. " + Asymmetry);
            var initializer = declarators[0].Initializer?.Value;
            Assert.True(initializer != null,
                $"{where}: '{name}' is declared without an initializer, so this pin cannot resolve the "
                + $"value that reaches the call. Initialize it where it is declared. " + Asymmetry);
            current = Unwrap(initializer!);
        }
        return current;
    }

    static IReadOnlyList<ExpressionSyntax> ArgsPassedTo(InvocationExpressionSyntax invoke, string where)
    {
        Assert.True(invoke.ArgumentList.Arguments.Count == 2,
            $"{where}: expected `Invoke(null, args)`, found `{invoke}`");
        return ElementsFor(invoke.ArgumentList.Arguments[1].Expression, invoke, where);
    }

    static IReadOnlyList<ExpressionSyntax> ElementsFor(ExpressionSyntax expression, SyntaxNode anchor,
        string where)
    {
        var resolved = ResolveConstant(expression, anchor, where);
        var elements = ElementsOf(resolved);
        Assert.True(elements != null,
            $"{where}: the args array is built by an expression this pin cannot read structurally "
            + $"('{resolved}'). Keep it an array or collection literal, or a local or field initialized "
            + "with one, so the dry_run element stays visible to the pin. " + Asymmetry);
        return elements!;
    }

    static IReadOnlyList<ExpressionSyntax>? ElementsOf(ExpressionSyntax expression) => expression switch
    {
        ArrayCreationExpressionSyntax { Initializer: not null } array => array.Initializer!.Expressions.ToList(),
        ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions.ToList(),
        CollectionExpressionSyntax collection => collection.Elements
            .OfType<ExpressionElementSyntax>().Select(e => e.Expression).ToList(),
        _ => null
    };

    static ExpressionSyntax BooleanConstantAt(IReadOnlyList<ExpressionSyntax> elements, SyntaxNode anchor,
        int index, string nativeMethod, string parameterName, string where)
    {
        Assert.True(index < elements.Count,
            $"{where}: the args array for {nativeMethod} has {elements.Count} element(s); the reflected "
            + $"'{parameterName}' sits at index {index}");
        var element = elements[index];
        var resolved = ResolveConstant(element, anchor, where);
        Assert.True(resolved.IsKind(SyntaxKind.TrueLiteralExpression)
                    || resolved.IsKind(SyntaxKind.FalseLiteralExpression),
            $"{where}: {nativeMethod}'s '{parameterName}' argument is at index {index} (resolved from the "
            + $"reflected parameter NAME, not counted) and is written as '{element}', which resolves to "
            + $"'{resolved}' — not a boolean constant this pin can evaluate. Pass a boolean literal, or a "
            + "local or field initialized with one, so the value stays checkable. " + Asymmetry);
        return resolved;
    }

    static void AssertBooleanConstantAt(IReadOnlyList<ExpressionSyntax> elements, SyntaxNode anchor,
        int index, bool expected, string nativeMethod, string parameterName, string where)
    {
        var resolved = BooleanConstantAt(elements, anchor, index, nativeMethod, parameterName, where);
        var element = elements[index];
        var kind = expected ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression;
        Assert.True(resolved.IsKind(kind),
            $"{where}: {nativeMethod}'s '{parameterName}' argument is at index {index} (resolved from the "
            + $"reflected parameter NAME, not counted) and must evaluate to "
            + $"{expected.ToString().ToLowerInvariant()}; it is written as '{element}' and evaluates to "
            + $"'{resolved}'. " + Asymmetry);
    }

    [Fact]
    public void CreateUtxosBegin_PassesDryRunTrueAtItsOnlyLiveCallSite()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var dryRunIndex = ParameterIndex(CreateUtxosBeginNative, DryRun, 8);
        var skipSyncIndex = ParameterIndex(CreateUtxosBeginNative, SkipSync, 8);
        var field = NativeMethodBackingField(tree, CreateUtxosBeginNative);

        var invokes = plugin.AllTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == "Invoke" && ReceiverOf(i) == field)
            .ToList();
        Assert.True(invokes.Count == 1,
            $"expected exactly one `{field}.Invoke` call site, found {invokes.Count}. A new call site to "
            + $"{CreateUtxosBeginNative} must decide dry_run explicitly and be listed here. " + Asymmetry);
        Assert.True(EnclosingMember(invokes[0]) == CreateUtxosBeginMember,
            $"the {CreateUtxosBeginNative} call site must stay inside {RgbLibType}.{CreateUtxosBeginMember}; "
            + $"it is now in '{EnclosingMember(invokes[0])}'. " + Asymmetry);

        var where = $"{RgbLibFile} {CreateUtxosBeginMember}";
        var elements = ArgsPassedTo(invokes[0], $"{RgbLibType}.{CreateUtxosBeginMember}");
        AssertBooleanConstantAt(elements, invokes[0], dryRunIndex, true, CreateUtxosBeginNative, DryRun,
            where);
        AssertBooleanConstantAt(elements, invokes[0], skipSyncIndex, false, CreateUtxosBeginNative, SkipSync,
            where);
        Assert.True(dryRunIndex != skipSyncIndex,
            "skip_sync and dry_run must resolve to different indices");

        var upToIndex = ParameterIndex(CreateUtxosBeginNative, UpTo, 8);
        var upTo = BooleanConstantAt(elements, invokes[0], upToIndex, CreateUtxosBeginNative, UpTo, where);
        Assert.True(upTo.IsKind(SyntaxKind.FalseLiteralExpression),
            $"{where}: {CreateUtxosBeginNative}'s '{UpTo}' argument is at "
            + $"index {upToIndex} (resolved from the reflected parameter NAME) and must evaluate to "
            + $"false; it is written as '{elements[upToIndex]}' and evaluates to '{upTo}'. This is a "
            + "SEPARATE property from dry_run, and an EARLIER REVISION OF THIS PIN REQUIRED true ON THE "
            + "FALSE GROUND that up_to makes the requested count a TOTAL standing number of colorable "
            + "UTXOs. It does not. Measured on rgb-lib at the pinned commit (src/wallet/online.rs:270-277) "
            + "and live on regtest: up_to subtracts only the ALLOCATABLE UTXOs returned by "
            + "get_available_allocations (src/wallet/offline.rs:118-145), which excludes every colorable "
            + "UTXO that is full, pending_witness, carries a pending_blinded, or holds an outgoing "
            + "initiated/waiting_counterparty allocation. Live measurement: with 3 standing colorable UTXOs "
            + "of which 1 was allocatable, up_to = true with num = 4 created 4 - 1 = 3 new outputs where a "
            + "total-standing reading predicts 1. So up_to = true would create (needed + excluded) outputs "
            + "per attempt and stand at up to twice the cap, over the two figures the consent screen states "
            + "and over the fee ceiling and MaxOutputCount the signing policy derives from the same number. "
            + "false makes the request exactly the number of NEW outputs rgb-lib builds, and the pool is "
            + "capped instead by TWO bounds outside this call, one per caller: the automatic path by "
            + "EvaluateReplenishDemand clamping to the headroom below MaxAutoColorableUtxos, and the "
            + "manual operator path by RGBWalletService.EnsureStandingColorableRoom refusing above its "
            + "OWN ceiling, MaxManualColorableUtxos. Those two ceilings MUST stay separate knobs: "
            + "MaxAutoColorableUtxos = 0 is a documented kill switch for unattended signing, so reading "
            + "it on the manual path made every wallet holding at least one colorable UTXO unable to "
            + "provision by ANY route while the shipped notice told the operator to press this very "
            + "button. up_to = true was previously the ONLY thing bounding the manual path, so flipping "
            + "it here without that refusal left every press growing the pool without limit — and "
            + "SendBtcInternalAsync filters !Colorable, so the plugin's BTC send cannot pay that back "
            + "out. Do not flip this to true to restore that bound. The manual bound's existence is "
            + $"COUNTED, not asserted, by {nameof(TheManualCreateUtxosPathCarriesItsOwnBoundOnTheColorablePool)}.");
    }

    [Fact]
    public void TheManualCreateUtxosPathCarriesItsOwnBoundOnTheColorablePool()
    {
        var plugin = PluginCompilation.Shared;
        var invocations = plugin.AllTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == ManualBoundMember)
            .ToList();
        Assert.True(invocations.Count == 1,
            $"expected exactly one invocation of {ManualBoundMember} in the plugin, found "
            + $"{invocations.Count} ({string.Join(", ", invocations.Select(EnclosingMember))}). This pin "
            + $"exists so the up_to reason in {nameof(CreateUtxosBegin_PassesDryRunTrueAtItsOnlyLiveCallSite)} "
            + "is a counted claim rather than a sentence that survives every green run: with up_to = false "
            + "the manual operator path has no other bound, and rgb-lib builds `count` NEW colorable "
            + "outputs on every press. Zero invocations means the pool grows without limit through an "
            + "operator-facing button, into UTXOs SendBtcInternalAsync refuses to spend.");
        var symbol = plugin.Model(invocations[0].SyntaxTree)
            .GetSymbolInfo(invocations[0]).Symbol as IMethodSymbol;
        Assert.True(symbol != null && symbol.Name == ManualBoundMember
                    && symbol.ContainingType?.Name == WalletServiceType,
            $"the single {ManualBoundMember} invocation must bind to {WalletServiceType}."
            + $"{ManualBoundMember}; it binds to "
            + $"'{symbol?.ContainingType?.Name ?? "<unresolved>"}.{symbol?.Name ?? "<unresolved>"}'. A "
            + "same-named method on another type would satisfy a name-only count while leaving the "
            + "colorable pool unbounded.");

        var model = plugin.Model(invocations[0].SyntaxTree);
        var configurationReads = invocations[0].ArgumentList.Arguments
            .Select(a => a.Expression as MemberAccessExpressionSyntax)
            .Where(a => a != null)
            .Select(a => (Access: a!, Symbol: model.GetSymbolInfo(a!).Symbol))
            .Where(x => x.Symbol?.ContainingType?.Name == ConfigurationType)
            .ToList();
        Assert.True(configurationReads.Count == 1,
            $"the single {ManualBoundMember} call passes {configurationReads.Count} arguments read from "
            + $"{ConfigurationType}; exactly one is required. The ceiling MUST come from configuration "
            + "rather than a literal or a constant, because an operator whose pool has reached it has to "
            + "be able to RAISE it: a ceiling nobody can raise turns a full pool into a permanent "
            + $"refusal. Arguments as written: {invocations[0].ArgumentList}.");
        var (access, ceiling) = configurationReads[0];
        Assert.True(ceiling!.Name == ManualCeilingMember,
            $"the ceiling argument of {ManualBoundMember} binds to {ConfigurationType}.{ceiling!.Name}; "
            + $"it must bind to {ConfigurationType}.{ManualCeilingMember}. Binding it to "
            + $"{ConfigurationType}.{AutoCapMember} instead is the regression this pin exists for: that "
            + "knob's documented meaning is a bound on AUTOMATIC creation and 0 is its shipped kill "
            + "switch, so reading it here refused the manual button for every wallet already holding one "
            + "colorable UTXO — no path left to provision, while RgbReplenishmentNotice tells the "
            + "operator to press exactly that button, and while raising the cap would re-arm the "
            + "unattended signing the operator set 0 to stop.");
        var receiver = (access.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
        Assert.True(receiver == ConfigurationReceiver,
            $"the ceiling argument of {ManualBoundMember} reads {ManualCeilingMember} from "
            + $"'{receiver ?? access.Expression.ToString()}'; it must read it from "
            + $"'{ConfigurationReceiver}'. A freshly constructed {ConfigurationType} binds to the same "
            + "symbol while silently ignoring the operator's configured and raisable ceiling.");
    }

    [Fact]
    public void SendBegin_PassesDryRunFalseAtEveryCallSite()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var dryRunIndex = ParameterIndex(SendBeginNative, DryRun, 8);
        var field = NativeMethodBackingField(tree, SendBeginNative);

        var invokes = plugin.AllTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == "Invoke" && ReceiverOf(i) == field)
            .ToList();
        Assert.True(invokes.Count == 1,
            $"expected exactly one `{field}.Invoke` call site inside the plugin, found {invokes.Count}. "
            + $"A new call site to {SendBeginNative} must decide dry_run explicitly. " + Asymmetry);
        Assert.True(EnclosingMember(invokes[0]) == SendBeginMember,
            $"the plugin-side {SendBeginNative} call site must stay inside {RgbLibType}.{SendBeginMember}; "
            + $"it is now in '{EnclosingMember(invokes[0])}'. " + Asymmetry);
        AssertBooleanConstantAt(ArgsPassedTo(invokes[0], $"{RgbLibType}.{SendBeginMember}"),
            invokes[0], dryRunIndex, false, SendBeginNative, DryRun, $"{RgbLibFile} {SendBeginMember}");

        var helper = HelperTree();
        var helperRoot = helper.GetRoot();
        var conditionals = helperRoot.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Where(c => StringLiteralOccurrences(c.Condition, SendBeginNative) == 1)
            .ToList();
        Assert.True(conditionals.Count == 1,
            $"expected exactly one args-array selection keyed on \"{SendBeginNative}\" in {HelperFile}, "
            + $"found {conditionals.Count}. That ternary is the LIVE send_begin call site — the "
            + $"{RgbLibType}.{SendBeginMember} wrapper has no production caller. " + Asymmetry);
        Assert.True(EnclosingMember(conditionals[0]) == HelperMember,
            $"the live {SendBeginNative} args array must stay inside {HelperType}.{HelperMember}; it is now "
            + $"in '{EnclosingMember(conditionals[0])}'. " + Asymmetry);
        AssertBooleanConstantAt(
            ElementsFor(conditionals[0].WhenTrue, conditionals[0], $"{HelperType}.{HelperMember}"),
            conditionals[0], dryRunIndex, false, SendBeginNative, DryRun,
            $"{HelperFile} {HelperMember}");

        var methodInfoLocal = SoleDynamicallyDispatchedMethodInfoLocal(helperRoot, HelperFile);
        var literalBoundNativeByLocal = LiteralBoundMethodInfoLocalsByDeclaringMember(helperRoot);
        var invokeCalls = helperRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "Invoke")
            .ToList();
        var literalBoundInvokes = invokeCalls
            .Where(i => literalBoundNativeByLocal.ContainsKey((EnclosingMember(i), ReceiverOf(i))))
            .ToList();

        var dynamicInvokes = invokeCalls.Except(literalBoundInvokes).ToList();
        Assert.True(dynamicInvokes.Count == 1,
            $"expected exactly one `.Invoke` in {HelperFile} whose receiver is not a local bound from a "
            + $"literal native name, found {dynamicInvokes.Count} "
            + $"({string.Join(", ", dynamicInvokes.Select(i => $"{EnclosingMember(i)}:{ReceiverOf(i)}"))}). "
            + "Anything this pin cannot prove is literal-bound counts as a native dispatch whose dry_run "
            + "it does not inspect — a MethodInfo reached through a parameter, a field or an alias is "
            + "exactly how an eight-argument send_begin call can be routed past the args array this pin "
            + "reads. If a receiver here is not a MethodInfo at all, give it a name this pin is not "
            + $"asked to judge rather than relaxing the clause. " + Asymmetry);
        Assert.True(EnclosingMember(dynamicInvokes[0]) == HelperMember,
            $"the live native invocation must stay inside {HelperType}.{HelperMember}; it is now in "
            + $"'{EnclosingMember(dynamicInvokes[0])}'. " + Asymmetry);
        Assert.True(ReceiverOf(dynamicInvokes[0]) == methodInfoLocal,
            $"the live native invocation dispatches through '{ReceiverOf(dynamicInvokes[0])}'; it must "
            + $"dispatch through '{methodInfoLocal}', the one local this pin proved is bound from a "
            + $"non-literal native name. " + Asymmetry);

        foreach (var call in literalBoundInvokes)
        {
            var native = literalBoundNativeByLocal[(EnclosingMember(call), ReceiverOf(call))];
            Assert.True(NativeMethodDeclaresNoDryRunParameter(native),
                $"{HelperFile} invokes the literal-bound native '{native}' inside "
                + $"{EnclosingMember(call)}, and RgbLib.NativeMethods.{native} declares a "
                + $"'{DryRun}' parameter that no clause of this pin inspects. " + Asymmetry);
        }

        var methodInfoLocals = helperRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer != null
                        && Unwrap(v.Initializer.Value) is InvocationExpressionSyntax bind
                        && NameOf(bind) == "GetMethod")
            .Select(v => (EnclosingMember(v), v.Identifier.ValueText))
            .ToHashSet();
        var argumentArrayLocals = helperRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => (v.Parent as VariableDeclarationSyntax)?.Type is ArrayTypeSyntax
                        || (v.Initializer != null
                            && Unwrap(v.Initializer.Value)
                                is CollectionExpressionSyntax or ArrayCreationExpressionSyntax
                                    or ImplicitArrayCreationExpressionSyntax))
            .Select(v => (EnclosingMember(v), v.Identifier.ValueText))
            .ToHashSet();

        var rebinds = helperRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id
                        && (methodInfoLocals.Contains((EnclosingMember(a), id.Identifier.ValueText))
                            || argumentArrayLocals.Contains((EnclosingMember(a), id.Identifier.ValueText))))
            .ToList();
        Assert.True(rebinds.Count == 0,
            $"{HelperFile} reassigns a MethodInfo local or a native argument array after binding it "
            + $"({string.Join(", ", rebinds.Select(a => $"{EnclosingMember(a)}: {a}"))}). Every clause "
            + "above reads a call site's receiver and arguments where they are DECLARED, so a later "
            + "rebind lets a cleared receiver carry send_begin to rgb-lib, or replaces the inspected "
            + $"array with one whose dry_run this pin never reads. " + Asymmetry);
        var argumentWrites = helperRoot.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is ElementAccessExpressionSyntax element
                        && element.Expression is IdentifierNameSyntax id
                        && argumentArrayLocals.Contains((EnclosingMember(a), id.Identifier.ValueText)))
            .ToList();
        Assert.True(argumentWrites.Count == 0,
            $"{HelperFile} writes into a native argument array after building it "
            + $"({string.Join(", ", argumentWrites.Select(a => $"{EnclosingMember(a)}: {a}"))}). This "
            + "pin reads the argument list where the array is CONSTRUCTED, so a later element write can "
            + $"set dry_run = true on send_begin while every clause above stays green. " + Asymmetry);

        var indirectDispatch = helperRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(NameOf)
            .Where(n => n is "CreateDelegate" or "DynamicInvoke")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(indirectDispatch.Count == 0,
            $"{HelperFile} dispatches reflectively through {string.Join(", ", indirectDispatch)}. Every "
            + "clause above finds native call sites by looking for `.Invoke`, so a MethodInfo turned "
            + $"into a delegate reaches rgb-lib without any of them reading its dry_run. " + Asymmetry);
    }
}
