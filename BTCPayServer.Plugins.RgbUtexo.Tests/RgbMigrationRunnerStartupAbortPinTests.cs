using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// RGBPluginMigrationRunner is an IStartupTask, and BTCPay's StartWithTasksAsync awaits every startup
/// task before host.StartAsync. An exception out of ExecuteAsync therefore stops the whole server, not
/// just the RGB plugin, and it repeats on every restart — with no web UI left through which an operator
/// could repair the data that caused it. The store-wallet unique index is the one statement in
/// ExecuteAsync that can be refused by pre-existing rows, so it must never run unguarded.
/// </summary>
public class RgbMigrationRunnerStartupAbortPinTests
{
    const string RunnerFile = "Data/RGBPluginMigrationRunner.cs";
    const string RunnerType = "RGBPluginMigrationRunner";
    const string Startup = "ExecuteAsync";
    const string Hardening = "HardenStoreWalletUniquenessAsync";
    const string RawSql = "ExecuteSqlRawAsync";
    const string Critical = "LogCritical";
    const string UniqueIndexMarker = "UNIQUE INDEX";

    const string WhyItMatters =
        "A startup task that throws takes every store on the instance offline and is repairable only "
        + "with direct database access. The unique-index hardening must report and continue, never abort.";

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        _ => string.Empty
    };

    static IEnumerable<SyntaxNode> ExcludingLambdas(SyntaxNode scope)
    {
        var pending = new Stack<SyntaxNode>();
        pending.Push(scope);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            foreach (var child in node.ChildNodes())
            {
                if (child is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) continue;
                pending.Push(child);
            }
        }
    }

    static string SqlTextOf(PluginCompilation plugin, SyntaxTree tree, ExpressionSyntax expression)
    {
        var constant = plugin.Model(tree).GetConstantValue(expression);
        return constant is { HasValue: true, Value: string text } ? text : expression.ToString();
    }

    static bool GuardedByACriticalLoggingCatch(SyntaxNode node)
    {
        foreach (var tryStatement in node.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!tryStatement.Block.Span.Contains(node.Span)) continue;
            if (tryStatement.Catches.Count == 0) continue;
            if (tryStatement.Catches.All(clause =>
                    clause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                        .Any(invocation => NameOf(invocation) == Critical)))
                return true;
        }
        return false;
    }

    [Fact]
    public void TheUniqueIndexDdl_IsNeverExecutedFromTheStartupTaskItself()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RunnerFile);
        var startup = RoslynPins.Method(tree, RunnerType, Startup);

        var unguarded = ExcludingLambdas(startup)
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => NameOf(invocation) == RawSql)
            .Where(invocation => invocation.ArgumentList.Arguments.Count > 0)
            .Where(invocation => SqlTextOf(plugin, tree, invocation.ArgumentList.Arguments[0].Expression)
                .Contains(UniqueIndexMarker, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(unguarded.Count == 0,
            $"{RunnerFile}: {Startup} executes the store-wallet unique-index DDL directly. A database that "
            + "already holds two active RGB wallets for one store refuses CREATE UNIQUE INDEX, the exception "
            + $"escapes {Startup}, and BTCPay never finishes starting. {WhyItMatters} Found at: "
            + string.Join(", ", unguarded.Select(invocation =>
                $"line {tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1}")));
    }

    [Fact]
    public void TheStartupTask_ReachesTheUniqueIndexOnlyThroughTheHardeningGuard()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RunnerFile);
        var startup = RoslynPins.Method(tree, RunnerType, Startup);

        Assert.True(
            ExcludingLambdas(startup).OfType<InvocationExpressionSyntax>()
                .Any(invocation => NameOf(invocation) == Hardening),
            $"{RunnerFile}: {Startup} no longer calls {Hardening}, so nothing installs the unique index that "
            + "closes the concurrent wallet-create race for a store. " + WhyItMatters);
    }

    [Fact]
    public void TheHardening_SwallowsEveryDatabaseFaultAndLogsItCritical()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RunnerFile);
        var hardening = RoslynPins.Method(tree, RunnerType, Hardening);

        var delegateParameters = hardening.ParameterList.Parameters
            .Where(parameter => parameter.Type is GenericNameSyntax { Identifier.ValueText: "Func" })
            .Select(parameter => parameter.Identifier.ValueText)
            .ToList();

        Assert.True(delegateParameters.Count == 2,
            $"{RunnerFile}: {Hardening} is expected to take the duplicate probe and the index creation as "
            + $"injected delegates so both can be faulted in a test without a database; found "
            + $"{delegateParameters.Count}");

        foreach (var parameter in delegateParameters)
        {
            var invocations = ExcludingLambdas(hardening).OfType<InvocationExpressionSyntax>()
                .Where(invocation => invocation.Expression is IdentifierNameSyntax name
                                     && name.Identifier.ValueText == parameter)
                .ToList();
            Assert.True(invocations.Count > 0,
                $"{RunnerFile}: {Hardening} never invokes '{parameter}'");
            foreach (var invocation in invocations)
                Assert.True(GuardedByACriticalLoggingCatch(invocation),
                    $"{RunnerFile}: {Hardening} invokes '{parameter}' outside a try whose every catch clause "
                    + $"calls {Critical}. A fault there would escape {Startup}. " + WhyItMatters);
        }

        var throws = ExcludingLambdas(hardening).OfType<ThrowStatementSyntax>().ToList();
        Assert.True(throws.Count == 0,
            $"{RunnerFile}: {Hardening} contains {throws.Count} throw statement(s). The hardening reports a "
            + $"weakened invariant through {Critical} and returns false; throwing turns a pre-existing data "
            + "condition into an unbootable server. " + WhyItMatters);
    }
}
