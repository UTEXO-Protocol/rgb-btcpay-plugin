using System.Linq;
using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbConfigBoundsTests
{
    // WHY the FULL display name is the discriminator and never ContainingType.Name: a
    // `static class RgbConfigBounds` declared in the plugin's own Models, Data or PaymentHandler
    // namespace beats the using-imported Services one by C# name resolution — the hole
    // PluginSourcePins.cs documents in its header. Every consumer would then read the shadow's values
    // while a short-name comparison stayed green, restoring the finding with a green suite.
    const string BoundsType = "BTCPayServer.Plugins.RgbUtexo.Services.RgbConfigBounds";

    static void AssertBindsToBound(PluginCompilation plugin, SyntaxTree tree,
        ExpressionSyntax expression, string expected, string what)
    {
        var access = Assert.IsType<MemberAccessExpressionSyntax>(expression);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
        Assert.True(symbol.Name == expected && symbol.ContainingType?.ToDisplayString() == BoundsType,
            $"{what} must bind to {BoundsType}.{expected}, found "
            + $"{symbol.ContainingType?.ToDisplayString()}.{symbol.Name}");
    }

    // WHY these exact values: they are what the UI has always enforced. This change moves the
    // enforcement point, it does not introduce a new limit.
    [Fact]
    public void BoundsMatchTheLimitsTheUiHasAlwaysEnforced()
    {
        Assert.Equal(1, RgbConfigBounds.UtxoCountMin);
        Assert.Equal(20, RgbConfigBounds.UtxoCountMax);
        Assert.Equal(546, RgbConfigBounds.UtxoSizeMin);
        Assert.Equal(100_000, RgbConfigBounds.UtxoSizeMax);
        Assert.Equal(1, RgbConfigBounds.AllocationsPerUtxoMin);
        Assert.Equal(50, RgbConfigBounds.AllocationsPerUtxoMax);
        Assert.Equal(1, RgbConfigBounds.MinConfirmationsMin);
        Assert.Equal(100, RgbConfigBounds.MinConfirmationsMax);
    }

    [Theory]
    [InlineData(1, 546, 1, true)]
    [InlineData(20, 100_000, 100, true)]
    [InlineData(0, 546, 1, false)]
    [InlineData(21, 546, 1, false)]
    [InlineData(1, 545, 1, false)]
    [InlineData(1, 100_001, 1, false)]
    [InlineData(1, 546, 0, false)]
    [InlineData(1, 546, 101, false)]
    public void PersistedConfigurationIsRevalidatedAtRuntime(
        int count, int size, int confirmations, bool expected)
    {
        Assert.Equal(expected,
            RgbConfigBounds.ArePaymentMethodValuesValid(count, size, confirmations));
        if (expected)
            RgbConfigBounds.EnsurePaymentMethodValuesValid(count, size, confirmations);
        else
            Assert.Throws<InvalidOperationException>(() =>
                RgbConfigBounds.EnsurePaymentMethodValuesValid(count, size, confirmations));
    }

    [Fact]
    public void LegacyPersistedValuesAreGuardedAtEveryOperationalSink()
    {
        var handler = ReadRepoFile(Path.Combine("PaymentHandler", "RGBPaymentMethodHandler.cs"));
        var controller = ReadRepoFile(Path.Combine("Controllers", "RGBController.cs"));
        var listener = ReadRepoFile(Path.Combine("Services", "RGBInvoiceListener.cs"));

        Assert.Contains("RgbConfigBounds.EnsurePaymentMethodValuesValid(", handler,
            StringComparison.Ordinal);
        Assert.Contains("RgbConfigBounds.EnsurePaymentMethodValuesValid(", controller,
            StringComparison.Ordinal);
        var validation = listener.IndexOf(
            "RgbConfigBounds.ArePaymentMethodValuesValid(", StringComparison.Ordinal);
        var nativeWork = listener.IndexOf("ListUnspentsAsync(w.Id, ct)", StringComparison.Ordinal);
        Assert.True(validation >= 0 && nativeWork > validation,
            "automatic replenishment must reject legacy invalid config before native wallet work");
    }

    // WHY a string-content assertion: the bound values live inside a SQL string literal, which a
    // numeric-literal sweep of the syntax tree cannot see.
    // NOTE this pin IS deliberately sensitive to the statement's text — it counts the @p0/@p1
    // placeholders, pins which SQL position each of them occupies, and forbids any surviving bare bound
    // literal. If the statement is reformatted,
    // update this pin deliberately — do not weaken it.
    [Fact]
    public void MigrationSqlReadsTheAllocationBoundsFromTheConstants()
    {
        var src = ReadRepoFile(Path.Combine("Data", "RGBPluginMigrationRunner.cs"));

        // Both constants must be referenced...
        Assert.Contains("RgbConfigBounds.AllocationsPerUtxoMin", src);
        Assert.Contains("RgbConfigBounds.AllocationsPerUtxoMax", src);

        // ...and the statement must use the parameter placeholders in ALL FOUR bound positions.
        // WHY assert the placeholders rather than only the absence of one literal: forbidding just
        // GREATEST("MaxAllocationsPerUtxo", 1) leaves re-hardcoding 50 in LEAST and in the WHERE
        // clause green while retaining an unused constant argument.
        // WHY the offsets are asserted before they are used: both are IndexOf results, and a change to
        // the LITERAL'S FORM — a raw string rewritten as an equivalent escaped literal, say — makes them
        // -1, at which point the slice throws ArgumentOutOfRangeException and the developer learns
        // nothing about why. The pin is deliberately coupled to the text (see the note above); the
        // failure it produces when that text moves must still say so out loud.
        const string FormChanged =
            "the SQL literal's form changed, so this text pin can no longer locate the statement — "
            + "re-point it at the new form deliberately; do NOT weaken what it asserts";

        var start = src.IndexOf("UPDATE \"RGB_Wallets\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find the UPDATE statement: {FormChanged}");

        var statement = src[start..];
        var end = statement.IndexOf("\"\"\"", StringComparison.Ordinal);
        Assert.True(end >= 0, $"could not find the raw-string terminator: {FormChanged}");

        statement = statement[..end];

        Assert.Equal(2, Regex.Matches(statement, @"@p0\b").Count);
        Assert.Equal(2, Regex.Matches(statement, @"@p1\b").Count);
        Assert.DoesNotMatch(new Regex(@"\b(1|50)\b"), statement);

        // WHY each placeholder's POSITION relative to the SQL structure is asserted and not merely how
        // many times it occurs: swapping @p0 and @p1 inside this TEXT — leaving the object[] untouched —
        // inverts the clamp to LEAST(GREATEST(x, 50), 1) and pins every existing wallet row to 1. The
        // counts above stay 2 and 2, and the semantic sibling below pins only the ARRAY's element order,
        // so nothing else in the suite sees it; the unit suite has no database, so there is no
        // behavioural backup either. @p0 is the FLOOR (GREATEST's second argument, and the right-hand
        // side of the `<` test); @p1 is the CEILING (LEAST's second argument, and the right-hand side of
        // `>`). Together with the counts, every occurrence of both placeholders is accounted for.
        // WHY \s* between every token: a pure reflow of the statement must stay green.
        foreach (var (structure, what) in new[]
                 {
                     (@"SET\s+""MaxAllocationsPerUtxo""\s*=\s*LEAST\(\s*GREATEST\(\s*"
                      + @"""MaxAllocationsPerUtxo""\s*,\s*@p0\s*\)\s*,\s*@p1\s*\)",
                      "the clamp must read LEAST(GREATEST(column, @p0), @p1) — @p0 is the floor and "
                      + "@p1 the ceiling; swapping them forces every row to the minimum"),
                     (@"WHERE\s+""MaxAllocationsPerUtxo""\s*<\s*@p0\b",
                      "the WHERE clause must select rows BELOW the floor with @p0"),
                     (@"""MaxAllocationsPerUtxo""\s*>\s*@p1\b",
                      "the WHERE clause must select rows ABOVE the ceiling with @p1"),
                     // WHY the CONNECTIVE is pinned and not just the two comparisons: the tests above are
                     // satisfied by `… < @p0 AND … > @p1`, which no row can satisfy, so the UPDATE
                     // touches nothing and the clamp silently stops running on existing wallet rows —
                     // with the placeholder counts, the clamp expression and the semantic sibling pin all
                     // still green, and no database in the unit suite to notice.
                     (@"WHERE\s+""MaxAllocationsPerUtxo""\s*<\s*@p0\s+OR\s+""MaxAllocationsPerUtxo""\s*>\s*@p1\b",
                      "the two bound tests must be joined by OR — a row is out of range if it is EITHER "
                      + "below the floor or above the ceiling, and AND selects no row at all")
                 })
            Assert.True(Regex.IsMatch(statement, structure), $"{what}. Statement was: {statement}");
    }

    // WHY pin the ORDER semantically: the assertions above are all satisfied by
    // new object[] { AllocationsPerUtxoMax, AllocationsPerUtxoMin }, which inverts the clamp to
    // LEAST(GREATEST(x, 50), 1) and forces every wallet row to 1 — a silent data change with every
    // other pin green. @p0 must be Min and @p1 must be Max.
    [Fact]
    public void MigrationSqlPassesTheBoundsInTheRightOrder()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Data/RGBPluginMigrationRunner.cs");
        var method = RoslynPins.Method(tree, "RGBPluginMigrationRunner", "ExecuteAsync");

        // WHY the array is reached THROUGH the call and not found free-floating in the body: any
        // two-element array of member accesses used to satisfy this pin, so a decoy
        // `new object[] { Min, Max }` assigned to an unused local kept it green while the real
        // invocation passed `new object[] { 0, 1000 }` and left every row outside 1..50. The
        // discriminator has to be the SQL the parameters are bound to.
        var call = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText
                             == "ExecuteSqlRawAsync"
                      && i.ArgumentList.Arguments.Count > 0
                      && i.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal
                      && literal.Token.ValueText.Contains("UPDATE \"RGB_Wallets\"", StringComparison.Ordinal));

        // WHY the CALLEE is bound and asserted by full display name: the Single above selected it by
        // spelling only. ExecuteSqlRawAsync is itself an extension method, so a static class in the
        // *enclosing* BTCPayServer.Plugins.RgbUtexo.Data namespace declaring
        // ExecuteSqlRawAsync(this DatabaseFacade, string, object[], CancellationToken) wins extension
        // lookup over the file-level `using Microsoft.EntityFrameworkCore`, receives the same SQL and
        // the same bounds array, and clamps nothing — with every assertion below still green.
        var access = Assert.IsType<MemberAccessExpressionSyntax>(call.Expression);
        var callee = Assert.IsAssignableFrom<IMethodSymbol>(RoslynPins.BoundSymbol(plugin, tree, access));
        Assert.Equal("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions",
            callee.ContainingType?.ToDisplayString());

        // ...and the RECEIVER, so the statement cannot be aimed at some other DatabaseFacade-shaped
        // object: `new FakeContext().Database.ExecuteSqlRawAsync(sameSql, sameArray, ct)` binds the same
        // extension method while touching no real database.
        var receiver = Assert.IsType<MemberAccessExpressionSyntax>(access.Expression);
        var receiverSymbol = RoslynPins.BoundSymbol(plugin, tree, receiver);
        Assert.Equal("Database", receiverSymbol.Name);
        Assert.Equal("Microsoft.EntityFrameworkCore.DbContext",
            receiverSymbol.ContainingType?.ToDisplayString());
        Assert.Equal(SymbolKind.Local, RoslynPins.BoundSymbol(
            plugin, tree, Assert.IsType<IdentifierNameSyntax>(receiver.Expression)).Kind);

        // Three arguments exactly: sql, parameters, cancellationToken. The (sql, cancellationToken)
        // overload also compiles here, and it would drop the bound parameters altogether.
        Assert.Equal(3, call.ArgumentList.Arguments.Count);

        var array = Assert.IsType<ArrayCreationExpressionSyntax>(call.ArgumentList.Arguments[1].Expression);
        Assert.NotNull(array.Initializer);
        Assert.Equal(2, array.Initializer!.Expressions.Count);

        foreach (var (index, expected) in new[]
                 { (0, "AllocationsPerUtxoMin"), (1, "AllocationsPerUtxoMax") })
            AssertBindsToBound(plugin, tree, array.Initializer!.Expressions[index], expected,
                $"parameter @p{index}");
    }

    // WHY hand-rolled rather than a RoslynPins helper: RoslynPins has no attribute-argument assertion,
    // and RoslynPins.Method only matches MethodDeclarationSyntax, so it cannot reach a property's
    // attribute list. BoundSymbol is the binding primitive; this mirrors the local helper
    // AssertArgumentBindsTo in RgbListenerSourcePinTests.
    [Theory]
    [InlineData("RGBSetupViewModel", "MaxAllocationsPerUtxo", "AllocationsPerUtxoMin", "AllocationsPerUtxoMax")]
    [InlineData("RGBSettingsViewModel", "UtxoCount", "UtxoCountMin", "UtxoCountMax")]
    [InlineData("RGBSettingsViewModel", "UtxoSize", "UtxoSizeMin", "UtxoSizeMax")]
    [InlineData("RGBSettingsViewModel", "MinConfirmations", "MinConfirmationsMin", "MinConfirmationsMax")]
    public void RangeAttributeArgumentsBindToRgbConfigBounds(
        string typeName, string propertyName, string minConst, string maxConst)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Models/RGBViewModels.cs");

        var property = tree.GetRoot().DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Identifier.ValueText == typeName)
            .SelectMany(t => t.Members.OfType<PropertyDeclarationSyntax>())
            .Single(m => m.Identifier.ValueText == propertyName);

        var range = property.AttributeLists
            .SelectMany(a => a.Attributes)
            .Single(a => a.Name.ToString() == "Range");

        // WHY the attribute ITSELF is bound: the line above picks it by spelling, and a non-validation
        // RangeAttribute declared in the plugin's own namespace beats the using-imported one by C# name
        // resolution — the arguments below would still bind to the right constants while MVC model
        // validation stopped enforcing anything and the Settings UI persisted out-of-range values.
        // BoundSymbol on an AttributeSyntax yields the constructor, so its containing type is the check.
        var constructor = Assert.IsAssignableFrom<IMethodSymbol>(
            RoslynPins.BoundSymbol(plugin, tree, range));
        Assert.Equal("System.ComponentModel.DataAnnotations.RangeAttribute",
            constructor.ContainingType?.ToDisplayString());

        var args = range.ArgumentList!.Arguments;
        Assert.Equal(2, args.Count);

        foreach (var (arg, expected) in args.Zip(new[] { minConst, maxConst }))
            AssertBindsToBound(plugin, tree, arg.Expression, expected,
                $"{typeName}.{propertyName} [Range]");
    }

    [Theory]
    [InlineData("MinAllocationsPerUtxo", "AllocationsPerUtxoMin")]
    [InlineData("MaxAllocationsPerUtxoLimit", "AllocationsPerUtxoMax")]
    public void WalletServiceAllocationConstsAliasRgbConfigBounds(string constName, string expected)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");

        var declarator = tree.GetRoot().DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Single(v => v.Identifier.ValueText == constName);

        // RGBWalletService.cs shares the Services namespace with the real type, so no *namespace*
        // shadow is constructible here — but a type named RgbConfigBounds nested in RGBWalletService
        // (or in a base type) still wins over the namespace member, so the display name is asserted
        // for the same reason as everywhere else.
        AssertBindsToBound(plugin, tree, declarator.Initializer!.Value, expected,
            $"{constName}");
    }

    // WHY text and not Roslyn: .cshtml cannot be compiled by the pin harness (precedent
    // SetupViewContentTests). Recorded as text coverage only — defeatable by reformatting.
    [Theory]
    [InlineData("Views/RGB/Settings.cshtml")]
    [InlineData("Views/RGB/Setup.cshtml")]
    // NOTE: pass the path with forward slashes; ReadRepoFile feeds it to Path.Combine, which
    // tolerates them on both macOS and Linux.
    public void ViewsCarryNoBareBoundLiteral(string path)
    {
        var view = ReadRepoFile(path);

        foreach (var bad in new[] { "min=\"1\"", "max=\"20\"", "min=\"546\"", "max=\"100000\"", "max=\"50\"", "max=\"100\"" })
            Assert.DoesNotContain(bad, view);

        foreach (var bad in new[] { "(1-20)", "(min 546)", "(1-50)", "(1-100)" })
            Assert.DoesNotContain(bad, view);
    }

    // WHY here: this file needs it too, and the settings-read-only tests keep their own copy in a
    // different class.
    internal static string ReadRepoFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        Assert.True(File.Exists(path), $"Could not locate {relativePath} at {path}");
        return File.ReadAllText(path);
    }
}
