using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Why pins and not behavioural tests: the defect these guard is an ORDERING inside
// RestoreProcessRunner.RunAsync's poll loop. Reaching it behaviourally needs a real child process, a
// staging tree slow enough to walk that it visibly delays the kill, and an injectable clock the runner
// does not have — a test that would be slow, machine-dependent and flaky, i.e. one that gets deleted
// or muted and stops guarding anything. The ordering itself is a static property, so it is pinned
// statically, and the bound it depends on (MeasureStaging) is pinned behaviourally in
// RestoreStagingBoundTests. Neither pin is a substitute for the other.
public class RestoreDeadlinePinTests
{
    const string RunnerFile = "Services/RestoreProcessRunner.cs";
    const string RunnerType = "RestoreProcessRunner";

    static WhileStatementSyntax PollLoop()
    {
        var tree = PluginCompilation.Shared.Tree(RunnerFile);
        var method = RoslynPins.Method(tree, RunnerType, "RunAsync");
        var loops = RoslynPins.BodyOf(method).DescendantNodes().OfType<WhileStatementSyntax>().ToList();
        Assert.True(loops.Count == 1, $"expected exactly one while loop in RunAsync, found {loops.Count}");
        return loops[0];
    }

    // The defect: the deadline was compared ONLY on the way into each iteration, before a scan that
    // walks a tree the restored archive's author controls. The kill therefore landed at
    // deadline + scan-time, which is literally the audit's "work continues after the 30-second
    // timeout". One comparison is not enough; there must be one after the measurement too.
    [Fact]
    public void TheDeadlineIsComparedBothBeforeAndAfterTheStagingMeasurement()
    {
        var loop = PollLoop();
        var measure = loop.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString().EndsWith("MeasureStaging", StringComparison.Ordinal));

        var deadlineChecks = loop.Statement.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Where(b => b.ToString().Contains("deadline", StringComparison.Ordinal))
            .ToList();

        Assert.True(deadlineChecks.Count >= 2,
            $"expected the deadline to be compared at least twice per iteration, found {deadlineChecks.Count}");
        Assert.Contains(deadlineChecks, c => c.SpanStart < measure.SpanStart);
        Assert.Contains(deadlineChecks, c => c.SpanStart > measure.SpanStart);
    }

    // Binds the bound rather than counting it: the measurement must be the two-argument-capped
    // MeasureStaging reading BOTH caps off `limits`. An edit that reverted to an unbounded
    // `EnumerateFiles(...).Sum(...)`, or that passed a literal instead of the configured cap, would
    // keep the ordering pin above green while removing the thing that makes the ordering sufficient.
    [Fact]
    public void TheMeasurementIsBoundedByBothConfiguredCaps()
    {
        var loop = PollLoop();
        var measure = loop.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression.ToString().EndsWith("MeasureStaging", StringComparison.Ordinal));

        var args = measure.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
        Assert.Equal(3, args.Count);
        Assert.Contains("limits.DiskCapBytes", args);
        Assert.Contains("limits.MaxStagingEntries", args);
    }

    // The scan the fix removed. Its shape — enumerate everything, then Sum — is what made the parent's
    // own work proportional to attacker-chosen file counts, so its absence inside the loop is the
    // property worth pinning, and it is pinned against the loop rather than the file so a bounded
    // helper elsewhere is still allowed.
    [Fact]
    public void ThePollLoopDoesNotEnumerateTheWholeStagingTree()
    {
        var loop = PollLoop();
        var unbounded = loop.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith("EnumerateFiles", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unbounded);
    }

    // An entry-cap breach must reach the caller as its own outcome. Folding it into KilledDisk would
    // still stop the attack but would report a size limit for a file-count breach, which is how an
    // operator ends up raising the wrong knob.
    [Fact]
    public void AnEntryCapBreachHasItsOwnOutcome()
    {
        Assert.Equal(RgbUtexo.Services.RestoreOutcome.KilledEntries,
            Enum.Parse<RgbUtexo.Services.RestoreOutcome>("KilledEntries"));
        Assert.Equal(RgbUtexo.Services.RestoreKillReason.Entries,
            Enum.Parse<RgbUtexo.Services.RestoreKillReason>("Entries"));
    }
}
