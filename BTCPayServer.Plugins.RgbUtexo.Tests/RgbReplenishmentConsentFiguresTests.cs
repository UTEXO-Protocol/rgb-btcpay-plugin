using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Models;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbReplenishmentConsentFiguresTests
{
    const string ControllerFile = "Controllers/RGBController.cs";
    const string ControllerType = "RGBController";
    const string Populate = "PopulateSettingsViewModel";
    const string GrantAction = "SetAutomaticReplenishmentAuthorization";
    const string PersistedGate = "HasPersistedReplenishmentFigures";

    static readonly string[] ConsentFigureMembers =
    [
        "PersistedUtxoCount",
        "PersistedUtxoSize",
        "WorstCaseReplenishFeeBaseSats",
        "WorstCaseReplenishFeePerVanillaUtxoSats"
    ];

    static RGBPaymentMethodConfig Config(int utxoCount, int utxoSize, int minConfirmations = 1) =>
        new() { UtxoCount = utxoCount, UtxoSize = utxoSize, MinConfirmations = minConfirmations };

    [Fact]
    public void NoPersistedConfig_HasNoStatableFigures()
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(null));

    [Theory]
    [InlineData(RgbConfigBounds.UtxoCountMin, RgbConfigBounds.UtxoSizeMin)]
    [InlineData(RgbConfigBounds.UtxoCountMax, RgbConfigBounds.UtxoSizeMax)]
    [InlineData(4, 1000)]
    public void InRangePersistedConfig_HasStatableFigures(int utxoCount, int utxoSize)
        => Assert.True(RGBController.ArePersistedReplenishmentFiguresValid(Config(utxoCount, utxoSize)));

    [Theory]
    [InlineData(0, 100_000)]
    [InlineData(-3, 100_000)]
    [InlineData(RgbConfigBounds.UtxoCountMax + 1, 100_000)]
    [InlineData(20, 0)]
    [InlineData(20, RgbConfigBounds.UtxoSizeMax + 1)]
    public void OutOfRangePersistedConfig_HasNoStatableFigures(int utxoCount, int utxoSize)
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(Config(utxoCount, utxoSize)));

    [Theory]
    [InlineData(20, 100_000, 0)]
    [InlineData(20, 100_000, RgbConfigBounds.MinConfirmationsMax + 1)]
    public void OutOfRangeMinConfirmations_HasNoStatableFigures(
        int utxoCount, int utxoSize, int minConfirmations)
        => Assert.False(RGBController.ArePersistedReplenishmentFiguresValid(
            Config(utxoCount, utxoSize, minConfirmations)));

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public void ThePrintedFiguresReproduceTheEnforcedCeilingAtEveryVanillaUtxoCount(int utxoCount)
    {
        var printedBase = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(utxoCount);
        var printedPerUtxo = RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(utxoCount);
        for (var vanillaInputs = 1; vanillaInputs <= 250; vanillaInputs++)
        {
            var honest = RGBWalletService.EstimateTaprootFee(
                vanillaInputs, utxoCount + 1, RGBWalletService.CreateUtxosFeeRate);
            Assert.True(printedBase + printedPerUtxo * (vanillaInputs - 1) >= honest,
                $"UtxoCount {utxoCount}, vanilla inputs {vanillaInputs}: the card understates what can "
                + "actually be spent, so consent is given against a smaller number than the signer "
                + "admits.");
        }
    }

    [Fact]
    public void PopulateSettingsViewModel_DerivesEveryConsentFigureFromThePersistedConfigOnly()
    {
        var tree = PluginCompilation.Shared.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, ControllerType, Populate);
        var body = RoslynPins.BodyOf(method);

        var postedReads = body.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.Expression is IdentifierNameSyntax { Identifier.ValueText: "vm" }
                        && m.Name.Identifier.ValueText is "UtxoCount" or "UtxoSize" or "MinConfirmations")
            .Select(m => m.ToString())
            .ToList();
        Assert.True(postedReads.Count == 0,
            $"{Populate} reads {string.Join(", ", postedReads)}. On the ModelState-invalid re-render "
            + "those hold what the operator POSTED, and this method is called with preferSubmitted: true "
            + "from that path, so any consent figure derived from them describes a submission that was "
            + "rejected while the Authorize button on the very same page records a durable grant against "
            + "the PERSISTED values. Reachable with stored 20/100000 and a posted UtxoCount of 0: the "
            + "card read '0 at a time' at a 160-sat worst case.");

        var assignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: "vm" }
            } left && ConsentFigureMembers.Contains(left.Name.Identifier.ValueText))
            .ToList();
        Assert.True(assignments.Count == ConsentFigureMembers.Length,
            $"{Populate} assigns {assignments.Count} of the {ConsentFigureMembers.Length} consent figures "
            + $"({string.Join(", ", ConsentFigureMembers)}); an unassigned one silently renders its "
            + "default and the card states a figure no configuration produced.");

        foreach (var assignment in assignments)
        {
            var right = assignment.Right.ToString();
            Assert.True(right.Contains("storedConfig", StringComparison.Ordinal),
                $"{Populate}: `{assignment}` does not read storedConfig. Every consent figure must come "
                + "from the persisted payment-method config, which is the only thing the unattended sweep "
                + "ever reads.");
        }
    }

    [Fact]
    public void TheGrantPost_RefusesWhenNoPersistedFiguresExist_AndNeverGatesRevocation()
    {
        var tree = PluginCompilation.Shared.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, ControllerType, GrantAction);
        var body = RoslynPins.BodyOf(method);

        var gate = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: PersistedGate })
            .ToList();
        Assert.True(gate.Count == 1,
            $"{GrantAction} invokes {PersistedGate} {gate.Count} time(s); exactly one is expected. "
            + "Without it a direct POST records a durable grant against figures the consent card was "
            + "never able to state.");

        var record = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "RecordDecisionAsync"
            });
        Assert.True(gate[0].SpanStart < record.SpanStart,
            $"{GrantAction} must consult {PersistedGate} BEFORE writing the decision");

        var guard = gate[0].Ancestors().OfType<IfStatementSyntax>().First();
        var condition = guard.Condition.ToString();
        Assert.True(condition.Contains("grant &&", StringComparison.Ordinal),
            $"{GrantAction}'s guard condition is `{condition}`. It must be conjoined with `grant` so it "
            + "applies to authorization ONLY. Gating revocation would block the operator's emergency stop "
            + "on exactly the stores whose configuration has gone out of range — a permanent refusal of "
            + "the one action that must always be available.");
        Assert.Contains("return", guard.Statement.ToString());
    }

    [Fact]
    public void TheConsentCard_StatesPersistedFiguresAndWithholdsAuthorizeWithoutThem()
    {
        var card = ConsentCard();

        foreach (var posted in new[] { "@Model.UtxoCount", "@Model.UtxoSize" })
            Assert.DoesNotContain(posted, card);
        Assert.Contains("@Model.PersistedUtxoCount", card);
        Assert.Contains("@Model.PersistedUtxoSize", card);
        Assert.Contains("@Model.WorstCaseReplenishFeeBaseSats", card);
        Assert.Contains("@Model.WorstCaseReplenishFeePerVanillaUtxoSats", card);
        Assert.DoesNotContain("WorstCaseReplenishFeeSats", card);

        Assert.Matches(
            new Regex(@"else if \(Model\.PersistedUtxoCount\.HasValue\)[^}]*?"
                      + @"id=""rgb-authorize-auto-replenishment""", RegexOptions.Singleline),
            card);
        Assert.Contains("rgb-authorize-auto-replenishment-unavailable", card);

        var revokeAt = card.IndexOf("rgb-revoke-auto-replenishment", StringComparison.Ordinal);
        var authorizeGateAt = card.IndexOf(
            "else if (Model.PersistedUtxoCount.HasValue)", StringComparison.Ordinal);
        Assert.True(revokeAt >= 0 && authorizeGateAt > revokeAt,
            "the Revoke button must sit in the branch BEFORE the persisted-figures gate, so revocation is "
            + "never withheld — it is the emergency stop");
    }

    static RGBSettingsViewModel Card(int cap, int? persistedUtxoSize) =>
        new() { MaxAutoColorableUtxos = cap, PersistedUtxoSize = persistedUtxoSize };

    [Fact]
    public void MaxAutoColorablePrincipal_IsTheCapTimesTheSavedUtxoSize()
        => Assert.Equal(5_000_000L, Card(50, 100_000).MaxAutoColorablePrincipalSats);

    [Theory]
    [InlineData(50, 1000, 50_000L)]
    [InlineData(4, 1000, 4_000L)]
    [InlineData(50, 546, 27_300L)]
    [InlineData(1, 100_000, 100_000L)]
    [InlineData(0, 100_000, 0L)]
    public void MaxAutoColorablePrincipal_MovesWithBothLiveInputs(int cap, int size, long expected)
        => Assert.Equal(expected, Card(cap, size).MaxAutoColorablePrincipalSats);

    // The card is the consent screen, so a figure it prints is not "display only". A negative cap
    // reached the view model as a bare int.TryParse result, and the card read "up to -1" colorable UTXOs
    // with a negative parked principal — false consent text on the one screen whose entire purpose is
    // informed authorization. The cap is floored at intake, so every reader agrees and 0 keeps its
    // existing meaning of "automatic creation disabled".
    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    [InlineData(int.MinValue)]
    public void ANegativeDeploymentCap_ReadsAsDisabledNotAsANegativeFigure(int configured)
    {
        var cfg = new RGBConfiguration { MaxAutoColorableUtxos = configured };
        Assert.Equal(0, cfg.MaxAutoColorableUtxos);

        var vm = Card(cfg.MaxAutoColorableUtxos, 100_000);
        Assert.Equal(0L, vm.MaxAutoColorablePrincipalSats);
        Assert.Equal(0L, vm.MaxAutoColorablePrincipalCeilingSats);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-50")]
    public void ANegativeCapFromTheEnvironment_ReadsAsDisabled(string raw)
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, key =>
            key == "RGB_MAX_AUTO_COLORABLE_UTXOS" ? raw : null);

        Assert.Equal(0, cfg.MaxAutoColorableUtxos);
    }

    [Fact]
    public void ANegativeCapFromTheConfigFile_ReadsAsDisabled()
    {
        var cfg = System.Text.Json.JsonSerializer.Deserialize<RGBConfiguration>(
            """{ "max_auto_colorable_utxos": -7 }""")!;

        Assert.Equal(0, cfg.MaxAutoColorableUtxos);
    }

    // Flooring must not change what 0 already meant, or it silently re-enables unattended signing
    // nowhere or disables it everywhere.
    [Fact]
    public void TheFlooringLeavesZeroAndPositiveCapsUntouched()
    {
        Assert.Equal(0, new RGBConfiguration { MaxAutoColorableUtxos = 0 }.MaxAutoColorableUtxos);
        Assert.Equal(1, new RGBConfiguration { MaxAutoColorableUtxos = 1 }.MaxAutoColorableUtxos);
        Assert.Equal(50, new RGBConfiguration().MaxAutoColorableUtxos);
        Assert.Equal(int.MaxValue,
            new RGBConfiguration { MaxAutoColorableUtxos = int.MaxValue }.MaxAutoColorableUtxos);
    }

    [Fact]
    public void MaxAutoColorablePrincipal_IsAbsentWithoutSavedSettings()
        => Assert.Null(Card(50, null).MaxAutoColorablePrincipalSats);

    // A colorable UTXO is never resized or reclaimed and still counts toward the cap, so a store that
    // ran at 100_000 until the cap was reached and then lowered UtxoSize to 546 has 5_000_000 sats
    // parked while the current-settings figure reads 27_300. Understating what the authorization has
    // parked invalidates the consent, which is exactly what
    // ThePrintedFiguresReproduceTheEnforcedCeilingAtEveryVanillaUtxoCount forbids for the fee figures.
    // The ceiling is the figure that cannot understate, at any UtxoSize the bounds have ever allowed.
    [Fact]
    public void TheCeiling_IsNeverBelowTheCurrentFigure_AtEveryLegalUtxoSize()
    {
        for (var size = RgbConfigBounds.UtxoSizeMin; size <= RgbConfigBounds.UtxoSizeMax; size++)
        {
            var vm = Card(50, size);
            Assert.True(vm.MaxAutoColorablePrincipalCeilingSats >= vm.MaxAutoColorablePrincipalSats,
                $"at UtxoSize {size} the ceiling {vm.MaxAutoColorablePrincipalCeilingSats} is below the "
                + $"current figure {vm.MaxAutoColorablePrincipalSats}. The ceiling is the only figure on "
                + "the card that bounds what earlier, larger UtxoSize settings already parked.");
        }
    }

    [Fact]
    public void TheCeilingIsTheCapAtTheLargestSizeTheBoundsAllow()
        => Assert.Equal(50L * RgbConfigBounds.UtxoSizeMax,
            Card(50, 546).MaxAutoColorablePrincipalCeilingSats);

    // RgbConfigBounds declares NO ceiling for MaxAutoColorableUtxos, and RGB_MAX_AUTO_COLORABLE_UTXOS is
    // read with int.TryParse and no clamp, so the cap is the input that can overflow the product. A test
    // built from "the bounds maxima" would stay green with both casts removed.
    [Fact]
    public void MaxAutoColorablePrincipal_DoesNotOverflowAtAnAdversarialCap()
    {
        var vm = Card(int.MaxValue, RgbConfigBounds.UtxoSizeMax);

        Assert.Equal((long)int.MaxValue * RgbConfigBounds.UtxoSizeMax,
            vm.MaxAutoColorablePrincipalSats);
        Assert.Equal((long)int.MaxValue * RgbConfigBounds.UtxoSizeMax,
            vm.MaxAutoColorablePrincipalCeilingSats);
        Assert.True(vm.MaxAutoColorablePrincipalSats > 0,
            $"the printed principal is {vm.MaxAutoColorablePrincipalSats}; an int-int product overflows "
            + "to a negative figure and the consent card would state a negative number of sats.");
    }

    // The material consequence of the authorization, and the one the card never stated: the manual path
    // says it in RGBWalletService.EnsureStandingColorableRoom's refusal, but the manual path is the one
    // the merchant did not have to consent to.
    [Fact]
    public void TheConsentCard_StatesTheParkedPrincipalAndThatSendBtcCannotSpendIt()
    {
        var card = ConsentCard();

        Assert.Contains("@Model.MaxAutoColorablePrincipalSats", card);
        Assert.Contains("@Model.MaxAutoColorablePrincipalCeilingSats", card);
        Assert.Contains("Send BTC cannot spend a colorable UTXO", card);

        // Both figures are derived from the CURRENT deployment cap, and lowering that cap (or setting it
        // to 0) does not release a single standing colorable UTXO. So the card must present them as a
        // bound on what this authorization can still park, never as the total already parked — otherwise
        // a store whose cap was reduced reads a figure far below what is actually beyond Send BTC's
        // reach, and that understatement is what invalidates the consent.
        Assert.Contains("can park at most", card);
        Assert.Contains("already holds are additional", card);
        foreach (var totalClaim in new[] { "the parked total is bounded", "in total", "total parked" })
            Assert.True(!card.Contains(totalClaim, StringComparison.OrdinalIgnoreCase),
                $"the consent card claims '{totalClaim}'. Neither printed figure bounds the historical "
                + "total: both are the CURRENT cap times a size, and a cap lowered after UTXOs were "
                + "created leaves them standing and unspendable by Send BTC.");

        var figures = Regex.Matches(card, @"<strong>\s*\d[\d,_ ]*\s*sat</strong>");
        Assert.True(figures.Count == 0,
            "the consent card contains a hardcoded sats figure: "
            + string.Join(", ", figures.Select(m => m.Value))
            + ". Every figure must be computed from the live cap, the saved size and the live bounds, or "
            + "it goes stale the moment one of them changes.");
    }

    static string ConsentCard()
    {
        var view = RgbSettingsReadOnlyTests.ReadRepoFile(Path.Combine("Views", "RGB", "Settings.cshtml"));
        var start = view.IndexOf("id=\"rgb-auto-replenishment-card\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the automatic-replenishment consent card is missing from Settings.cshtml");
        var end = view.IndexOf("Wallet Information", start, StringComparison.Ordinal);
        Assert.True(end > start, "the consent card is no longer followed by the Wallet Information card");
        return view[start..end];
    }
}
