using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbTrustedExceptionMessageProvenanceTests
{
    const string DiscriminatorFile = "Controllers/RgbOperatorFacingFailure.cs";
    const string DiscriminatorTypeName = "RgbOperatorFacingFailure";
    const string DiscriminatorPredicateName = "MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime";

    const int SitesTheTreeIsKnownToHaveSoTheScanCannotSilentlyGoVacuous = 15;

    static readonly string[] MessageBearingMemberNames = ["Message", "ToString", "StackTrace", "InnerException"];

    [Fact]
    public void TheTrustedSetIsDerivedFromTheDiscriminatorSourceRatherThanHardcodedHere()
    {
        var roots = TrustedRootTypes(PluginCompilation.Shared);

        Assert.Contains("System.InvalidOperationException", roots.Select(r => r.ToDisplayString()));
        Assert.True(roots.Count >= 3,
            $"{DiscriminatorFile}: {DiscriminatorPredicateName} no longer names at least three exception types in "
            + "a single `is` pattern, which is the shape this pin reads to learn which thrown types are trusted to "
            + "reach a store Owner verbatim. Re-derive TrustedRootTypes against the new shape before touching "
            + "anything else: with an empty trusted set the provenance pin below passes vacuously.");
    }

    [Fact]
    public void NoThrowOfATrustedTypeCarriesADotnetRuntimeExceptionMessageToAStoreOwner()
    {
        var plugin = PluginCompilation.Shared;
        var trustedRoots = TrustedRootTypes(plugin);
        var offences = new List<string>();
        var unresolvedThrownTypes = new List<string>();
        var examinedSites = 0;

        foreach (var tree in plugin.AllTrees)
        {
            var model = plugin.Model(tree);

            foreach (var creation in ThrownObjectCreations(tree))
            {
                var thrownType = model.GetTypeInfo(creation).Type as INamedTypeSymbol;
                if (thrownType == null || thrownType.TypeKind == TypeKind.Error)
                {
                    if (LooksLikeAnExceptionName(creation.Type.ToString()))
                        unresolvedThrownTypes.Add($"{Where(tree, creation)}: {creation.Type}");
                    continue;
                }

                if (!DerivesFromAny(thrownType, trustedRoots)) continue;

                var runtimeCatches = EnclosingRuntimeExceptionCatches(model, creation, trustedRoots);
                if (runtimeCatches.Count == 0) continue;
                examinedSites++;

                foreach (var catchClause in runtimeCatches)
                {
                    var tainted = TaintedLocalNames(model, catchClause, trustedRoots);

                    foreach (var argument in creation.ArgumentList?.Arguments ?? default)
                    {
                        foreach (var node in argument.Expression.DescendantNodesAndSelf())
                        {
                            var carrier = RuntimeExceptionMessageCarrier(model, node, tainted, trustedRoots);
                            if (carrier == null) continue;
                            if (DelegatedToAHelperThatConsultsTheDiscriminator(plugin, model, node)) continue;

                            offences.Add(
                                $"{Where(tree, creation)}: `throw new {thrownType.Name}(...)` inside "
                                + $"`catch ({catchClause.Declaration!.Type} {catchClause.Declaration!.Identifier})` "
                                + $"builds its message from `{carrier}`");
                        }
                    }
                }
            }
        }

        Assert.True(unresolvedThrownTypes.Count == 0,
            "these thrown types did not bind against the plugin compilation, so this pin could not tell whether "
            + "they are trusted by " + DiscriminatorPredicateName + ": "
            + string.Join("; ", unresolvedThrownTypes));

        Assert.True(examinedSites >= SitesTheTreeIsKnownToHaveSoTheScanCannotSilentlyGoVacuous,
            $"this pin found only {examinedSites} throw(s) of a discriminator-trusted exception type inside a catch "
            + "of a .NET runtime exception, below the "
            + $"{SitesTheTreeIsKnownToHaveSoTheScanCannotSilentlyGoVacuous} the tree is known to have. Either the "
            + "shapes it matches were refactored away — in which case re-derive the matcher, do not lower this "
            + "floor — or the pin has stopped reaching the code it is supposed to guard and is now green for free.");

        Assert.True(offences.Count == 0,
            "a throw of an exception type that "
            + $"{DiscriminatorTypeName}.{DiscriminatorPredicateName} TRUSTS is carrying a .NET runtime exception's "
            + "own text, so that text reaches a store Owner's browser verbatim:\n  "
            + string.Join("\n  ", offences)
            + "\n\nWhy this matters: the discriminator's whole premise is that an InvalidOperationException (or a "
            + "subclass of one — seven plugin exception types derive from it) means a human wrote this message for "
            + "an operator. The .NET runtime embeds absolute host filesystem paths in IOException, "
            + "FileNotFoundException, DirectoryNotFoundException, UnauthorizedAccessException and "
            + "DllNotFoundException messages by construction, so interpolating one into a trusted throw hands the "
            + "server's directory layout to anyone who can reach a store's RGB settings page. This exact class of "
            + "bug has already landed three times (RgbBackupScryptGuard's IOException and "
            + "UnauthorizedAccessException clauses, and RgbLibService's wallet bring-up catch).\n"
            + "What to do instead: log the caught exception server-side, and throw the trusted type with text a "
            + "human wrote for the operator — say what failed and what to do, and point at the server log for the "
            + "underlying detail. If the runtime detail genuinely has to be shown, route it through a helper that "
            + $"asks {DiscriminatorPredicateName} first (RgbLibService.WalletBringUpFailureForTheOperator is the "
            + "worked example), which is the only construction this pin exempts.\n"
            + "What this pin does NOT see, so do not read a green run as proof of absence: a message built in a "
            + "method other than the one holding the catch; taint through a field, property or a second local hop; "
            + "a runtime message reaching the throw through a collection, a format-string table or a delegate; and "
            + "any exception type outside the System/Microsoft namespaces (rgb-lib's own native text is deliberately "
            + "surfaced elsewhere and is not what this pin is about).");
    }

    static IEnumerable<ObjectCreationExpressionSyntax> ThrownObjectCreations(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes()
            .Select(node => node switch
            {
                ThrowStatementSyntax statement => statement.Expression,
                ThrowExpressionSyntax expression => expression.Expression,
                _ => null
            })
            .OfType<ObjectCreationExpressionSyntax>();

    static List<CatchClauseSyntax> EnclosingRuntimeExceptionCatches(
        SemanticModel model, SyntaxNode node, IReadOnlyCollection<INamedTypeSymbol> trustedRoots) =>
        node.Ancestors().OfType<CatchClauseSyntax>()
            .Where(clause => clause.Declaration != null
                             && !clause.Declaration.Identifier.IsKind(SyntaxKind.None)
                             && IsUntrustedDotnetRuntimeException(
                                 model.GetTypeInfo(clause.Declaration.Type).Type, trustedRoots))
            .ToList();

    static HashSet<string> TaintedLocalNames(
        SemanticModel model, CatchClauseSyntax catchClause, IReadOnlyCollection<INamedTypeSymbol> trustedRoots)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var scope = (SyntaxNode?)catchClause.Block ?? catchClause;

        foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer == null) continue;
            if (CarriesARuntimeExceptionMessage(model, declarator.Initializer.Value, trustedRoots))
                names.Add(declarator.Identifier.ValueText);
        }

        foreach (var assignment in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not IdentifierNameSyntax target) continue;
            if (CarriesARuntimeExceptionMessage(model, assignment.Right, trustedRoots))
                names.Add(target.Identifier.ValueText);
        }

        return names;
    }

    static bool CarriesARuntimeExceptionMessage(
        SemanticModel model, SyntaxNode expression, IReadOnlyCollection<INamedTypeSymbol> trustedRoots) =>
        expression.DescendantNodesAndSelf()
            .Any(node => DirectMessageCarrier(model, node, trustedRoots) != null);

    static string? RuntimeExceptionMessageCarrier(
        SemanticModel model, SyntaxNode node, IReadOnlyCollection<string> taintedLocalNames,
        IReadOnlyCollection<INamedTypeSymbol> trustedRoots)
    {
        var direct = DirectMessageCarrier(model, node, trustedRoots);
        if (direct != null) return direct;

        if (node is IdentifierNameSyntax identifier
            && taintedLocalNames.Contains(identifier.Identifier.ValueText)
            && model.GetSymbolInfo(identifier).Symbol is ILocalSymbol)
        {
            return $"the local {identifier.Identifier.ValueText}, initialised from the runtime exception's own text";
        }

        return null;
    }

    static string? DirectMessageCarrier(
        SemanticModel model, SyntaxNode node, IReadOnlyCollection<INamedTypeSymbol> trustedRoots)
    {
        if (node is not MemberAccessExpressionSyntax access) return null;
        if (!MessageBearingMemberNames.Contains(access.Name.Identifier.ValueText)) return null;
        if (!IsUntrustedDotnetRuntimeException(model.GetTypeInfo(access.Expression).Type, trustedRoots)) return null;
        return access.ToString();
    }

    static bool DelegatedToAHelperThatConsultsTheDiscriminator(
        PluginCompilation plugin, SemanticModel model, SyntaxNode node)
    {
        foreach (var invocation in node.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee) continue;
            if (!invocation.ArgumentList.Arguments.Any(argument =>
                    IsAnyException(model.GetTypeInfo(argument.Expression).Type))) continue;
            if (ReachesTheDiscriminator(plugin, callee, new HashSet<string>(StringComparer.Ordinal), depth: 0))
                return true;
        }
        return false;
    }

    static bool ReachesTheDiscriminator(
        PluginCompilation plugin, IMethodSymbol method, HashSet<string> visited, int depth)
    {
        if (method.ContainingType?.Name == DiscriminatorTypeName) return true;
        if (depth >= 4) return false;
        if (!visited.Add(method.ToDisplayString())) return false;

        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            var model = plugin.Compilation.GetSemanticModel(declaration.SyntaxTree);
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol callee
                    && ReachesTheDiscriminator(plugin, callee, visited, depth + 1))
                    return true;
            }
        }

        return false;
    }

    static List<INamedTypeSymbol> TrustedRootTypes(PluginCompilation plugin)
    {
        var tree = plugin.Tree(DiscriminatorFile);
        var model = plugin.Model(tree);
        var predicate = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.ValueText == DiscriminatorPredicateName);
        Assert.True(predicate != null,
            $"{DiscriminatorFile}: {DiscriminatorPredicateName} is gone. This pin reads the trusted exception "
            + "types out of that predicate; without it, it cannot tell a trusted throw from an untrusted one.");

        var roots = ((SyntaxNode?)predicate!.Body ?? predicate.ExpressionBody!)
            .DescendantNodesAndSelf().OfType<PatternSyntax>()
            .Select(TypeNamedByPattern)
            .OfType<TypeSyntax>()
            .Select(type => model.GetSymbolInfo(type).Symbol as INamedTypeSymbol
                            ?? model.GetTypeInfo(type).Type as INamedTypeSymbol)
            .OfType<INamedTypeSymbol>()
            .Where(IsAnyException)
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<INamedTypeSymbol>()
            .ToList();

        Assert.True(roots.Count > 0,
            $"{DiscriminatorFile}: no exception type could be read out of {DiscriminatorPredicateName}'s `is` "
            + "pattern, so this pin has nothing to consider trusted and would pass for free. Re-derive it against "
            + "the predicate's new shape.");
        return roots;
    }

    static TypeSyntax? TypeNamedByPattern(PatternSyntax pattern) => pattern switch
    {
        TypePatternSyntax type => type.Type,
        DeclarationPatternSyntax declaration => declaration.Type,
        RecursivePatternSyntax recursive => recursive.Type,
        ConstantPatternSyntax constant => constant.Expression as TypeSyntax,
        _ => null
    };

    static bool IsUntrustedDotnetRuntimeException(
        ITypeSymbol? type, IReadOnlyCollection<INamedTypeSymbol> trustedRoots)
    {
        if (type is not INamedTypeSymbol named) return false;
        if (!IsAnyException(named)) return false;
        if (named.DeclaringSyntaxReferences.Length > 0) return false;
        if (DerivesFromAny(named, trustedRoots)) return false;

        var rootNamespace = RootNamespaceOf(named);
        return rootNamespace is "System" or "Microsoft";
    }

    static string RootNamespaceOf(INamedTypeSymbol type)
    {
        var current = type.ContainingNamespace;
        var root = string.Empty;
        while (current != null && !current.IsGlobalNamespace)
        {
            root = current.Name;
            current = current.ContainingNamespace;
        }
        return root;
    }

    static bool IsAnyException(ITypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Exception") return true;
        }
        return false;
    }

    static bool DerivesFromAny(INamedTypeSymbol type, IReadOnlyCollection<INamedTypeSymbol> roots)
    {
        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
        {
            if (roots.Any(root => SymbolEqualityComparer.Default.Equals(root, current))) return true;
        }
        return false;
    }

    static bool LooksLikeAnExceptionName(string typeName) =>
        typeName.EndsWith("Exception", StringComparison.Ordinal);

    static string Where(SyntaxTree tree, SyntaxNode node) =>
        $"{Path.GetRelativePath(PluginCompilation.RepoRootPath, tree.FilePath).Replace('\\', '/')}"
        + $":{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}";
}
