using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins the one invariant that keeps an incoming RGB asset spendable: the schema list handed to
/// rgb-lib at wallet construction may never name a schema the plugin cannot afterwards enumerate.
/// rgb-lib accepts an incoming consignment when, and only when, its schema appears in that list
/// (src/wallet/online.rs:1121 in the pinned v0.3.0-beta.30 tree), so a schema listed there but
/// absent from ListAssetsResponse produces an asset that lands in the wallet, never appears in the
/// asset list, and can never be selected for a send.
///
/// Scope, stated so nobody mistakes it: these are syntactic and they catch an ACCIDENTAL regression —
/// a refactor, a merge, a well-meaning simplification. They are not a defence against a committer who
/// intends to defeat them, because whoever can edit these files can edit this one. In particular the
/// consumption check reads whether InterpretListAssets NAMES each modelled collection, not whether the
/// value reaches the returned list, so a reference that goes nowhere satisfies it. Proving reachability
/// needs data-flow analysis this file deliberately does not attempt. That residue is caught by code
/// review and by the live end-to-end run.
/// </summary>
public class RgbAssetSchemaSupportSourcePinTests
{
    const string SupportFile = "Services/RgbAssetSchemaSupport.cs";
    const string RgbLibFile = "Services/RgbLibService.cs";
    const string SupportMember = "TheOnlySchemasThisPluginCanEnumerateAndSpend";

    const string WhyTheTwoSetsMustMatch =
        "rgb-lib accepts an incoming consignment only for a schema named in the wallet's "
        + "supported_schemas, and the plugin can only surface an asset whose collection "
        + "ListAssetsResponse models. Naming a schema in the first without modelling it in the second "
        + "lets a payer put an asset into the wallet that no operator can list, select or send, which "
        + "strands it. Widening one set requires widening the other in the same change.";

    [Fact]
    public void EverySchemaHandedToRgbLibIsOneListAssetsResponseCanModel()
    {
        var declared = DeclaredSchemas();
        var modelled = ModelledAssetCollections();

        Assert.True(declared.SetEquals(modelled),
            $"declared {string.Join(",", declared.OrderBy(s => s))} but ListAssetsResponse models "
            + $"{string.Join(",", modelled.OrderBy(s => s))}. {WhyTheTwoSetsMustMatch}");
    }

    [Fact]
    public void BothWalletConstructionSitesTakeTheirSchemasFromTheSharedMember()
    {
        foreach (var (label, root) in WalletConstructingRoots())
        {
            var entries = root.DescendantNodes().OfType<InitializerExpressionSyntax>()
                .SelectMany(i => i.Expressions)
                .OfType<AssignmentExpressionSyntax>()
                .Where(a => KeyOf(a) == "supported_schemas")
                .ToList();

            Assert.True(entries.Count == 1,
                $"{label} sets supported_schemas {entries.Count} times; this pin reads exactly one");
            Assert.True(IsExactlyTheSharedMember(entries[0].Right),
                $"{label} builds supported_schemas from '{entries[0].Right}' rather than "
                + $"{SupportMember}. Two independently written lists drift, and the wallet the send "
                + $"helper constructs would then accept a schema the in-process wallet refuses, or the "
                + $"reverse. {WhyTheTwoSetsMustMatch}");
        }
    }

