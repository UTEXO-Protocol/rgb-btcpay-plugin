using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins properties of the startup self-check that no runtime assertion can reach: which member a
/// name resolves to, which default a production call site takes, and what the probe path may not
/// touch. Every clause here follows the five standing rules — node assertions only, no shadowing,
/// no reassignment, no conditional compilation, and semantic binding through a real compilation.
/// </summary>
public class RgbNativeSourcePinTests
{
    [Fact]
    public void PluginSources_ContainNoConditionalCompilationOrAliases()
    {
        RoslynPins.AssertNoDirectivesOrAliases(PluginCompilation.Shared);
    }

    [Fact]
    public void PluginSources_DeclarePinnedNamesExactlyAsMandated()
    {
        RoslynPins.AssertRepoWideDeclarationTotals(PluginCompilation.Shared);
    }

    [Fact]
    public void PluginStartup_InvokesLogOnlyEntryPoint()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.PluginFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>());

        var execute = RoslynPins.Method(tree, "RGBPlugin", "Execute");
        RoslynPins.AssertNoLocalShadow(execute, "VerifyOrLog");
        RoslynPins.AssertNeverReassigned(execute, "ctx");

        var statements = Assert.IsType<BlockSyntax>(execute.Body).Statements;

        var probes = statements
            .Select((statement, index) => (statement, index))
            .Where(x => x.statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }
                        && NameOf(invocation) == "VerifyOrLog")
            .ToList();
        Assert.True(probes.Count == 1,
            $"RGBPlugin.Execute must contain exactly one live VerifyOrLog statement directly in its body, found {probes.Count}");

        var probeIndex = probes[0].index;
        var probeCall = (InvocationExpressionSyntax)((ExpressionStatementSyntax)probes[0].statement).Expression;

        var qualifier = Assert.IsType<MemberAccessExpressionSyntax>(probeCall.Expression);
        Assert.True(qualifier.Expression is IdentifierNameSyntax { Identifier.ValueText: "RgbNativeSelfCheck" },
            $"the probe call must be member-access-qualified with RgbNativeSelfCheck, found '{qualifier.Expression}'");

        var arguments = probeCall.ArgumentList.Arguments;
        Assert.True(arguments.Count == 1,
            $"the probe must be called with exactly ctx.BootstrapServices — no probe/hasExport/sink override, found {arguments.Count} argument(s)");
        Assert.Null(arguments[0].NameColon);
        var argument = Assert.IsType<MemberAccessExpressionSyntax>(arguments[0].Expression);
        Assert.Equal("BootstrapServices", argument.Name.Identifier.ValueText);
        Assert.True(argument.Expression is IdentifierNameSyntax { Identifier.ValueText: "ctx" },
            $"the probe argument must be ctx.BootstrapServices, found '{argument}'");

        for (var i = 0; i < probeIndex; i++)
        {
            Assert.False(ContainsReturn(statements[i]),
                $"RGBPlugin.Execute returns (statement {i}) before the self-check — a degraded startup would skip the diagnostic");
        }

        var loadConfiguration = statements
            .Select((statement, index) => (statement, index))
            .Where(x => x.statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(invocation => NameOf(invocation) == "LoadConfiguration"))
            .Select(x => x.index)
            .ToList();
        Assert.True(loadConfiguration.Count == 1,
            $"expected exactly one statement invoking LoadConfiguration, found {loadConfiguration.Count}");
        Assert.True(probeIndex < loadConfiguration[0],
            "the self-check must run before LoadConfiguration, whose uncaught failures would otherwise skip it");

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, probeCall));
        Assert.Equal("VerifyOrLog", symbol.Name);
        Assert.Equal(RoslynPins.SelfCheckType, symbol.ContainingType.ToDisplayString());
    }

    [Fact]
    public void ResolveNative_DelegatesToSharedCandidateLoop()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.VerifyNativeFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>
        {
            ["TryLoadFromCandidates"] = 1,
            ["ResolveBaseDir"] = 1,
        });

        var resolve = RoslynPins.Method(tree, "RgbVerifyNative", "ResolveNative");
        RoslynPins.AssertNoLocalShadow(resolve, "TryLoadFromCandidates", "ResolveBaseDir");
        RoslynPins.AssertNeverReassigned(resolve, "assembly", "libraryName");

        var block = Assert.IsType<BlockSyntax>(resolve.Body);
        var guard = Assert.IsType<IfStatementSyntax>(block.Statements[0]);
        var condition = Assert.IsType<BinaryExpressionSyntax>(guard.Condition);
        Assert.True(condition.IsKind(SyntaxKind.NotEqualsExpression),
            $"the first statement must be the libraryName != Library guard, found '{guard.Condition}'");
        Assert.True(condition.Left is IdentifierNameSyntax { Identifier.ValueText: "libraryName" }
                    && condition.Right is IdentifierNameSyntax { Identifier.ValueText: "Library" },
            $"the guard must compare libraryName against Library, found '{guard.Condition}'");
        Assert.NotEmpty(guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>());

        Assert.Empty(block.DescendantNodes().Where(node =>
            node is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax));

        AssertDelegatesToCandidateLoop(plugin, tree, resolve,
            firstArgument: argument =>
            {
                Assert.True(argument.ArgumentList.Arguments.Count == 1
                            && argument.ArgumentList.Arguments[0].Expression
                                is IdentifierNameSyntax { Identifier.ValueText: "assembly" },
                    $"ResolveNative must pass ResolveBaseDir(assembly), found 'ResolveBaseDir({argument.ArgumentList.Arguments})'");
            });

        var selfCheckTree = plugin.Tree(RoslynPins.SelfCheckFile);
        var defaultProbe = RoslynPins.Method(selfCheckTree, "RgbNativeSelfCheck", "DefaultProbe");
        RoslynPins.AssertNoLocalShadow(defaultProbe, "TryLoadFromCandidates", "ResolveBaseDir");

        AssertDelegatesToCandidateLoop(plugin, selfCheckTree, defaultProbe,
            firstArgument: argument =>
            {
                Assert.True(argument.ArgumentList.Arguments.Count == 1, $"found 'ResolveBaseDir({argument.ArgumentList.Arguments})'");
                var assembly = Assert.IsType<MemberAccessExpressionSyntax>(argument.ArgumentList.Arguments[0].Expression);
                Assert.Equal("Assembly", assembly.Name.Identifier.ValueText);
                var typeOf = Assert.IsType<TypeOfExpressionSyntax>(assembly.Expression);
                Assert.True(typeOf.Type is IdentifierNameSyntax { Identifier.ValueText: "RgbVerifyNative" },
                    $"DefaultProbe must resolve the base directory from the plugin's own assembly, found '{assembly}'");
            });
    }

    [Fact]
    public void ConvenienceOverloads_BindTheirDefaultsToTheRealHelpers()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.SelfCheckFile);
        RoslynPins.AssertDeclarationCounts(tree, new Dictionary<string, int>
        {
            ["Verify"] = 2,
            ["VerifyOrLog"] = 2,
            ["DefaultProbe"] = 1,
            ["DefaultHasExport"] = 1,
        });

        foreach (var name in new[] { "Verify", "VerifyOrLog" })
        {
            var overload = RoslynPins.Method(tree, "RgbNativeSelfCheck", name, IsConvenienceOverload);
            RoslynPins.AssertNoLocalShadow(overload, "DefaultProbe", "DefaultHasExport", name);
            RoslynPins.AssertNeverReassigned(overload, "probe", "hasExport", "sink", "sp");

            var body = RoslynPins.BodyOf(overload);

            var delegation = SingleInvocation(body, name);
            var delegated = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, delegation));
            Assert.Equal(name, delegated.Name);
            Assert.Equal(RoslynPins.SelfCheckType, delegated.ContainingType.ToDisplayString());
            Assert.Equal(4, delegated.Parameters.Length);
            Assert.Equal("ILoggerFactory", delegated.Parameters[0].Type.Name);
            Assert.Equal("TextWriter", delegated.Parameters[1].Type.Name);

            AssertCoalescedDefault(plugin, tree, delegation, "probe", "DefaultProbe");
            AssertCoalescedDefault(plugin, tree, delegation, "hasExport", "DefaultHasExport");

            var sinkAssignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "writer" })
                .ToList();
            Assert.True(sinkAssignments.Count == 1,
                $"{name}(IServiceProvider?) must assign 'writer' exactly once, found {sinkAssignments.Count}");
            var coalesce = Assert.IsType<BinaryExpressionSyntax>(sinkAssignments[0].Right);
            Assert.True(coalesce.IsKind(SyntaxKind.CoalesceExpression),
                $"'writer' must be assigned 'sink ?? Console.Error', found '{sinkAssignments[0]}'");
            Assert.True(coalesce.Left is IdentifierNameSyntax { Identifier.ValueText: "sink" },
                $"'writer' must default from the sink parameter, found '{sinkAssignments[0]}'");
            var consoleError = Assert.IsType<MemberAccessExpressionSyntax>(coalesce.Right);
            Assert.True(RoslynPins.NamesBclMember(consoleError, "Console", "Error"),
                $"the default sink must be Console.Error, found '{consoleError}'");
            RoslynPins.AssertSingleAssignmentTo(overload, "writer", sinkAssignments[0]);
        }
    }

    [Fact]
    public void DefaultHelpers_AreNothingButTheirDelegation()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.SelfCheckFile);

        var probe = RoslynPins.Method(tree, "RgbNativeSelfCheck", "DefaultProbe");
        var probeBody = AssertExpressionBodiedInvocation(probe);
        var probeTarget = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, probeBody));
        Assert.Equal("TryLoadFromCandidates", probeTarget.Name);
        Assert.Equal(RoslynPins.VerifyNativeType, probeTarget.ContainingType.ToDisplayString());

        var hasExport = RoslynPins.Method(tree, "RgbNativeSelfCheck", "DefaultHasExport");
        var hasExportBody = AssertExpressionBodiedInvocation(hasExport);
        var exportAccess = Assert.IsType<MemberAccessExpressionSyntax>(hasExportBody.Expression);
        Assert.True(RoslynPins.NamesBclMember(exportAccess, "NativeLibrary", "TryGetExport"),
            $"DefaultHasExport must delegate to NativeLibrary.TryGetExport, found '{exportAccess}'");
    }

    // The assembly-scoped NativeLibrary.Load overload throws instead of returning IntPtr.Zero, so
    // swapping it in converts an absent or corrupt native (states 1-2) into a self-check fault
    // (state 5) and makes the live resolver throw. No behavioural test reaches it: T18 injects a
    // loader and the healthy-host cases are green either way. Scanned over the whole compilation
    // because a partial class in an unparsed file defeats a per-file check.
    [Fact]
    public void PluginSources_NeverNameTheThrowingNativeLibraryLoadOverload()
    {
        var plugin = PluginCompilation.Shared;
        RoslynPins.AssertNoDirectivesOrAliases(plugin);

        var offenders = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(access => RoslynPins.NamesBclMember(access, "NativeLibrary", "Load"))
                .Select(access => $"{tree.FilePath}: {access}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"NativeLibrary.Load must never be used — it throws where TryLoad returns false: {string.Join("; ", offenders)}");
    }

    // The probe resolves a handle and checks exports; it must never call one. Every export returns
    // CResultString by value and the binding dereferences and frees the returned pointer, which
    // against an ABI-mismatched image can abort the process — at plugin load, on every install,
    // turning a diagnostic into an outage.
    [Fact]
    public void StartupSelfCheckPath_NeverInvokesAnExportedNativeFunction()
    {
        var plugin = PluginCompilation.Shared;
        var selfCheck = plugin.Compilation.GetTypeByMetadataName(RoslynPins.SelfCheckType);
        Assert.NotNull(selfCheck);

        var roots = new[] { "Verify", "VerifyOrLog", "DefaultProbe", "DefaultHasExport" }
            .SelectMany(name => selfCheck!.GetMembers(name).OfType<IMethodSymbol>())
            .ToList();
        Assert.Equal(6, roots.Count);

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var pending = new Queue<IMethodSymbol>(roots);
        var reachedExports = new List<string>();

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current)) continue;

            if (current.Name.StartsWith("rgbverify_", StringComparison.Ordinal))
            {
                reachedExports.Add(current.ToDisplayString());
                continue;
            }

            foreach (var edge in Edges(plugin, current)) pending.Enqueue(edge);
        }

        Assert.True(reachedExports.Count == 0,
            $"the startup self-check path reaches native export(s): {string.Join(", ", reachedExports)}");
    }

    static bool IsConvenienceOverload(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Count == 4
        && method.ParameterList.Parameters[0].Identifier.ValueText == "sp";

    static void AssertCoalescedDefault(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax delegation, string parameter, string helper)
    {
        var matches = delegation.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .OfType<BinaryExpressionSyntax>()
            .Where(binary => binary.IsKind(SyntaxKind.CoalesceExpression)
                             && binary.Left is IdentifierNameSyntax identifier
                             && identifier.Identifier.ValueText == parameter)
            .ToList();
        Assert.True(matches.Count == 1,
            $"the delegation must pass '{parameter} ?? {helper}', found {matches.Count} coalescing argument(s) on '{parameter}'");

        var right = Assert.IsType<IdentifierNameSyntax>(matches[0].Right);
        Assert.Equal(helper, right.Identifier.ValueText);

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, right));
        Assert.Equal(helper, symbol.Name);
        Assert.Equal(RoslynPins.SelfCheckType, symbol.ContainingType.ToDisplayString());
    }

    static void AssertDelegatesToCandidateLoop(PluginCompilation plugin, SyntaxTree tree,
        MethodDeclarationSyntax method, Action<InvocationExpressionSyntax> firstArgument)
    {
        var call = SingleInvocation(RoslynPins.BodyOf(method), "TryLoadFromCandidates");

        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, call));
        Assert.Equal("TryLoadFromCandidates", symbol.Name);
        Assert.Equal(RoslynPins.VerifyNativeType, symbol.ContainingType.ToDisplayString());

        Assert.DoesNotContain(call.ArgumentList.Arguments,
            argument => argument.NameColon?.Name.Identifier.ValueText == "load");
        Assert.True(call.ArgumentList.Arguments.Count == 5,
            $"the shared loop must be called with baseDir plus the four out-values and no loader override, found {call.ArgumentList.Arguments.Count} argument(s)");

        var baseDir = Assert.IsType<InvocationExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        Assert.Equal("ResolveBaseDir", NameOf(baseDir));
        var baseDirSymbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, baseDir));
        Assert.Equal(RoslynPins.VerifyNativeType, baseDirSymbol.ContainingType.ToDisplayString());
        firstArgument(baseDir);
    }

    // ---- G1-T10: the native result is read once, freed once, and every site routes through it ----

    const string RgbLibFile = "Services/RgbLibService.cs";
    const string RgbLibServiceType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbLibService";

    // The nine plain sites: payload or throw. The three seam-B sites branch on the error instead.
    static readonly (string Method, string Consumer)[] ReflectedSites =
    [
        ("BlindReceiveAsync", "Require"),
        ("CreateUtxosEndAsync", "Require"),
        ("RefreshAsync", "Require"),
        ("SendBeginAsync", "Require"),
        ("SendEndAsync", "Require"),
        ("GetAddressAsync", "Require"),
        ("GetBtcBalanceAsync", "Require"),
        ("ListAssetsAsync", "Require"),
        ("IssueAssetNiaAsync", "Require"),
        ("CreateUtxosBeginAsync", "InterpretCreateUtxosBegin"),
        ("ListBtcTransactionsAsync", "InterpretListBtcTransactions"),
        ("ListUnspentsAsync", "InterpretListUnspents"),
    ];

    // Indices rather than the tuples themselves: xUnit needs the member data to be serializable, and a
    // count-derived range is what stops a site added to the array above from carrying no coverage — which
    // is exactly what four hand-written InlineData(0..7) attributes let happen to the four sites that
    // replaced the leaking wrappers.
    public static TheoryData<int> ReflectedSiteIndices()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < ReflectedSites.Length; i++) data.Add(i);
        return data;
    }

    // Every field this service resolves out of RgbLib.NativeMethods, against the entry point it must
    // resolve. Pinning the field at the CALL SITE leaves a transposition among these sixteen near-identical
    // constructor lines completely invisible: the site still reads _getBtcBalanceMethod, the argument array
    // and the freeing reader are still right, every source pin stays green, and production calls the wrong
    // native function. Measured: binding _getBtcBalanceMethod to rgblib_get_address passed 38/38.
    static readonly (string Field, string EntryPoint)[] NativeBindings =
    [
        ("_getAddressMethod", "rgblib_get_address"),
        ("_issueAssetNiaMethod", "rgblib_issue_asset_nia"),
        ("_getBtcBalanceMethod", "rgblib_get_btc_balance"),
        ("_listAssetsMethod", "rgblib_list_assets"),
        ("_blindReceiveMethod", "rgblib_blind_receive"),
        ("_listUnspentsMethod", "rgblib_list_unspents"),
        ("_createUtxosBeginMethod", "rgblib_create_utxos_begin"),
        ("_createUtxosEndMethod", "rgblib_create_utxos_end"),
        ("_refreshMethod", "rgblib_refresh"),
        ("_listTransactionsMethod", "rgblib_list_transactions"),
        ("_sendBeginMethod", "rgblib_send_begin"),
        ("_sendEndMethod", "rgblib_send_end"),
        ("_goOnlineMethod", "rgblib_go_online"),
        ("_generateKeysMethod", "rgblib_generate_keys"),
        ("_restoreKeysMethod", "rgblib_restore_keys"),
        ("_backupMethod", "rgblib_backup"),
    ];

    public static TheoryData<string, string> NativeBindingRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var (field, entryPoint) in NativeBindings) data.Add(field, entryPoint);
        return data;
    }

    [Theory]
    [MemberData(nameof(NativeBindingRows))]
    public void EachReflectedFieldResolvesTheEntryPointItsNameClaims(string field, string entryPoint)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var model = plugin.Model(tree);

        // Searched across the whole type rather than inside one chosen constructor: this service has two
        // (the public one delegates to the internal one), and a pin that names the wrong one would assert
        // nothing at all.
        var assignments = tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => model.GetSymbolInfo(a.Left).Symbol is IFieldSymbol f
                        && f.Name == field
                        && f.ContainingType.ToDisplayString() == RgbLibServiceType)
            .ToList();

        Assert.True(assignments.Count == 1,
            $"{field} must be assigned exactly once in {RgbLibFile}, found {assignments.Count}");
        Assert.True(assignments[0].Ancestors().OfType<ConstructorDeclarationSyntax>().Any(),
            $"{field} must be resolved in a constructor, so a failed lookup fails at service construction "
            + "rather than on the first send");

        var lookup = assignments[0].Right.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetMethod" });

        var literal = Assert.IsType<LiteralExpressionSyntax>(lookup.ArgumentList.Arguments[0].Expression);
        Assert.Equal(entryPoint, literal.Token.ValueText);
    }

    [Fact] // G1-T10(a)
    public void TheNonFreeingReaders_AreGoneFromTheFile()
    {
        var tree = PluginCompilation.Shared.Tree(RgbLibFile);

        var survivors = tree.GetRoot().DescendantNodes()
            .Where(node => node switch
            {
                MethodDeclarationSyntax m => m.Identifier.ValueText is "GetNativeResult" or "GetNativeError",
                IdentifierNameSyntax i => i.Identifier.ValueText is "GetNativeResult" or "GetNativeError",
                _ => false
            })
            .ToList();

        Assert.True(survivors.Count == 0,
            "GetNativeResult/GetNativeError never freed their payload; leaving either in place leaves a "
            + $"non-freeing reader for a future edit to reach for, found {survivors.Count} reference(s)");
    }

    [Theory] // G1-T10(c) and (e) — the nine plain sites call Require; the three seam-B sites call their function
    [MemberData(nameof(ReflectedSiteIndices))]
    public void EachReflectedSite_CallsItsExtractedConsumer(int index)
    {
        var (methodName, consumer) = ReflectedSites[index];
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);

        var method = RoslynPins.Method(tree, "RgbLibService", methodName);
        RoslynPins.AssertNoLocalShadow(method, consumer, "ReadNativeResult");

        var call = SingleInvocation(RoslynPins.BodyOf(method), consumer);
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, call));
        Assert.Equal(consumer, symbol.Name);
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());
    }

    [Theory] // G1-T10(g) — and the consumer is fed the READER's output, not a fabricated result
    [MemberData(nameof(ReflectedSiteIndices))]
    public void EachReflectedSite_FeedsItsConsumerTheReadersOutput(int index)
    {
        var (methodName, consumer) = ReflectedSites[index];
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);

        var method = RoslynPins.Method(tree, "RgbLibService", methodName);
        var call = SingleInvocation(RoslynPins.BodyOf(method), consumer);

        // Pinning only that the consumer is CALLED leaves Require(new NativeCallResult("", null), …)
        // green: a synthesised result never reads the native pointer and never frees it, so the site
        // leaks on every call while every test and pin stays green.
        var argument = Assert.IsType<InvocationExpressionSyntax>(call.ArgumentList.Arguments[0].Expression);
        AssertReadsTheNativeResult(plugin, tree, argument);
    }

    [Fact] // G1-T10(g) for the one consumer whose statement shape is fixed by P-C8
    public void InterpretListUnspents_ReadsTheResultItIsGiven_AndKeepsItsPinnedShape()
    {
        var tree = PluginCompilation.Shared.Tree(RgbLibFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, "RgbLibService", "InterpretListUnspents"));

        // This consumer cannot be Require: P-C8 pins an `if (<identifier> == null) throw` statement, which
        // the Require form does not contain. So the result's payload is pinned through its local instead.
        var payload = SingleDeclarator(body, "unspentsJson");
        var access = Assert.IsType<MemberAccessExpressionSyntax>(payload.Initializer!.Value);
        Assert.Equal("Payload", access.Name.Identifier.ValueText);
        Assert.True(access.Expression is IdentifierNameSyntax { Identifier.ValueText: "r" },
            $"the payload must come from the result parameter, found '{access.Expression}'");

        var errors = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText == "Error"
                        && m.Expression is IdentifierNameSyntax { Identifier.ValueText: "r" })
            .ToList();
        Assert.True(errors.Count == 1,
            $"the throw must report the result's error, found {errors.Count} r.Error reference(s)");
    }

    [Fact] // G1-T10(f) — the production wiring
    public void TheProductionConstructor_PassesTheRealStringFree()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);

        // RoslynPins.Method matches only MethodDeclarationSyntax and BodyOf returns only Body /
        // ExpressionBody, so neither can see a constructor initializer — this clause is hand-rolled.
        var constructors = tree.GetRoot().DescendantNodes().OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "RgbLibService"
                        && c.ParameterList.Parameters.Count == 3)
            .ToList();
        Assert.True(constructors.Count == 1,
            $"expected exactly one three-argument RgbLibService constructor, found {constructors.Count}");

        var initializer = constructors[0].Initializer;
        Assert.True(initializer != null,
            "the public constructor must delegate to the internal one so the free delegate is an "
            + "argument this clause can pin; a field initializer would satisfy the runtime and hide it");

        var arguments = initializer!.ArgumentList.Arguments;
        Assert.True(arguments.Count == 6,
            $"expected the reflected type, the free delegate and the marshaller, found {arguments.Count} argument(s)");

        // A no-op lambda here leaves every unit test green — they count the TEST delegate — and ships
        // the leak unfixed. The argument must name the real deallocator.
        var free = Assert.IsType<IdentifierNameSyntax>(arguments[4].Expression);
        Assert.Equal("rgblib_string_free", free.Identifier.ValueText);
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, free));
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());
    }

    [Fact] // G1-T10(b) — no non-freeing reader can exist
    public void EveryCResultStringPayloadRead_HappensInsideOneOfTheTwoReaders()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var model = plugin.Model(tree);
        string[] readers = ["ReadNativeResult", "ReadRgbLibString"];

        // Resolved by DECLARING TYPE, not by the identifier: this file also reads `.inner` on a
        // CResult, whose opaque inner is freed by FreeCResultErrorString and free_invoice rather than
        // by either of these two readers, and a name-based clause would redden that correct code.
        var typed = tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Name.Identifier.ValueText == "inner")
            .Where(m => model.GetSymbolInfo(m).Symbol is IFieldSymbol f
                        && f.ContainingType.ToDisplayString() == "RgbLib.CResultString")
            .ToList();
        Assert.True(typed.Count > 0,
            "no CResultString.inner read bound at all — the clause would pass vacuously");

        foreach (var read in typed)
        {
            var enclosing = read.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            Assert.True(enclosing != null && readers.Contains(enclosing.Identifier.ValueText),
                $"CResultString.inner is read in '{enclosing?.Identifier.ValueText ?? "<no method>"}'; "
                + "only the two readers may touch it, because only they free it");
        }

        // The reflected route to the same field. The constructor resolves the FieldInfo and is the
        // one place outside a reader that may name it.
        var reflected = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.ValueText == "_innerField")
            .Where(i => i.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } m
                        || !readers.Contains(m.Identifier.ValueText))
            .ToList();
        Assert.True(reflected.All(i => i.Ancestors().OfType<ConstructorDeclarationSyntax>().Any()),
            "_innerField is used outside the two readers and outside the constructor — that is a "
            + "second reader of the native pointer, which is how the double-free comes back");
    }

    [Theory]
    [InlineData("rgblib_get_address", 1)]
    [InlineData("rgblib_get_btc_balance", 3)]
    [InlineData("rgblib_list_assets", 2)]
    [InlineData("rgblib_issue_asset_nia", 5)]
    public void TheReflectedEntryPointsReplacingTheLeakingWrappersExistWithTheAritiesPassed(
        string entryPoint, int arity)
    {
        // The three constructor lookups are `GetMethod(name)!`, so a renamed or re-signatured entry
        // point in a future RgbLib is a NullReferenceException at service construction, and a changed
        // arity is a TargetParameterCountException on first use — neither of which any source pin sees.
        var nativeMethods = typeof(RgbLib.RgbLibWallet).Assembly.GetType("RgbLib.NativeMethods");
        Assert.NotNull(nativeMethods);

        var method = nativeMethods!.GetMethod(entryPoint);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Equal(arity, parameters.Length);
        Assert.True(parameters[0].ParameterType.IsByRef,
            $"{entryPoint} must take the wallet struct by reference, so the write-back after Invoke "
            + "preserves what the typed wrapper's `ref _wallet` preserved");
        Assert.Equal("RgbLib.CResultString", method.ReturnType.ToString());
    }

    [Fact]
    public void EveryRgbLibWrapperCallIsInventoried()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var model = plugin.Model(tree);

        // RgbLib 0.3.0-beta.30 binds NO string-free at all, so every typed wrapper that marshals a
        // CResultString leaks its payload for the life of the process, and that leak happens inside the
        // package where this file's CResultString.inner pin cannot see it. An allowlist of names known
        // to leak would silently pass any wrapper nobody thought of, so this asserts the complete set of
        // RgbLibWallet members INVOKED IN RgbLibService.cs: a new call of any kind fails here until it is
        // reviewed and recorded below. Two limits stated rather than implied — it is scoped to this one
        // compile unit, and it matches invocation expressions, so binding a wrapper to a delegate and
        // calling that instead evades it. Neither is a shape a maintainer reaches for by accident, and
        // this file is where every native call in the plugin is funnelled.
        var reviewed = new Dictionary<string, string>
        {
            ["Dispose"] =
                "MARSHALS NOTHING: rgblib_drop_wallet returns void, so there is no CResultString payload "
                + "for ReadNativeResult to own or free. It is called on the failure path of wallet "
                + "construction because anything that throws after the rgb-lib constructor returns would "
                + "otherwise abandon a LIVE native wallet still holding rgb_runtime.lock — the failed Lazy "
                + "is never IsValueCreated so the cache cannot reach it, and beta.30 declares no finalizer "
                + "so the Rust Drop that removes that marker would never run. The next construction would "
                + "then reclaim a live owner's marker and open a second wallet on one directory."
        };

        var called = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => model.GetSymbolInfo(i).Symbol)
            .OfType<IMethodSymbol>()
            .Where(m => m.ContainingType.ToDisplayString() == "RgbLib.RgbLibWallet")
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var unreviewed = called.Where(n => !reviewed.ContainsKey(n)).ToList();
        Assert.True(unreviewed.Count == 0,
            "these RgbLibWallet wrappers are called but not inventoried; each one marshals a native "
            + "payload the package may never free, so route it through NativeMethods + ReadNativeResult "
            + "or record why it cannot be: " + string.Join(", ", unreviewed));

        var recordedButGone = reviewed.Keys.Where(k => !called.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(recordedButGone.Count == 0,
            "these wrappers are recorded as still in use but are no longer called — delete the entry so "
            + "the inventory keeps describing the code: " + string.Join(", ", recordedButGone));
    }

    [Theory]
    [InlineData("GetAddressAsync", "_getAddressMethod")]
    [InlineData("GetBtcBalanceAsync", "_getBtcBalanceMethod")]
    [InlineData("ListAssetsAsync", "_listAssetsMethod")]
    [InlineData("IssueAssetNiaAsync", "_issueAssetNiaMethod")]
    public void EachReflectedSiteInvokesItsOwnEntryPoint(string method, string expectedField)
    {
        // Substituting one resolved MethodInfo for another leaves every source pin and every signature
        // check green — the shape, the argument array and the reader are all still correct — while
        // production calls the wrong native function and fails on arity. So the binding of site to entry
        // point is pinned per site.
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var body = RoslynPins.BodyOf(RoslynPins.Method(tree, "RgbLibService", method));

        var invocations = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke" })
            .ToList();

        // Counted BEFORE any de-duplication: collapsing to distinct receivers first would report one
        // receiver for a site that invokes the same entry point twice, which is a second unread native
        // result and so a second leak, through a field name this assertion would still call correct.
        Assert.True(invocations.Count == 1,
            $"{method} must make exactly one reflected native call, found {invocations.Count}");

        var receiver = ((MemberAccessExpressionSyntax)invocations[0].Expression).Expression;
        var symbol = Assert.IsAssignableFrom<IFieldSymbol>(RoslynPins.BoundSymbol(plugin, tree, receiver));
        Assert.Equal(expectedField, symbol.Name);
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());
    }

    [Fact] // G1-T10(d)
    public void DecodeInvoice_RoutesTheInvoiceDataResultThroughTheTypedReader()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var method = RoslynPins.Method(tree, "RgbLibService", "DecodeInvoice");
        var body = RoslynPins.BodyOf(method);
        RoslynPins.AssertNoLocalShadow(method, "ReadRgbLibString");

        var read = SingleInvocation(body, "ReadRgbLibString");
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, read));
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());

        // The argument is pinned, not merely the call: handing the reader any other CResultString
        // would leave invoice_data's payload unfreed while this clause stayed green.
        Assert.True(read.ArgumentList.Arguments[0].Expression
                is IdentifierNameSyntax { Identifier.ValueText: "dataResult" },
            $"the reader must be given invoice_data's own result, found '{read.ArgumentList.Arguments[0]}'");

        var declared = SingleDeclarator(body, "dataResult");
        var call = Assert.IsType<InvocationExpressionSyntax>(declared.Initializer!.Value);
        Assert.Equal("rgblib_invoice_data", NameOf(call));
    }

    [Fact]
    public void DecodeInvoice_FreesTheInvoiceNewErrorStringBeforeItThrows()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RgbLibFile);
        var method = RoslynPins.Method(tree, "RgbLibService", "DecodeInvoice");
        var body = RoslynPins.BodyOf(method);
        RoslynPins.AssertNoLocalShadow(method, "FreeCResultErrorString");

        var free = SingleInvocation(body, "FreeCResultErrorString");
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, free));
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());

        Assert.True(free.ArgumentList.Arguments.Count == 1
                    && free.ArgumentList.Arguments[0].Expression
                        is IdentifierNameSyntax { Identifier.ValueText: "newResult" },
            "the free must be given invoice_new's own result, or the leaked error string is not the "
            + $"one freed, found '{free.ArgumentList}'");

        var branch = free.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
        Assert.True(branch != null,
            "the free must sit inside the non-Ok branch; on the Ok arm the same pointer is a boxed "
            + "Invoice and freeing it as a string is heap corruption");

        var statements = Assert.IsType<BlockSyntax>(branch!.Statement).Statements;
        var freeIndex = statements.IndexOf(free.FirstAncestorOrSelf<StatementSyntax>()!);
        var throwIndex = statements.IndexOf(statements.OfType<ThrowStatementSyntax>().Single());
        Assert.True(freeIndex >= 0 && freeIndex < throwIndex,
            $"the free must be a statement of the branch that precedes the throw, found free at "
            + $"{freeIndex} and throw at {throwIndex}");
    }

    static void AssertReadsTheNativeResult(PluginCompilation plugin, SyntaxTree tree,
        InvocationExpressionSyntax read)
    {
        Assert.Equal("ReadNativeResult", NameOf(read));
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, read));
        Assert.Equal(RgbLibServiceType, symbol.ContainingType.ToDisplayString());

        Assert.True(read.ArgumentList.Arguments.Count == 1,
            $"the reader takes the invoked result and nothing else, found {read.ArgumentList.Arguments.Count}");
        Assert.True(read.ArgumentList.Arguments[0].Expression
                is IdentifierNameSyntax { Identifier.ValueText: "result" },
            $"the reader must be given the native call's own result, found '{read.ArgumentList.Arguments[0]}'");
    }

    static VariableDeclaratorSyntax SingleDeclarator(SyntaxNode body, string name)
    {
        var matches = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.ValueText == name)
            .ToList();
        Assert.True(matches.Count == 1, $"expected exactly one declaration of '{name}', found {matches.Count}");
        Assert.NotNull(matches[0].Initializer);
        return matches[0];
    }

    static InvocationExpressionSyntax AssertExpressionBodiedInvocation(MethodDeclarationSyntax method)
    {
        Assert.Null(method.Body);
        var arrow = method.ExpressionBody;
        Assert.NotNull(arrow);
        var invocation = Assert.IsType<InvocationExpressionSyntax>(arrow!.Expression);
        Assert.Empty(arrow.DescendantNodes().OfType<LiteralExpressionSyntax>());
        Assert.Empty(arrow.DescendantNodes().OfType<ReturnStatementSyntax>());
        return invocation;
    }

    static InvocationExpressionSyntax SingleInvocation(SyntaxNode body, string name)
    {
        var matches = body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
            .Where(invocation => NameOf(invocation) == name)
            .ToList();
        Assert.True(matches.Count == 1,
            $"expected exactly one invocation of '{name}', found {matches.Count}");
        return matches[0];
    }

    static bool ContainsReturn(SyntaxNode statement) =>
        statement.DescendantNodesAndSelf()
            .Where(node => node is ReturnStatementSyntax)
            .Any(node => node.Ancestors()
                .TakeWhile(ancestor => ancestor != statement.Parent)
                .All(ancestor => ancestor is not (LambdaExpressionSyntax or AnonymousMethodExpressionSyntax
                    or LocalFunctionStatementSyntax)));

    static IEnumerable<IMethodSymbol> Edges(PluginCompilation plugin, IMethodSymbol method)
    {
        foreach (var symbol in ReferencedMethods(plugin, method.DeclaringSyntaxReferences))
            yield return symbol;

        var type = method.ContainingType;
        if (type == null) yield break;

        foreach (var constructor in type.StaticConstructors)
        {
            foreach (var symbol in ReferencedMethods(plugin, constructor.DeclaringSyntaxReferences))
                yield return symbol;
        }

        var initialised = type.GetMembers()
            .Where(member => member is IFieldSymbol or IPropertySymbol)
            .SelectMany(member => member.DeclaringSyntaxReferences);
        foreach (var symbol in ReferencedMethods(plugin, initialised))
            yield return symbol;
    }

    static IEnumerable<IMethodSymbol> ReferencedMethods(PluginCompilation plugin,
        IEnumerable<SyntaxReference> references)
    {
        foreach (var reference in references)
        {
            var node = reference.GetSyntax();
            if (!plugin.Compilation.SyntaxTrees.Contains(node.SyntaxTree)) continue;
            var model = plugin.Model(node.SyntaxTree);

            foreach (var descendant in node.DescendantNodesAndSelf())
            {
                if (descendant is not (IdentifierNameSyntax or GenericNameSyntax or MemberAccessExpressionSyntax))
                    continue;
                if (model.GetSymbolInfo(descendant).Symbol is IMethodSymbol referenced)
                    yield return referenced.OriginalDefinition;
            }
        }
    }

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty
    };
}
