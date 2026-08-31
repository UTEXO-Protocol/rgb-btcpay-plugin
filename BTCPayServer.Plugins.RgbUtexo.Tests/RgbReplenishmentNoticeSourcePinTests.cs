using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbReplenishmentNoticeSourcePinTests
{
    const string ListenerFile = "Services/RGBInvoiceListener.cs";
    const string ListenerType = "RGBInvoiceListener";
    const string Replenish = "ReplenishUtxosAsync";
    const string ControllerFile = "Controllers/RGBController.cs";
    const string RunnerFile = "Data/RGBPluginMigrationRunner.cs";
    const string NoticeServiceFile = "Services/RgbReplenishmentNoticeService.cs";
    const string GrantTable = "RGBStoreAutoReplenishments";
    const string NoticeTable = "RGBStoreNoticeStates";

    const string CooldownReason =
        "The cooldown rate-limits SIGNING ATTEMPTS, not notices. Every non-Create outcome stamps a "
        + "30-minute cooldown and the cooldown gate runs inside EvaluateReplenishEligibility, so a notice "
        + "evaluated after that call is evaluated at most once per 30 minutes — and at eight invoices an "
        + "hour the whole pre-failure window is consumed inside one cooldown, giving zero warning. Do NOT "
        + "move the notice below the eligibility call to 'restore consistency' with the rest of the sweep.";

    const string CauseCReason =
        "Cause C — an out-of-range stored RGB configuration — must be evaluated BEFORE the bounds guard's "
        + "`continue`. That guard is the earliest thing that blocks replenishment, so a notice evaluated "
        + "after it never fires for the population it exists to serve, and the operator is instead told "
        + "cause A: that granting standing authorization is the remedy. It is not — the grant is refused "
        + "at that same guard and Create UTXOs throws. The real remedy, re-saving the settings, is named "
        + "nowhere else.";

    static IReadOnlyList<string> DedupeMarkerPropertyNames()
    {
        var causes = Enum.GetValues<RgbReplenishmentNoticeCause>()
            .Where(cause => cause != RgbReplenishmentNoticeCause.None)
            .ToList();
        var markers = typeof(RGBStoreNoticeState).GetProperties()
            .Where(p => p.PropertyType == typeof(DateTimeOffset?))
            .Select(p => p.Name)
            .ToList();
        Assert.True(markers.Count == causes.Count,
            $"{nameof(RGBStoreNoticeState)} exposes {markers.Count} nullable-timestamp dedupe marker "
            + $"propert(ies) ({string.Join(", ", markers)}) but {nameof(RgbReplenishmentNoticeCause)} has "
            + $"{causes.Count} non-None cause(s) ({string.Join(", ", causes)}). The marker names below are "
            + "resolved from the CLR properties rather than written as literals, precisely so that "
            + "renaming a property cannot silently reduce this pin to a tautology. If the shape genuinely "
            + "changed, keep one marker per cause; do not list names here.");
        return markers;
    }

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };

    static List<InvocationExpressionSyntax> Named(SyntaxNode scope, string name) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == name)
            .ToList();

    static InvocationExpressionSyntax Single(SyntaxNode scope, string name, string where)
    {
        var found = Named(scope, name);
        Assert.True(found.Count == 1,
            $"{where}: expected exactly one '{name}' invocation, found {found.Count}");
        return found[0];
    }

    static string EnclosingMember(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? "<none>";

    static MethodDeclarationSyntax ReplenishMethod() =>
        RoslynPins.Method(PluginCompilation.Shared.Tree(ListenerFile), ListenerType, Replenish);

    static IfStatementSyntax BoundsGuardWithContinue(MethodDeclarationSyntax replenish)
    {
        var guards = RoslynPins.BodyOf(replenish).DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(s => s.Condition.ToString().Contains("ArePaymentMethodValuesValid", StringComparison.Ordinal)
                        && s.Statement.DescendantNodesAndSelf().OfType<ContinueStatementSyntax>().Any())
            .ToList();
        Assert.True(guards.Count == 1,
            $"{Replenish}: expected exactly one out-of-range stored-config guard that `continue`s, found "
            + $"{guards.Count}. " + CauseCReason);
        return guards[0];
    }

    [Fact]
    public void NoticeIsEvaluatedInsideTheSweepBeforeTheBoundsContinueAndBeforeTheEligibilityCall()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ListenerFile);
        var replenish = ReplenishMethod();

        RoslynPins.AssertNoLocalShadow(replenish, "Evaluate", "RaiseOncePerCauseAsync", "LogsPerSweep");

        var evaluate = Single(replenish, "Evaluate", Replenish);
        RoslynPins.AssertBindsToMemberOf(plugin, tree, evaluate.Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RgbReplenishmentNotice", "Evaluate", Replenish);
        Assert.True(EnclosingMember(evaluate) == Replenish,
            $"the notice must be evaluated inside {ListenerType}.{Replenish}; it is evaluated in "
            + $"'{EnclosingMember(evaluate)}'. " + CooldownReason);

        var boundsGuard = BoundsGuardWithContinue(replenish);
        Assert.True(evaluate.SpanStart < boundsGuard.SpanStart,
            $"{Replenish}: the notice must be evaluated BEFORE the out-of-range stored-config guard that "
            + $"`continue`s. " + CauseCReason);

        var eligibility = Single(replenish, "EvaluateReplenishEligibility", Replenish);
        Assert.True(evaluate.SpanStart < eligibility.SpanStart,
            $"{Replenish}: the notice must be evaluated BEFORE the EvaluateReplenishEligibility "
            + $"invocation. " + CooldownReason);

        var raise = Single(replenish, "RaiseOncePerCauseAsync", Replenish);
        Assert.True(raise.SpanStart > evaluate.SpanStart && raise.SpanStart < boundsGuard.SpanStart,
            $"{Replenish}: the push notification must be raised from the same place the cause is "
            + $"evaluated, above the bounds guard. " + CauseCReason);
    }

    [Fact]
    public void TheNoticePredicateConsultsNoPoolArithmetic()
    {
        var replenish = ReplenishMethod();
        var evaluate = Single(replenish, "Evaluate", Replenish);
        var arguments = evaluate.ArgumentList.ToString();

        foreach (var banned in new[]
                 {
                     "freeSlots", "minFreeSlots", "colorableCount", "usedByColorings",
                     "activePendingInvoices", "EvaluateReplenishDemand", "decision"
                 })
            Assert.True(!arguments.Contains(banned, StringComparison.Ordinal),
                $"{Replenish}: the notice predicate must be arithmetic-free — it took '{banned}'. A "
                + "demand-derived predicate fires only in the last three of forty slots, at the END of "
                + "the drain window, and only once per 30-minute cooldown. That is the defect the "
                + "arithmetic-free predicate exists to remove.");
    }

    [Fact]
    public void TheWarningLogIsGatedOnTheCauseThatMayBeLoggedPerSweep()
    {
        var replenish = ReplenishMethod();
        var logs = Named(replenish, "LogWarning");
        var gated = logs.Where(l => l.Ancestors().OfType<IfStatementSyntax>()
                .Any(s => s.Condition.ToString().Contains("LogsPerSweep", StringComparison.Ordinal)))
            .ToList();
        Assert.True(gated.Count == 1,
            $"{Replenish}: exactly one LogWarning must be gated on RgbReplenishmentNotice.LogsPerSweep, "
            + $"found {gated.Count}. Logging a deliberate deployment-wide cap of 0, or an out-of-range "
            + "stored config, once per sweep forever for every wallet is the noise the existing Debug "
            + "line's own comment warns about; and logging cause A at Debug is not an operator surface "
            + "at all.");
    }

    [Fact]
    public void AllThreeNoticeSurfacesExist()
    {
        var plugin = PluginCompilation.Shared;

        var push = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => NameOf(i) == "SendNotification"
                        && i.ArgumentList.Arguments.ToString()
                            .Contains("RgbReplenishmentBlockedNotification", StringComparison.Ordinal))
            .ToList();
        Assert.True(push.Count == 1,
            "the PUSH surface is the only one that reaches an operator who never opens RGB pages: it is "
            + "sent with StoreScope, so it fans out to every user of the store. Exactly one raise site is "
            + $"expected, found {push.Count}.");
        Assert.True(push[0].ArgumentList.Arguments[0].ToString().Contains("StoreScope", StringComparison.Ordinal),
            "the notification must be store-scoped, or it reaches one user instead of every user of the store");

        var handlerRegistrations = plugin.Tree(RoslynPins.PluginFile).GetRoot().ToString();
        Assert.Contains("RgbReplenishmentBlockedNotification.Handler", handlerRegistrations,
            StringComparison.Ordinal);

        var settingsView = Path.Combine(PluginCompilation.RepoRootPath, "Views/RGB/Settings.cshtml");
        Assert.True(File.Exists(settingsView), $"{settingsView} is missing");
        var view = File.ReadAllText(settingsView);
        Assert.Contains("ReplenishmentNoticeCause", view, StringComparison.Ordinal);
        Assert.Contains("ReplenishmentNoticeMessage", view, StringComparison.Ordinal);
        Assert.Contains("SetAutomaticReplenishmentAuthorization", view, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheDedicatedOperatorActionWritesTheGrantTable()
    {
        var plugin = PluginCompilation.Shared;
        var writers = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Where(m => m.Name.Identifier.ValueText == GrantTable)
                .Select(m => (tree, member: (SyntaxNode)m)))
            .ToList();

        Assert.True(writers.Count > 0, $"nothing in the plugin touches {GrantTable} at all");

        var byMember = writers
            .Select(w => (File: w.tree.FilePath, Member: EnclosingMember(w.member)))
            .Distinct()
            .ToList();

        foreach (var (file, member) in byMember)
        {
            Assert.True(!file.EndsWith(RunnerFile, StringComparison.Ordinal),
                $"{RunnerFile} touches {GrantTable} in '{member}'. That runner is an IStartupTask whose "
                + "data passes run on EVERY boot with no one-time gate, so any pass of the form 'RGB "
                + "enabled + no row => Granted' grants standing unattended-signing authority, at the next "
                + "restart, to every store created after the upgrade — the exact population that must "
                + "refuse. 'Never decided' is encoded as absence, and absence is precisely such a pass's "
                + "trigger. There must be no migration pass at all.");
            Assert.True(member != "SaveSettings",
                $"SaveSettings touches {GrantTable}. The load-bearing invariant is 'only a deliberate "
                + "operator action writes the decision', not 'any settings save writes the decision'.");
        }

        var controllerMembers = byMember
            .Where(x => x.File.EndsWith(ControllerFile, StringComparison.Ordinal))
            .Select(x => x.Member)
            .ToList();
        Assert.True(controllerMembers.Count == 0,
            $"{ControllerFile} touches {GrantTable} directly in {string.Join(", ", controllerMembers)}; it "
            + "must go through RgbAutoReplenishmentAuthorizationStore so the write has exactly one home.");
    }

    [Fact]
    public void TheGrantDecisionIsWrittenFromExactlyOneMember()
    {
        var plugin = PluginCompilation.Shared;
        var writes = plugin.AllTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => NameOf(i) == "RecordDecisionAsync")
                .Select(i => (tree, node: (SyntaxNode)i)))
            .ToList();

        Assert.True(writes.Count == 1,
            "the standing authorization must be written from exactly one place, found "
            + string.Join(", ", writes.Select(w => $"{EnclosingMember(w.node)} in {w.tree.FilePath}")));
        Assert.True(EnclosingMember(writes[0].node) == "SetAutomaticReplenishmentAuthorization",
            "the only writer must be the dedicated operator POST action, not the settings save and not "
            + $"anything automatic; it is '{EnclosingMember(writes[0].node)}'.");
    }

    [Fact]
    public void TheMigrationRunnerNamesNeitherNewTable()
    {
        var text = PluginCompilation.Shared.Tree(RunnerFile).GetRoot().ToString();
        foreach (var name in new[]
                 {
                     GrantTable, NoticeTable,
                     "RGB_StoreAutoReplenishment", "RGB_StoreNoticeState",
                     "RgbAutoReplenishmentDecision"
                 })
            Assert.True(!text.Contains(name, StringComparison.Ordinal),
                $"{RunnerFile} mentions '{name}'. It runs on every boot with no one-time gate; it must "
                + "not be modified at all by this work.");
    }

    [Fact]
    public void ThePullSurfaceIsDerivedFromTheConditionAndReadsNoDedupeMarker()
    {
        var plugin = PluginCompilation.Shared;
        var controller = plugin.Tree(ControllerFile).GetRoot().ToString();
        Assert.True(!controller.Contains(NoticeTable, StringComparison.Ordinal),
            $"{ControllerFile} reads {NoticeTable}. The settings alert exists so that a dismissed or "
            + "missed push notification is not a silent outage, so it must be recomputed from the "
            + "predicate on every render and must never be dedupe-gated.");
        var noticeService = plugin.Tree(NoticeServiceFile).GetRoot().ToString();
        foreach (var marker in DedupeMarkerPropertyNames())
        {
            Assert.True(noticeService.Contains(marker, StringComparison.Ordinal),
                $"'{marker}' is a {nameof(RGBStoreNoticeState)} dedupe marker that {NoticeServiceFile} "
                + "never names. This pin resolves the marker names from the CLR properties so that a "
                + "rename cannot turn it into a tautology; a marker no longer read or stamped anywhere "
                + "means the resolved set has drifted away from the real markers and the controller check "
                + "below would be checking nothing.");
            Assert.True(!controller.Contains(marker, StringComparison.Ordinal),
                $"{ControllerFile} reads the '{marker}' dedupe marker; the pull surface must be a pure "
                + "function of current state.");
        }

        var evaluate = plugin.Tree(ControllerFile).GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "Evaluate"
                        && i.Expression.ToString().Contains("RgbReplenishmentNotice", StringComparison.Ordinal))
            .ToList();
        Assert.True(evaluate.Count == 1,
            $"{ControllerFile} must evaluate the notice cause exactly once, from the same predicate the "
            + $"sweep uses, found {evaluate.Count}");
    }
}
