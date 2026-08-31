using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// A real <see cref="CSharpCompilation"/> over every plugin source, shared by the tests that pin
/// source shape. Semantic binding is required rather than optional: with no alias, no local
/// declaration and the right qualifier, a stub type declared in the *enclosing* namespace beats the
/// using-imported one by C# name resolution, so a syntax-only assertion can be satisfied verbatim
/// while the pinned member never runs.
/// </summary>
internal sealed class PluginCompilation
{
    const string PluginAssemblyFileName = "BTCPayServer.Plugins.RgbUtexo.dll";

    static readonly Lazy<PluginCompilation> Lazy = new(() => new PluginCompilation(), isThreadSafe: true);

    internal static PluginCompilation Shared => Lazy.Value;

    readonly Dictionary<string, SyntaxTree> _byRelativePath;

    // Exposed separately so the tests that only need the repo root (the MSBuild-evaluation pins)
    // do not pay for building the whole compilation.
    internal static string RepoRootPath { get; } = ResolveRepoRoot();

    internal string RepoRoot { get; }
    internal CSharpCompilation Compilation { get; }

    PluginCompilation()
    {
        RepoRoot = RepoRootPath;
        var (configuration, targetFramework) = HostBuild();

        var sources = SourceFiles(RepoRoot).ToList();
        sources.Add(SingleGlobalUsings(RepoRoot, configuration, targetFramework));

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path))
            .ToList();

        Compilation = CSharpCompilation.Create(
            "PluginSourcePins",
            trees,
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        _byRelativePath = trees.ToDictionary(
            tree => Relative(RepoRoot, tree.FilePath),
            StringComparer.OrdinalIgnoreCase);
    }

    internal IEnumerable<SyntaxTree> AllTrees => Compilation.SyntaxTrees;

    internal SyntaxTree Tree(string relativePath)
    {
        Assert.True(_byRelativePath.TryGetValue(relativePath, out var tree),
            $"{relativePath} is not part of the plugin compile set ({_byRelativePath.Count} trees under {RepoRoot})");
        return tree!;
    }

    internal SemanticModel Model(SyntaxTree tree) => Compilation.GetSemanticModel(tree);

    static string ResolveRepoRoot()
    {
        var value = typeof(PluginCompilation).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepoRoot")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(value),
            "AssemblyMetadata(\"RepoRoot\") is missing — add it to the Tests csproj");
        return Path.GetFullPath(value!);
    }

    static (string Configuration, string TargetFramework) HostBuild()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.NotNull(output.Parent);
        return (output.Parent!.Name, output.Name);
    }

    // Mirrors the plugin csproj's Compile set: the default **/*.cs glob minus the three
    // directory removals, minus intermediate/output directories.
    static IEnumerable<string> SourceFiles(string repoRoot)
    {
        string[] excludedRoots = ["submodules", "RgbRestoreHelper", "BTCPayServer.Plugins.RgbUtexo.Tests"];

        foreach (var path in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var segments = Relative(repoRoot, path).Split('/');
            if (excludedRoots.Contains(segments[0], StringComparer.OrdinalIgnoreCase)) continue;
            if (segments.SkipLast(1).Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase)
                                           || s.Equals("bin", StringComparison.OrdinalIgnoreCase))) continue;
            yield return path;
        }
    }

    // The implicit usings live in a generated file under obj/<Configuration>/<TFM>/. Dropping it
    // costs every global using and buries the compilation in CS0246; a bare recursive glob matches
    // one file per configuration/TFM ever built here (including a retired net8.0 leftover) and
    // would feed duplicate global usings into one compilation.
    static string SingleGlobalUsings(string repoRoot, string configuration, string targetFramework)
    {
        var directory = Path.Combine(repoRoot, "obj", configuration, targetFramework);
        var matches = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.GlobalUsings.g.cs")
            : [];
        Assert.True(matches.Length == 1,
            $"expected exactly one *.GlobalUsings.g.cs under {directory}, found {matches.Length}");
        return matches[0];
    }

    // The loaded-assembly set alone is not enough — the asserted call site then fails to bind at
    // all (Symbol = null, OverloadResolutionFailure). The plugin's own stale output copy is
    // excluded so a clause never depends on build freshness (it produces CS0122 and a null symbol).
    static IEnumerable<MetadataReference> References()
    {
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            byFileName[Path.GetFileName(dll)] = dll;

        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var dll in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                byFileName.TryAdd(Path.GetFileName(dll), dll);
        }

        byFileName.Remove(PluginAssemblyFileName);

        var references = new List<MetadataReference>();
        foreach (var path in byFileName.Values)
        {
            if (!IsManaged(path)) continue;
            references.Add(MetadataReference.CreateFromFile(path));
        }
        return references;
    }

    static bool IsManaged(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// The five cumulative standing rules the source pins must satisfy. Each was written after a
/// measured evasion of the previous wording: text-free, shadow-free, reassignment-free,
/// directive-free, symbol-bound — and whole-compilation for every absence assertion.
/// </summary>
internal static class RoslynPins
{
    internal const string SelfCheckFile = "Services/RgbNativeSelfCheck.cs";
    internal const string VerifyNativeFile = "Services/RgbVerifyNative.cs";
    internal const string PluginFile = "RGBPlugin.cs";

    internal const string SelfCheckType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbNativeSelfCheck";
    internal const string VerifyNativeType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbVerifyNative";

    // Every name whose declarations are counted. A name absent from a file's mandated map is
    // required to be declared zero times there; the counts are per-file, never per-name.
    static readonly string[] CountedNames =
    [
        "Verify", "VerifyOrLog", "DefaultProbe", "DefaultHasExport",
        "ResolveBaseDir", "TryLoadFromCandidates", "NativeLibrary", "Console",
        // Finding C: the replenishment decision surface. A second declaration of any of these means a
        // parallel copy of the gate exists somewhere the clauses below never parse.
        "ReplenishUtxosAsync", "ActivePendingInvoicePredicate",
        "EvaluateReplenishEligibility", "EvaluateReplenishDemand",
        "ReplenishCooldownTracker", "NextEligibleAt", "RecordAttemptSucceeded",
        "RecordAttemptFailed", "RecordNoActionNeeded", "Prune",
        // Finding H2b: the listener's ingress predicate. The finding WAS this expression duplicated across
        // two call sites with one copy wrong, so a second declaration is a regression of the root cause.
        "ShouldEnqueue"
    ];

    internal static readonly Dictionary<string, int> RepoWideMandatedTotals = new()
    {
        ["Verify"] = 2,
        ["VerifyOrLog"] = 2,
        ["DefaultProbe"] = 1,
        ["DefaultHasExport"] = 1,
        ["ResolveBaseDir"] = 1,
        ["TryLoadFromCandidates"] = 1,
        ["ReplenishUtxosAsync"] = 1,
        ["ActivePendingInvoicePredicate"] = 1,
        ["EvaluateReplenishEligibility"] = 1,
        ["EvaluateReplenishDemand"] = 1,
        ["ReplenishCooldownTracker"] = 1,
        ["NextEligibleAt"] = 1,
        ["RecordAttemptSucceeded"] = 1,
        ["RecordAttemptFailed"] = 1,
        ["RecordNoActionNeeded"] = 1,
        ["Prune"] = 1,
        ["ShouldEnqueue"] = 1,
    };

    internal static SyntaxNode BodyOf(BaseMethodDeclarationSyntax method)
    {
        var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        Assert.NotNull(body);
        return body!;
    }

    internal static MethodDeclarationSyntax Method(SyntaxTree tree, string typeName, string methodName,
        Func<MethodDeclarationSyntax, bool>? where = null)
    {
        var candidates = tree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.ValueText == typeName)
            .SelectMany(t => t.Members.OfType<MethodDeclarationSyntax>())
            .Where(m => m.Identifier.ValueText == methodName)
            .Where(m => where == null || where(m))
            .ToList();
        Assert.True(candidates.Count == 1,
            $"{tree.FilePath}: expected exactly one matching '{typeName}.{methodName}', found {candidates.Count}");
        return candidates[0];
    }

    // Rule 4 — repo-wide. #if-guarded code is disabled trivia, invisible to every node assertion,
    // while DefineConstants compiles it live; a using alias or `using static` re-points a pinned
    // name without touching any asserted node.
    internal static void AssertNoDirectivesOrAliases(PluginCompilation plugin)
    {
        foreach (var tree in plugin.AllTrees)
        {
            var root = tree.GetRoot();

            var directives = root.DescendantTrivia()
                .Where(t => t.IsKind(SyntaxKind.IfDirectiveTrivia)
                         || t.IsKind(SyntaxKind.ElifDirectiveTrivia)
                         || t.IsKind(SyntaxKind.ElseDirectiveTrivia))
                .ToList();
            Assert.True(directives.Count == 0,
                $"{tree.FilePath}: conditional compilation is not allowed in a parsed source ({directives.Count} directive(s))");

            var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias != null || !u.StaticKeyword.IsKind(SyntaxKind.None))
                .Select(u => u.ToString())
                .ToList();
            Assert.True(aliases.Count == 0,
                $"{tree.FilePath}: using alias / using static is not allowed in a parsed source: {string.Join(", ", aliases)}");
        }
    }

    // Rule 2(a) — per file.
    internal static void AssertDeclarationCounts(SyntaxTree tree, IReadOnlyDictionary<string, int> mandated)
    {
        foreach (var name in CountedNames)
        {
            var expected = mandated.TryGetValue(name, out var count) ? count : 0;
            var actual = CountDeclarations(tree.GetRoot(), name);
            Assert.True(actual == expected,
                $"{tree.FilePath}: expected {expected} declaration(s) named '{name}', found {actual}");
        }
    }

    // Rule 2(a) at rule 5's scope: a declaration in a file no clause parses is exactly the hole.
    internal static void AssertRepoWideDeclarationTotals(PluginCompilation plugin)
    {
        foreach (var name in CountedNames)
        {
            var expected = RepoWideMandatedTotals.TryGetValue(name, out var count) ? count : 0;
            var actual = plugin.AllTrees.Sum(tree => CountDeclarations(tree.GetRoot(), name));
            Assert.True(actual == expected,
                $"the plugin declares '{name}' {actual} time(s); the mandated total is {expected}");
        }
    }

    // Rule 2(b) — a local function shadowing a class member compiles without a warning, satisfies
    // every node assertion verbatim, and the real helper never runs.
    // Coverage is three declaration forms and no more: local declarations, local functions, and the
    // method's own parameters. Measured by a Roslyn probe, it does NOT see a foreach variable, a pattern
    // or out variable, a lambda parameter, a deconstruction, a catch declaration, a query range variable
    // or a switch-case pattern. A caller that needs those excluded must block them itself; this helper
    // does not, and its failure message therefore names what it found rather than how the node binds.
    internal static void AssertNoLocalShadow(BaseMethodDeclarationSyntax method, params string[] names)
    {
        var body = BodyOf(method);
        foreach (var name in names)
        {
            var shadows = body.DescendantNodes().Count(node => node switch
            {
                LocalFunctionStatementSyntax f => f.Identifier.ValueText == name,
                VariableDeclaratorSyntax v => v.Identifier.ValueText == name,
                _ => false
            }) + method.ParameterList.Parameters.Count(p => p.Identifier.ValueText == name);
            Assert.True(shadows == 0,
                $"'{name}' is redeclared as a local, local function or parameter inside {Describe(method)}; "
                + "a name this pin asserts on may not be redeclared there");
        }
    }

    // Rule 2(b) done POSITIVELY, and the reason AssertNoLocalShadow is not sufficient on its own.
    // Enumerating the syntax that can introduce a shadow is the construction this file already rejects for
    // `Inert` and for the "written is not run" clauses, relearned a third time: an `out var ShouldEnqueue`
    // declared inside an argument list no clause parses rebinds the pinned call to a delegate local that
    // returns false — every enqueue suppressed, whole suite green (measured). Binding is what the clauses
    // actually need, so assert binding: a local reports the same Name and ContainingType as the member it
    // shadows and differs only in Kind. That covers every LOCAL declaration form at once rather than a list
    // of them — and only those; two further families needed their own discriminators, below.
    // ORDINARY, because SymbolKind alone is not enough for a method: a LOCAL FUNCTION is an IMethodSymbol
    // whose ContainingType is the enclosing type, so it reports Kind, Name and ContainingType exactly as the
    // member it shadows and would pass a Kind-only rule. MethodKind is what separates them.
    // FULLY QUALIFIED containing type, because the simple name is not enough either: a class of the same
    // simple name in another namespace, inherited by the pinned type, supplies an inherited member that
    // reports the same Kind, Name and ContainingType.Name — measured GREEN against a simple-name compare.
    // The predicate has a second line of defence in the repo-wide declaration count; `ComputeExpiry` and the
    // two field receivers have none, which is why this comparison and not that count is what makes them safe.
    internal static void AssertBindsToMemberOf(PluginCompilation plugin, SyntaxTree tree, SyntaxNode node,
        SymbolKind kind, string containingType, string name, string where)
    {
        var symbol = BoundSymbol(plugin, tree, node);
        var methodKind = (symbol as IMethodSymbol)?.MethodKind;
        var actualType = symbol.ContainingType?.ToDisplayString();
        Assert.True(symbol.Kind == kind && symbol.Name == name && actualType == containingType
                    && methodKind is null or MethodKind.Ordinary,
            $"{where}: '{node}' must bind to the {kind} {containingType}.{name}; it binds to the "
            + $"{symbol.Kind}{(methodKind is null ? "" : $"/{methodKind}")} {actualType}.{symbol.Name}. "
            + "A shadow, or an inherited member of a same-named type, that reaches this point satisfies "
            + "every syntactic clause while the pinned member never runs.");
    }

    // Rule 3 — a node assertion pins what the tree says, not what the value is: `probe ??= Fake;`
    // before the delegation leaves the asserted coalesce node completely intact.
    internal static void AssertNeverReassigned(BaseMethodDeclarationSyntax method, params string[] identifiers)
    {
        var body = BodyOf(method);
        foreach (var identifier in identifiers)
        {
            Assert.True(AssignmentsTo(body, identifier).Count == 0,
                $"'{identifier}' is reassigned inside {Describe(method)}");
            var byReference = body.DescendantNodes().OfType<ArgumentSyntax>()
                .Count(a => !a.RefKindKeyword.IsKind(SyntaxKind.None)
                            && a.Expression is IdentifierNameSyntax id
                            && id.Identifier.ValueText == identifier);
            Assert.True(byReference == 0,
                $"'{identifier}' is passed by ref/out inside {Describe(method)}");
        }
    }

    // Rule 3 inverted for the one assignment a clause itself pins: it must be the only one, or a
    // later overwrite silently deletes what the clause claims to have pinned.
    internal static void AssertSingleAssignmentTo(BaseMethodDeclarationSyntax method, string identifier,
        AssignmentExpressionSyntax pinned)
    {
        var assignments = AssignmentsTo(BodyOf(method), identifier);
        Assert.True(assignments.Count == 1,
            $"'{identifier}' is assigned {assignments.Count} time(s) inside {Describe(method)}; exactly one is allowed");
        Assert.Same(pinned, assignments[0]);
    }

    internal static ISymbol BoundSymbol(PluginCompilation plugin, SyntaxTree tree, SyntaxNode node)
    {
        var symbol = plugin.Model(tree).GetSymbolInfo(node).Symbol;
        Assert.True(symbol != null,
            $"{tree.FilePath}: '{node}' does not bind to any symbol "
            + $"(CandidateReason = {plugin.Model(tree).GetSymbolInfo(node).CandidateReason})");
        return symbol!;
    }

    // BCL members are matched syntactically on their rightmost two name components: they do not
    // bind under this reference set, and matching the whole expression's text lets a
    // fully-qualified System.Runtime.InteropServices.NativeLibrary.Load slip past.
    internal static bool NamesBclMember(MemberAccessExpressionSyntax access, string type, string member)
    {
        if (access.Name.Identifier.ValueText != member) return false;
        return access.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == type,
            MemberAccessExpressionSyntax qualified => qualified.Name.Identifier.ValueText == type,
            _ => false
        };
    }

    static List<AssignmentExpressionSyntax> AssignmentsTo(SyntaxNode body, string identifier) =>
        body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == identifier)
            .ToList();

    static int CountDeclarations(SyntaxNode root, string name) =>
        root.DescendantNodes().Count(node => DeclaredName(node) == name);

    static string? DeclaredName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        LocalFunctionStatementSyntax f => f.Identifier.ValueText,
        BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
        DelegateDeclarationSyntax d => d.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        VariableDeclaratorSyntax v => v.Identifier.ValueText,
        ParameterSyntax p => p.Identifier.ValueText,
        _ => null
    };

    static string Describe(BaseMethodDeclarationSyntax method) =>
        method is MethodDeclarationSyntax m
            ? $"{m.Identifier.ValueText}({m.ParameterList.Parameters.Count} params)"
            : method.ToString();
}
