using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReplenishDecisionTests
{
    static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    const int Cap = 50;

    static ReplenishOutcome? Eligibility(
        bool isActive = true, bool needsRecovery = false, int maxAllocationsPerUtxo = 10,
        bool paymentMethodEnabled = true, string? configuredWalletId = "w1",
        DateTimeOffset? nextEligibleAt = null) =>
        RGBInvoiceListener.EvaluateReplenishEligibility(
            walletId: "w1", isActive: isActive, needsRecovery: needsRecovery,
            maxAllocationsPerUtxo: maxAllocationsPerUtxo, paymentMethodEnabled: paymentMethodEnabled,
            configuredWalletId: configuredWalletId, now: Now, nextEligibleAt: nextEligibleAt);

    static ReplenishDecision Demand(
        int colorableCount = 4, int usedByColorings = 0, int activePendingInvoices = 0,
        int maxAllocationsPerUtxo = 10, int minFreeSlots = 4, int utxoSize = 1000,
        int maxAutoColorableUtxos = Cap, bool standingAuthorizationGranted = true) =>
        RGBInvoiceListener.EvaluateReplenishDemand(
            colorableCount: colorableCount, usedByColorings: usedByColorings,
            activePendingInvoices: activePendingInvoices, maxAllocationsPerUtxo: maxAllocationsPerUtxo,
            minFreeSlots: minFreeSlots, utxoSize: utxoSize, maxAutoColorableUtxos: maxAutoColorableUtxos,
            standingAuthorizationGranted: standingAuthorizationGranted);

    const int DemandingColorableCount = 1;
    const int DemandingActivePendingInvoices = 10;

    static ReplenishDecision DemandUnderRealPressure(bool standingAuthorizationGranted) =>
        Demand(colorableCount: DemandingColorableCount, usedByColorings: 0,
            activePendingInvoices: DemandingActivePendingInvoices,
            standingAuthorizationGranted: standingAuthorizationGranted);

    [Fact]
    public void NoStandingAuthorization_SkipsBeforeAnyArithmetic()
        => Assert.True(
            DemandUnderRealPressure(standingAuthorizationGranted: false).Outcome
                == ReplenishOutcome.SkipCapReached,
            "with no standing authorization the demand computation must refuse before it can construct a "
            + $"request. These parameters ({DemandingColorableCount} colorable UTXO, "
            + $"{DemandingActivePendingInvoices} active pending invoices) exhaust every free slot, so an "
            + "authorized store returns Create here — as the granted half of "
            + "NoStandingAuthorization_RequestsNothing asserts. Otherwise a public invoice can still "
            + "trigger an unattended signature nobody granted.");

    [Fact]
    public void NoStandingAuthorization_RequestsNothing()
    {
        var granted = DemandUnderRealPressure(standingAuthorizationGranted: true);
        Assert.True(granted.Outcome == ReplenishOutcome.Create && granted.RequestCount > 0,
            "these parameters must be ones an AUTHORIZED store acts on, or asserting that an "
            + $"unauthorized store requests nothing proves nothing at all; granted gives {granted.Outcome} "
            + $"requesting {granted.RequestCount}");
        Assert.Equal(0, DemandUnderRealPressure(standingAuthorizationGranted: false).RequestCount);
    }

    [Fact]
    public void RgbExcludedForTheStore_Skips()
        => Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled, Eligibility(paymentMethodEnabled: false));

    [Fact]
    public void NoRgbConfigAtAll_Skips()
        => Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled,
            Eligibility(paymentMethodEnabled: false, configuredWalletId: null));

    [Fact]
    public void ConfigNamesADifferentWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(configuredWalletId: "other"));

    [Fact]
    public void QuarantinedWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipQuarantined, Eligibility(needsRecovery: true));

    [Fact]
    public void InactiveWallet_Skips()
        => Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(isActive: false));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxAllocations_Skips(int maxAlloc)
        => Assert.Equal(ReplenishOutcome.SkipInvalidWalletConfig, Eligibility(maxAllocationsPerUtxo: maxAlloc));

    [Fact]
    public void CooldownBoundary_SkipsOnlyStrictlyBefore()
    {
        Assert.Equal(ReplenishOutcome.SkipCooldown, Eligibility(nextEligibleAt: Now.AddTicks(1)));
        Assert.Null(Eligibility(nextEligibleAt: Now));
        Assert.Null(Eligibility(nextEligibleAt: Now.AddTicks(-1)));
    }

    // Pins the documented PRECEDENCE, one gate at a time: with every condition failing, satisfy the gates in
    // order and each successive outcome must appear. Asserting only the first-place gate would leave every
    // permutation of the other five green. (Which gates precede ListUnspentsAsync is P-C3's job, not this
    // test's — this one is about the order among the six refusals.)
    [Fact]
    public void SkipConditions_ArePrioritisedInTheDocumentedOrder()
    {
        Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(
            isActive: false, needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other", nextEligibleAt: Now.AddHours(1)));

        Assert.Equal(ReplenishOutcome.SkipCooldown, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other", nextEligibleAt: Now.AddHours(1)));

        Assert.Equal(ReplenishOutcome.SkipPaymentMethodDisabled, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0,
            paymentMethodEnabled: false, configuredWalletId: "other"));

        Assert.Equal(ReplenishOutcome.SkipWalletNotConfigured, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0, configuredWalletId: "other"));

        Assert.Equal(ReplenishOutcome.SkipQuarantined, Eligibility(
            needsRecovery: true, maxAllocationsPerUtxo: 0));

        Assert.Equal(ReplenishOutcome.SkipInvalidWalletConfig, Eligibility(maxAllocationsPerUtxo: 0));
    }

    [Fact]
    public void HealthyEnabledMatchingWallet_IsEligible() => Assert.Null(Eligibility());

    [Fact]
    public void EnoughFreeSlots_DoesNotCreate()
    {
        var decision = Demand();
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, decision.Outcome);
        Assert.Equal(0, decision.RequestCount);
    }

    // The attacker's lever, isolated: identical inputs except the stale-invoice term.
    [Fact]
    public void StalePendingInvoices_AreTheOnlyDifferenceBetweenSkipAndCreate()
    {
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, Demand(activePendingInvoices: 0).Outcome);
        Assert.Equal(ReplenishOutcome.Create, Demand(activePendingInvoices: 37).Outcome);
    }

    [Fact]
    public void CapAlreadyReached_DoesNotCreate()
        => Assert.Equal(ReplenishOutcome.SkipCapReached, Demand(colorableCount: Cap).Outcome);

    [Fact]
    public void ColorableCountExactlyAtTheCapWithNoFreeSlots_SkipsBeforeTheHeadroomClamp()
    {
        var outcome = Demand(
            colorableCount: Cap, usedByColorings: Cap * 10, activePendingInvoices: 500).Outcome;
        Assert.True(outcome == ReplenishOutcome.SkipCapReached,
            $"a pool at the cap with every slot used resolved to {outcome}. This is the ONLY input that "
            + "reaches the headroom clamp with a starved pool AND no headroom left: every slot is used, so "
            + "the free-slots gate lets it through and only the cap gate stands between it and "
            + "Math.Clamp(needed, 1, 0), which throws ArgumentException because min exceeds max. Weakening "
            + $"that gate to a strict `>` is a one-character edit that "
            + $"{nameof(CapAlreadyReached_DoesNotCreate)} catches on its own parameters, but only this row "
            + "makes it reach the clamp.");
    }

    // UtxoSize is the number of sats buried in each created UTXO, so returning anything but the configured
    // value changes how much the automatic path spends. It is asserted on the skip outcomes too — not
    // because the shell logs it there (it does not), but because a mutation that corrupts the size only on
    // the Create path is the one that costs money, and pinning every outcome leaves it nowhere to hide.
    [Theory]
    [InlineData(1000)]
    [InlineData(4242)]
    public void UtxoSize_IsCarriedThroughUnchanged(int utxoSize)
    {
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, activePendingInvoices: 37).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, colorableCount: Cap).UtxoSize);
        Assert.Equal(utxoSize, Demand(utxoSize: utxoSize, maxAutoColorableUtxos: 0).UtxoSize);
    }

    [Fact]
    public void DemandBeyondTheCap_IsClampedToTheHeadroomBelowTheCap()
    {
        const int colorable = 40;
        var decision = Demand(colorableCount: colorable, usedByColorings: 400, minFreeSlots: 200);
        Assert.True(decision.RequestCount == Cap - colorable,
            $"with maxAlloc 10 and freeSlots 0 the shortfall is ceil(200/10) = 20 new UTXOs while the "
            + $"headroom is Cap - {colorable} = {Cap - colorable}, and the request came out as "
            + $"{decision.RequestCount}. The clamp to the headroom is what binds here.");
        Assert.True(colorable + decision.RequestCount == Cap,
            $"RequestCount is an INCREMENT of new UTXOs, not a target total, so the cap binds through the "
            + $"headroom below it rather than through the request itself: {colorable} standing + "
            + $"{decision.RequestCount} new must land on the {Cap} cap, not on 60.");
    }

    [Fact]
    public void HugeMinFreeSlots_IsClampedToTheHeadroomBelowTheCap()
    {
        var decision = Demand(colorableCount: Cap - 1, usedByColorings: Cap - 1, maxAllocationsPerUtxo: 1,
            minFreeSlots: int.MaxValue);
        Assert.Equal(ReplenishOutcome.Create, decision.Outcome);
        Assert.True(decision.RequestCount == 1,
            $"freeSlots 0 with maxAllocationsPerUtxo 1 makes the shortfall int.MaxValue, which only the "
            + $"headroom clamp brings back into range, and the request came out as {decision.RequestCount}.");
        Assert.True(Cap - 1 + decision.RequestCount == Cap,
            $"a request of Cap here would put the standing total at Cap + colorableCount; {Cap - 1} "
            + $"standing + {decision.RequestCount} new must land exactly on the {Cap} cap.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void NonPositiveMinFreeSlots_NeverCreates(int minFreeSlots)
        => Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, Demand(minFreeSlots: minFreeSlots).Outcome);

    [Fact]
    public void EveryCreateOutcome_HonoursEveryFigureTheConsentScreenStates()
    {
        var examined = 0;
        foreach (var colorable in new[] { 0, 1, 7, 40, Cap - 1 })
        foreach (var maxAlloc in new[] { 1, 3, 10 })
        foreach (var minFreeSlots in new[] { 1, 4, 40, 200 })
        foreach (var pending in new[] { 0, 5, 50, 500 })
        {
            var decision = Demand(colorableCount: colorable, activePendingInvoices: pending,
                maxAllocationsPerUtxo: maxAlloc, minFreeSlots: minFreeSlots);
            if (decision.Outcome != ReplenishOutcome.Create) continue;
            examined++;

            Assert.True(decision.RequestCount >= 1,
                $"colorable {colorable}, maxAlloc {maxAlloc}, minFreeSlots {minFreeSlots}, pending "
                + $"{pending}: a Create outcome requested {decision.RequestCount}. Asking rgb-lib for zero "
                + "new outputs with up_to = false builds a transaction with no created UTXO and stamps a "
                + "success cooldown, so the wallet stalls for a full cooldown having done nothing.");

            Assert.True(colorable + decision.RequestCount <= Cap,
                $"colorable {colorable}, maxAlloc {maxAlloc}, minFreeSlots {minFreeSlots}, pending "
                + $"{pending}: {colorable} standing + {decision.RequestCount} new = "
                + $"{colorable + decision.RequestCount}, over the {Cap} standing colorable UTXOs the "
                + "consent screen states as the deployment-wide limit. The clamp to the headroom below the "
                + "cap is the ONLY thing enforcing that limit — rgb-lib deducts nothing with up_to = false, "
                + "and deducted only the allocatable ones with up_to = true, so a total-standing request "
                + "would create (needed + excluded) outputs. That argument is pinned by "
                + $"{nameof(RgbDryRunSourcePinTests)}."
                + $"{nameof(RgbDryRunSourcePinTests.CreateUtxosBegin_PassesDryRunTrueAtItsOnlyLiveCallSite)}"
                + ".");

            Assert.True(decision.RequestCount <= minFreeSlots,
                $"colorable {colorable}, maxAlloc {maxAlloc}, minFreeSlots {minFreeSlots}, pending "
                + $"{pending}: requested {decision.RequestCount} new UTXOs in one attempt, over the "
                + $"{minFreeSlots} the consent screen states are created at a time.");

            Assert.True(
                RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(decision.RequestCount)
                <= RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(minFreeSlots)
                && RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(decision.RequestCount)
                   <= RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(minFreeSlots),
                $"colorable {colorable}, maxAlloc {maxAlloc}, minFreeSlots {minFreeSlots}, pending "
                + $"{pending}: the fee ceiling the signing policy derives from RequestCount "
                + $"{decision.RequestCount} exceeds, in one of its two terms, the worst-case fee per "
                + "attempt the consent screen prints, which PopulateSettingsViewModel computes from the "
                + "PERSISTED UtxoCount by the same two expressions. The policy widens in lockstep with "
                + "the request, so nothing downstream would refuse it. RequestCount is the single number "
                + "all three consent figures are spent through: RGBWalletService builds MaxOutputCount = "
                + "count + 1 and the MaxFeeSats / MaxFeeSatsPerAdditionalInput pair from it, and "
                + $"{nameof(RgbVanillaInputGuardSourcePinTests)} pins those three expressions.");
        }

        Assert.True(examined >= 100,
            $"only {examined} Create outcome(s) were reached, so this grid adjudicates almost nothing. It "
            + "must keep exercising both clamp arms — the shortfall and the headroom below the cap.");
    }

    // A non-positive cap must not reach Math.Clamp, whose min > max throws ArgumentException.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCap_SkipsWithoutThrowing(int cap)
        => Assert.Equal(ReplenishOutcome.SkipCapReached,
            Demand(activePendingInvoices: 500, maxAutoColorableUtxos: cap).Outcome);

    // The unchanged-behaviour anchor for a healthy wallet on today's defaults.
    [Fact]
    public void TodaysDefaults_ForAHealthyWallet_DoNotCreate()
        => Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots,
            Demand(colorableCount: 4, usedByColorings: 0, activePendingInvoices: 0,
                maxAllocationsPerUtxo: 10, minFreeSlots: 4).Outcome);

    static bool FinalAuthorization(
        bool enabled = true,
        bool active = true,
        bool quarantined = false,
        bool archived = false,
        string storeId = "s1",
        RGBPaymentMethodConfig? current = null,
        RGBPaymentMethodConfig? expected = null)
    {
        current ??= new RGBPaymentMethodConfig { UtxoCount = 4, UtxoSize = 1000, MinConfirmations = 1 };
        expected ??= new RGBPaymentMethodConfig { UtxoCount = 4, UtxoSize = 1000, MinConfirmations = 1 };
        return RGBInvoiceListener.IsAutomaticReplenishmentAuthorized(
            new RGBWallet
            {
                Id = "w1", StoreId = storeId, IsActive = active,
                NeedsRecovery = quarantined, MaxAllocationsPerUtxo = 10
            },
            "s1", enabled, archived, current, expected);
    }

    [Fact]
    public void FinalAuthorization_ArchivedStore_Rejects()
        => Assert.False(FinalAuthorization(archived: true),
            "an archived store is one the operator has stepped away from; unattended signing on it must "
            + "refuse. This is additional plugin policy, not BTCPay's notion of a disabled payment method "
            + "— an archived store's RGB payment method is still enabled and its checkout still works.");

    [Fact]
    public void FinalAuthorization_HealthyUnchangedState_Passes()
        => Assert.True(FinalAuthorization());

    [Fact]
    public void FinalAuthorization_DisabledAfterDemandDecision_Rejects()
        => Assert.False(FinalAuthorization(enabled: false));

    [Theory]
    [InlineData(true, false, "other")]
    [InlineData(false, false, "s1")]
    [InlineData(true, true, "s1")]
    public void FinalAuthorization_WrongStoreInactiveOrQuarantined_Rejects(
        bool active, bool quarantined, string storeId)
        => Assert.False(FinalAuthorization(active: active, quarantined: quarantined, storeId: storeId));

    [Fact]
    public void FinalAuthorization_ConfigChangedAfterDemandDecision_Rejects()
        => Assert.False(FinalAuthorization(current: new RGBPaymentMethodConfig
        {
            UtxoCount = 4, UtxoSize = 2000, MinConfirmations = 1
        }));

    [Fact]
    public void FinalRequest_FreshDemandStillExactlyMatches_Passes()
    {
        var decision = Demand(activePendingInvoices: 37);
        Assert.True(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            decision, decision.RequestCount, decision.UtxoSize));
    }

    [Fact]
    public void FinalRequest_InvoiceDemandDisappearedWhileWaiting_Rejects()
    {
        var original = Demand(activePendingInvoices: 37);
        var fresh = Demand(activePendingInvoices: 0);
        Assert.Equal(ReplenishOutcome.Create, original.Outcome);
        Assert.Equal(ReplenishOutcome.SkipEnoughFreeSlots, fresh.Outcome);
        Assert.False(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            fresh, original.RequestCount, original.UtxoSize));
    }

    [Fact]
    public void FinalRequest_UtxoStateChangedWhileWaiting_RejectsStaleCount()
    {
        var original = Demand(colorableCount: 48, usedByColorings: 480, minFreeSlots: 100);
        var fresh = Demand(colorableCount: 49, usedByColorings: 490, minFreeSlots: 100);
        Assert.Equal(ReplenishOutcome.Create, original.Outcome);
        Assert.Equal(ReplenishOutcome.Create, fresh.Outcome);
        Assert.NotEqual(original.RequestCount, fresh.RequestCount);
        Assert.False(RGBInvoiceListener.IsCurrentReplenishmentRequestAuthorized(
            fresh, original.RequestCount, original.UtxoSize));
    }
}
