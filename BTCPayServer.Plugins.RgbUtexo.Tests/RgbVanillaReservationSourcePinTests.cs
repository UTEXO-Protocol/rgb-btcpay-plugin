using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbVanillaReservationSourcePinTests
{
    const string InspectorFile = "Services/RgbVanillaReservationInspector.cs";
    const string InspectorType = "RgbVanillaReservationInspector";
    const string SqliteOpenModeFullType = "Microsoft.Data.Sqlite.SqliteOpenMode";
    const string SqliteConnectionFullType = "Microsoft.Data.Sqlite.SqliteConnection";
    const string SqliteBuilderFullType = "Microsoft.Data.Sqlite.SqliteConnectionStringBuilder";

    const int ReadOnlyConnectionsExpectedAtLeast = 6;

    const string ReadOnlyReason =
        "rgb-lib owns rgb_lib_db. The plugin's one write into that third-party schema is the pre-existing "
        + "expired-transfer sweep in RGBWalletService.CleanupExpiredTransfersInternalAsync, which flips "
        + "expired WaitingCounterparty batch_transfer rows to Failed under the native-send parent lease; "
        + "every other plugin connection to that database, this inspector's included, is opened "
        + "SqliteOpenMode.ReadOnly. The inspector must stay read-only on its own terms: its whole purpose "
        + "is to diagnose that schema's state, and a write from a diagnostic path can corrupt the "
        + "reservation bookkeeping the wallet's coin selection depends on.";

    sealed record ConnectionAllowedToOmitReadOnly(string FileSuffix, string Owner, string Why);

    static readonly ConnectionAllowedToOmitReadOnly[] ConnectionsAllowedToOmitReadOnly =
    [
        new("/Services/RGBWalletService.cs",
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService.CleanupExpiredTransfersInternalAsync",
            "the pre-existing expired-transfer sweep — the plugin's only write into rgb-lib's schema, "
            + "grandfathered here so this clause can SEE it instead of being silently vacuous about it. "
            + "Re-scoping that write is its own review; no further site may join this list without one.")
    ];

    [Fact]
    public void EverySqliteConnectionInThePluginIsOpenedReadOnlyExceptTheOneAllowedWrite()
    {
        var plugin = PluginCompilation.Shared;
        var openModeType = plugin.Compilation.GetTypeByMetadataName(SqliteOpenModeFullType);
        var connectionType = plugin.Compilation.GetTypeByMetadataName(SqliteConnectionFullType);
        var builderType = plugin.Compilation.GetTypeByMetadataName(SqliteBuilderFullType);
        Assert.True(openModeType != null && connectionType != null && builderType != null,
            $"one of {SqliteOpenModeFullType}, {SqliteConnectionFullType}, {SqliteBuilderFullType} does "
            + "not resolve in the pin compilation, so this clause would silently adjudicate nothing");
        var readOnlyField = openModeType!.GetMembers("ReadOnly").OfType<IFieldSymbol>()
            .SingleOrDefault(f => f.HasConstantValue);
        Assert.True(readOnlyField != null,
            $"{SqliteOpenModeFullType}.ReadOnly does not resolve to a constant field, so this clause "
            + "would silently adjudicate nothing");

        var readOnly = 0;
        var exempted = new List<string>();
        foreach (var tree in plugin.AllTrees)
        {
            var model = plugin.Model(tree);
            var path = tree.FilePath.Replace('\\', '/');
            foreach (var creation in tree.GetRoot().DescendantNodes()
                         .OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (!IsOrDerivesFrom(model.GetTypeInfo(creation).Type, connectionType!)) continue;

                var why = WhyNotOpenedReadOnly(model, creation, builderType!, readOnlyField!);
                if (why == null)
                {
                    readOnly++;
                    continue;
                }

                var owner = model.GetEnclosingSymbol(creation.SpanStart);
                var ownerName = owner == null
                    ? "<no enclosing symbol>"
                    : $"{owner.ContainingType?.ToDisplayString()}.{owner.Name}";
                var allowed = ConnectionsAllowedToOmitReadOnly.FirstOrDefault(
                    a => path.EndsWith(a.FileSuffix, StringComparison.Ordinal)
                         && string.Equals(a.Owner, ownerName, StringComparison.Ordinal));
                Assert.True(allowed != null,
                    $"{tree.FilePath}: {ownerName} opens a {SqliteConnectionFullType} that is not "
                    + $"{SqliteOpenModeFullType}.ReadOnly — {why}. This clause binds to CONSTRUCTION "
                    + "SITES, not to mentions of the enum, precisely so a connection built from a raw or "
                    + "interpolated connection string — the local idiom, and therefore the likeliest "
                    + "regression — is visible to it. Set Mode = SqliteOpenMode.ReadOnly, or justify the "
                    + "site by name in ConnectionsAllowedToOmitReadOnly. " + ReadOnlyReason);
                exempted.Add(ownerName);
            }
        }

        foreach (var entry in ConnectionsAllowedToOmitReadOnly)
        {
            var claimed = exempted.Count(o => string.Equals(o, entry.Owner, StringComparison.Ordinal));
            Assert.True(claimed <= 1,
                $"{entry.Owner} opens {claimed} non-ReadOnly {SqliteConnectionFullType}s; the exemption "
                + $"covers exactly one, being {entry.Why} " + ReadOnlyReason);
        }

        Assert.True(readOnly >= ReadOnlyConnectionsExpectedAtLeast,
            $"only {readOnly} of the plugin's {SqliteConnectionFullType} constructions resolve to a "
            + $"{SqliteOpenModeFullType}.ReadOnly connection string; at least "
            + $"{ReadOnlyConnectionsExpectedAtLeast} are expected. This count is what keeps the clause "
            + "fail-closed: it decides on resolved SYMBOLS rather than on spelling, so if binding ever "
            + "stops working, or a read-only site is quietly rewritten into something this clause cannot "
            + "resolve, it reddens instead of adjudicating an empty set. " + ReadOnlyReason);

        var reopened = new List<string>();
        foreach (var tree in plugin.AllTrees)
        {
            var model = plugin.Model(tree);
            foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not MemberAccessExpressionSyntax access) continue;
                if (model.GetSymbolInfo(access).Symbol is not IPropertySymbol property) continue;
                if (!string.Equals(property.Name, "ConnectionString", StringComparison.Ordinal)) continue;
                if (!IsOrDerivesFrom(model.GetTypeInfo(access.Expression).Type, connectionType!)) continue;
                reopened.Add($"{tree.FilePath}: {assignment}");
            }
        }
        Assert.True(reopened.Count == 0,
            $"a {SqliteConnectionFullType}.ConnectionString is assigned after construction, which replaces "
            + "the string this clause adjudicated at the construction site: "
            + string.Join("; ", reopened) + ". " + ReadOnlyReason);
    }

    static bool IsOrDerivesFrom(ITypeSymbol? type, INamedTypeSymbol target)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (SymbolEqualityComparer.Default.Equals(current, target)) return true;
        return false;
    }

    static string? WhyNotOpenedReadOnly(SemanticModel model, BaseObjectCreationExpressionSyntax creation,
        INamedTypeSymbol builderType, IFieldSymbol readOnlyField)
    {
        var arguments = creation.ArgumentList?.Arguments;
        if (arguments is not { Count: 1 })
            return $"it takes {arguments?.Count ?? 0} constructor argument(s), so no connection string is "
                 + "adjudicable at the site";

        var argument = arguments.Value[0].Expression;
        if (argument is not MemberAccessExpressionSyntax access
            || model.GetSymbolInfo(access).Symbol is not IPropertySymbol connectionString
            || !string.Equals(connectionString.Name, "ConnectionString", StringComparison.Ordinal)
            || !SymbolEqualityComparer.Default.Equals(
                   model.GetTypeInfo(access.Expression).Type, builderType))
            return $"its connection string is `{argument}`, which does not resolve to the "
                 + $"ConnectionString of a {builderType.ToDisplayString()}";

        var builder = access.Expression as BaseObjectCreationExpressionSyntax;
        SyntaxNode? scope = null;
        if (builder == null)
        {
            if (model.GetSymbolInfo(access.Expression).Symbol is not ILocalSymbol local)
                return $"its connection string comes from `{access.Expression}`, which resolves to no "
                     + "local builder this clause can follow";
            var declarator = local.DeclaringSyntaxReferences.Length == 1
                ? local.DeclaringSyntaxReferences[0].GetSyntax() as VariableDeclaratorSyntax
                : null;
            if (declarator?.Initializer?.Value is not BaseObjectCreationExpressionSyntax declared)
                return $"the builder local `{local.Name}` is not initialised by a single object creation";
            builder = declared;
            scope = declarator.Ancestors().FirstOrDefault(a =>
                a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
            if (scope == null)
                return $"the builder local `{local.Name}` is declared outside any method body";
            var rebinds = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Count(a => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, local));
            if (rebinds != 0)
                return $"the builder local `{local.Name}` is reassigned {rebinds} time(s) after its "
                     + "initialiser, so the initialiser is not what the connection string comes from";
        }

        if (!SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(builder).Type, builderType))
            return $"its connection string is built by `{builder}`, which is not a "
                 + builderType.ToDisplayString();

        var modeProperty = builderType.GetMembers("Mode").OfType<IPropertySymbol>().SingleOrDefault();
        if (modeProperty == null)
            return $"{builderType.ToDisplayString()}.Mode does not resolve, so no mode is adjudicable";

        var modes = builder.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
            .Where(a => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(a.Left).Symbol, modeProperty))
            .ToList() ?? [];
        if (modes.Count != 1)
            return $"its builder assigns Mode {modes.Count} time(s) in its initialiser; exactly one is "
                 + "required, and omitting Mode defaults to ReadWriteCreate";

        if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(modes[0].Right).Symbol, readOnlyField))
            return $"its builder assigns Mode = `{modes[0].Right}`, which does not resolve to "
                 + readOnlyField.ToDisplayString();

        if (scope != null)
        {
            var overwrites = scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Count(a => a.Left is MemberAccessExpressionSyntax m
                            && SymbolEqualityComparer.Default.Equals(
                                model.GetSymbolInfo(m).Symbol, modeProperty));
            if (overwrites != 0)
                return $"Mode is assigned {overwrites} further time(s) outside the builder's initialiser, "
                     + "which overwrites the ReadOnly this clause resolved";
        }

        return null;
    }

    [Fact]
    public void EveryConnectionBuiltByTheInspectorSetsReadOnlyMode()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(InspectorFile);
        var builders = tree.GetRoot().DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString().EndsWith("SqliteConnectionStringBuilder", StringComparison.Ordinal))
            .ToList();

        Assert.True(builders.Count == 1,
            $"{InspectorFile} builds {builders.Count} SQLite connection string(s); exactly one is "
            + "expected, so every one of them is covered by the clause below. " + ReadOnlyReason);

        foreach (var builder in builders)
        {
            var mode = builder.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .SingleOrDefault(a => a.Left.ToString() == "Mode");
            Assert.True(mode != null,
                $"{InspectorFile}: a SQLite connection string omits Mode entirely, which defaults to "
                + "ReadWriteCreate. " + ReadOnlyReason);
            Assert.True(mode!.Right.ToString().EndsWith(".SqliteOpenMode.ReadOnly", StringComparison.Ordinal),
                $"{InspectorFile}: Mode is `{mode.Right}`, not SqliteOpenMode.ReadOnly. " + ReadOnlyReason);
        }
    }

    [Fact]
    public void TheInspectorNeverIssuesAWrite()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(InspectorFile);

        var commandTexts = tree.GetRoot().DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax access
                        && access.Name.Identifier.ValueText == "CommandText")
            .ToList();
        Assert.True(commandTexts.Count == 2,
            $"{InspectorFile} assigns CommandText {commandTexts.Count} time(s); exactly two are expected — "
            + "the reserved-txo read and the table-presence probe. Without this positive anchor the "
            + "absence clauses below hold vacuously over a file whose read path has been gutted or "
            + "emptied. " + ReadOnlyReason);
        foreach (var assignment in commandTexts)
        {
            var folded = plugin.Model(tree).GetConstantValue(assignment.Right);
            var sql = folded.Value as string ?? assignment.Right.ToString();
            Assert.True(sql.Contains("SELECT", StringComparison.Ordinal),
                $"{InspectorFile}: CommandText is assigned `{assignment.Right}`, resolving to `{sql}`, "
                + "which is not a SELECT. " + ReadOnlyReason);
        }

        var body = tree.GetRoot().ToString();
        foreach (var write in new[] { "INSERT", "UPDATE", "DELETE", "DROP", "CREATE TABLE", "PRAGMA" })
            Assert.True(!body.Contains(write, StringComparison.OrdinalIgnoreCase),
                $"{InspectorFile} contains '{write}'. " + ReadOnlyReason);
        foreach (var write in new[] { "ExecuteNonQuery", "BeginTransaction" })
            Assert.True(!body.Contains(write, StringComparison.Ordinal),
                $"{InspectorFile} calls '{write}'. " + ReadOnlyReason);
    }

    [Fact]
    public void TheTwoRgbLibTableNamesAreNamedExactlyOnceEach()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(InspectorFile);
        var literals = tree.GetRoot().DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.StringLiteralToken))
            .Select(t => (string?)t.Value)
            .Where(v => v is "reserved_txo" or "wallet_transaction")
            .ToList();

        Assert.True(literals.Count == 2,
            $"{InspectorFile} spells an rgb-lib table name {literals.Count} time(s) as a literal; exactly "
            + "two are expected, one per named constant. Every other use must go through the constant, so "
            + "that a rename has exactly one place to be wrong and the real-binding schema test catches "
            + "it. Found: " + string.Join(", ", literals));

        var declared = tree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.ValueText == InspectorType)
            .SelectMany(t => t.Members.OfType<FieldDeclarationSyntax>())
            .SelectMany(f => f.Declaration.Variables)
            .Where(v => v.Initializer?.Value is LiteralExpressionSyntax literal
                        && (string?)literal.Token.Value is "reserved_txo" or "wallet_transaction")
            .Select(v => v.Identifier.ValueText)
            .ToList();
        Assert.Equal(2, declared.Count);
    }
}