    [Fact]
    public void EveryCollectionListAssetsResponseModelsIsReadWhereTheOperatorsAssetListIsBuilt()
    {
        var modelled = ModelledAssetProperties();
        var interpreter = InterpretListAssetsDeclaration();
        var read = interpreter.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var property in modelled)
            Assert.True(read.Contains(property),
                $"ListAssetsResponse models a collection '{property}' that InterpretListAssets never "
                + "reads, so an asset of that schema deserializes and is then dropped before the "
                + "operator's asset list is built. Modelling a collection is not the same as "
                + $"surfacing it: without this, declaring the schema in {SupportMember} and adding a "
                + "matching [JsonPropertyName] would satisfy the other pins here while rgb-lib "
                + "accepted an asset no operator could ever list, select or send.");
    }

    [Fact]
    public void TheSharedMemberIsAnInlineLiteralArrayBuiltFreshForEachCaller()
    {
        SchemaListElements();

        var member = SupportMemberDeclaration();
        Assert.True(member.Initializer == null,
            $"{SupportMember} is initialised once rather than evaluated per call, so every caller now "
            + "shares one array instance. A caller that sorts or clears it in place would silently "
            + "change what every later wallet tells rgb-lib to accept. An expression body or a "
            + "getter that returns the array keeps each caller its own copy.");
        Assert.True(member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
            $"{SupportMember} must stay static; an instance member would need a lifetime the send "
            + "helper's static entry point cannot give it");
    }

    static HashSet<string> DeclaredSchemas() =>
        SchemaListElements()
            .Select(e => ((LiteralExpressionSyntax)e).Token.ValueText)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    static ExpressionSyntax SchemaListExpression()
    {
        var member = SupportMemberDeclaration();
        var getter = member.AccessorList?.Accessors
            .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        var returned = getter?.Body?.Statements.OfType<ReturnStatementSyntax>()
            .SingleOrDefault()?.Expression;

        var schemaList = member.ExpressionBody?.Expression
            ?? getter?.ExpressionBody?.Expression
            ?? returned
            ?? member.Initializer?.Value;

        Assert.True(schemaList != null,
            $"{SupportMember} does not yield its schemas from an expression body, a getter with a "
            + "single return, or an initialiser, so this pin cannot read what it hands to rgb-lib");
        return schemaList!;
    }

    static List<ExpressionSyntax> SchemaListElements()
    {
        var schemaList = SchemaListExpression();
        var elements = schemaList switch
        {
            CollectionExpressionSyntax c when c.Elements.All(e => e is ExpressionElementSyntax) =>
                c.Elements.Cast<ExpressionElementSyntax>().Select(e => e.Expression).ToList(),
            ArrayCreationExpressionSyntax a => a.Initializer?.Expressions.ToList(),
            ImplicitArrayCreationExpressionSyntax i => i.Initializer.Expressions.ToList(),
            _ => null
        };

        Assert.True(elements != null,
            $"{SupportMember} does not spell its schemas out as an inline array but as "
            + $"'{schemaList}'. Any indirection — a helper call, a field, a concatenation, a spread — "
            + "lets the schemas actually handed to rgb-lib differ from the ones this pin reads, which "
            + "would leave every assertion in this file describing a list the wallet never sees.");

        foreach (var element in elements!)
            Assert.True(
                element is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression),
                $"{SupportMember} names a schema as '{element}' rather than a string literal. A "
                + "computed name is unreadable both to this pin and to a reviewer at a glance, and "
                + "rgb-lib matches these against its AssetSchema enum by exact text.");

        Assert.True(elements.Count > 0,
            $"{SupportMember} names no schema at all; rgb-lib refuses an empty supported_schemas with "
            + "Error::NoSupportedSchemas, so every wallet construction would fail");
        return elements;
    }

    static PropertyDeclarationSyntax SupportMemberDeclaration()
    {
        var matches = PluginCompilation.Shared.Tree(SupportFile).GetRoot()
            .DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.ValueText == SupportMember)
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly one {SupportMember} in {SupportFile}, found {matches.Count}");
        return matches[0];
    }

    static MethodDeclarationSyntax InterpretListAssetsDeclaration()
    {
        var matches = PluginCompilation.Shared.Tree(RgbLibFile).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "InterpretListAssets")
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly one InterpretListAssets in {RgbLibFile}, found {matches.Count}; it is "
            + "where the native list_assets payload becomes the operator's asset list");
        return matches[0];
    }

    static List<string> ModelledAssetProperties() =>
        SchemaCollectionProperties().Select(p => p.Identifier.ValueText).ToList();

    // Scoped to the List<T> members on purpose: a scalar field rgb-lib might add beside them — a
    // cursor, a count, a timestamp — is not an asset collection, names no schema, and must not be
    // forced into supported_schemas or into the asset-list projection to keep this file green.
    static List<PropertyDeclarationSyntax> SchemaCollectionProperties() =>
        ListAssetsResponseDeclaration().Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => NamedTypeOf(p.Type) is { } named && named.Identifier.ValueText == "List")
            .ToList();

    static GenericNameSyntax? NamedTypeOf(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => NamedTypeOf(nullable.ElementType),
        GenericNameSyntax generic => generic,
        QualifiedNameSyntax qualified => qualified.Right as GenericNameSyntax,
        _ => null
    };

    static ClassDeclarationSyntax ListAssetsResponseDeclaration()
    {
        var declarations = PluginCompilation.Shared.Tree(RgbLibFile).GetRoot()
            .DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.ValueText == "ListAssetsResponse")
            .ToList();

        Assert.True(declarations.Count == 1,
            $"expected exactly one ListAssetsResponse in {RgbLibFile}, found {declarations.Count}");
        return declarations[0];
    }

    static HashSet<string> ModelledAssetCollections()
    {
        var properties = SchemaCollectionProperties();
        var names = properties
            .Select(JsonPropertyNameOf)
            .Where(name => name != null)
            .Select(name => name!)
            .ToList();

        Assert.True(names.Count == properties.Count,
            "a ListAssetsResponse property carries no [JsonPropertyName]; System.Text.Json would then "
            + "match it by its C# name and this pin would read the wrong set");
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    static string? JsonPropertyNameOf(PropertyDeclarationSyntax property) =>
        property.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString().EndsWith("JsonPropertyName", StringComparison.Ordinal))
            .SelectMany(a => a.ArgumentList?.Arguments ?? default)
            .Select(a => a.Expression)
            .OfType<LiteralExpressionSyntax>()
            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(l => l.Token.ValueText)
            .FirstOrDefault();

    static bool IsExactlyTheSharedMember(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText == SupportMember,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == SupportMember,
            _ => false
        };
    }

    static string? KeyOf(AssignmentExpressionSyntax assignment) =>
        assignment.Left is ImplicitElementAccessSyntax access
        && access.ArgumentList.Arguments.Count == 1
        && access.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    static IEnumerable<(string Label, SyntaxNode Root)> WalletConstructingRoots()
    {
        yield return (RgbLibFile, PluginCompilation.Shared.Tree(RgbLibFile).GetRoot());

        var helper = Path.Combine(
            PluginCompilation.RepoRootPath, "RgbRestoreHelper", "RgbNativeSend.cs");
        Assert.True(File.Exists(helper),
            "RgbRestoreHelper/RgbNativeSend.cs is missing; it constructs the send child's wallet and is "
            + "outside the plugin compile set, so it has to be parsed from disk");
        yield return ("RgbRestoreHelper/RgbNativeSend.cs",
            CSharpSyntaxTree.ParseText(File.ReadAllText(helper)).GetRoot());
    }
}
