using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbReplenishmentNoticeTests
{
    static RgbReplenishmentNoticeCause Cause(
        bool paymentMethodEnabled = true,
        bool hasStoredConfig = true,
        bool configValuesValid = true,
        int maxAutoColorableUtxos = 50,
        bool standingAuthorizationGranted = false) =>
        RgbReplenishmentNotice.Evaluate(
            paymentMethodEnabled: paymentMethodEnabled,
            hasStoredConfig: hasStoredConfig,
            configValuesValid: configValuesValid,
            maxAutoColorableUtxos: maxAutoColorableUtxos,
            standingAuthorizationGranted: standingAuthorizationGranted);

    [Fact]
    public void RgbDisabledForTheStore_NoNotice()
        => Assert.Equal(RgbReplenishmentNoticeCause.None, Cause(paymentMethodEnabled: false));

    [Fact]
    public void NoStoredConfigAtAll_NoNotice()
        => Assert.Equal(RgbReplenishmentNoticeCause.None, Cause(hasStoredConfig: false));

    [Fact]
    public void EnabledAndGrantedAndHealthy_NoNotice()
        => Assert.Equal(RgbReplenishmentNoticeCause.None, Cause(standingAuthorizationGranted: true));

    [Fact]
    public void EnabledAndNotGranted_IsCauseA()
        => Assert.True(Cause() == RgbReplenishmentNoticeCause.NotAuthorized,
            "with RGB enabled and no standing authorization the operator must be told, and the predicate "
            + "must not consult the colorable pool at all: every pool-dependent formulation fires at the "
            + "END of the drain window rather than its start.");

    [Fact]
    public void FullPoolAndNotGranted_StillFiresCauseA()
        => Assert.True(Cause() == RgbReplenishmentNoticeCause.NotAuthorized,
            "the notice predicate is arithmetic-free: it takes no slot counts at all, so a store with a "
            + "completely full colorable pool and no grant still gets the notice. A band-based predicate "
            + "is silent in exactly this state, which is the start of the window.");

    [Fact]
    public void CapZero_IsCauseBNotCauseA()
        => Assert.True(Cause(maxAutoColorableUtxos: 0) == RgbReplenishmentNoticeCause.CapDisabledDeploymentWide,
            "on a deployment whose cap is 0, automatic creation is disabled deployment-wide and a "
            + "store-level grant changes nothing. Reporting cause A here would invite a standing "
            + "unattended-signing grant that does nothing at all.");

    [Fact]
    public void CapZeroAndAlreadyGranted_StillReportsCauseB()
        => Assert.Equal(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide,
            Cause(maxAutoColorableUtxos: 0, standingAuthorizationGranted: true));

    [Fact]
    public void OutOfBoundsStoredConfig_IsCauseCEvenWhenTheCapIsAlsoZeroAndNoGrantExists()
        => Assert.True(
            Cause(configValuesValid: false, maxAutoColorableUtxos: 0) == RgbReplenishmentNoticeCause.ConfigOutOfBounds,
            "cause C is evaluated FIRST because it blocks earliest: the sweep refuses an out-of-range "
            + "stored config and continues before it reaches any other gate. Both of cause A's remedies "
            + "fail in that state — the grant is refused at the same guard and Create UTXOs throws — and "
            + "the real remedy, re-saving the settings, is named nowhere else.");

    [Fact]
    public void OutOfBoundsStoredConfig_IsCauseCEvenWhenAlreadyGranted()
        => Assert.Equal(RgbReplenishmentNoticeCause.ConfigOutOfBounds,
            Cause(configValuesValid: false, standingAuthorizationGranted: true));

    [Fact]
    public void OnlyCauseAInvitesAGrant()
    {
        Assert.True(RgbReplenishmentNotice.InvitesGrant(RgbReplenishmentNoticeCause.NotAuthorized));
        Assert.False(RgbReplenishmentNotice.InvitesGrant(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide),
            "cause B must not invite a grant: a store-level grant cannot raise a deployment-wide cap");
        Assert.False(RgbReplenishmentNotice.InvitesGrant(RgbReplenishmentNoticeCause.ConfigOutOfBounds),
            "cause C must not invite a grant: the grant is refused at the very guard that produced it");
    }

    [Fact]
    public void OnlyCauseAIsLoggedPerSweep()
    {
        Assert.True(RgbReplenishmentNotice.LogsPerSweep(RgbReplenishmentNoticeCause.NotAuthorized));
        Assert.False(RgbReplenishmentNotice.LogsPerSweep(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide),
            "a deliberate deployment-wide cap of 0 must not log once per sweep forever, for every wallet");
        Assert.False(RgbReplenishmentNotice.LogsPerSweep(RgbReplenishmentNoticeCause.ConfigOutOfBounds));
        Assert.False(RgbReplenishmentNotice.LogsPerSweep(RgbReplenishmentNoticeCause.None));
    }

    [Theory]
    [InlineData(RgbReplenishmentNoticeCause.NotAuthorized)]
    [InlineData(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide)]
    [InlineData(RgbReplenishmentNoticeCause.ConfigOutOfBounds)]
    public void EveryCauseCarriesConsequenceAndAction(RgbReplenishmentNoticeCause cause)
    {
        var message = RgbReplenishmentNotice.MessageFor(cause);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("stop", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CauseCNamesResavingTheSettingsAsItsRemedy()
        => Assert.Contains("Re-save",
            RgbReplenishmentNotice.MessageFor(RgbReplenishmentNoticeCause.ConfigOutOfBounds),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CauseBSaysAGrantWillNotHelp()
        => Assert.Contains("will not change that",
            RgbReplenishmentNotice.MessageFor(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void CauseAPromisesNecessityNotSufficiency()
        => Assert.Contains("may not be sufficient",
            RgbReplenishmentNotice.MessageFor(RgbReplenishmentNoticeCause.NotAuthorized),
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EachCauseHasItsOwnPersistedMarkerColumn()
    {
        var row = new RGBStoreNoticeState { StoreId = "s" };
        var causes = new[]
        {
            RgbReplenishmentNoticeCause.NotAuthorized,
            RgbReplenishmentNoticeCause.CapDisabledDeploymentWide,
            RgbReplenishmentNoticeCause.ConfigOutOfBounds
        };

        foreach (var cause in causes)
            Assert.True(RgbReplenishmentNoticeService.MarkerOf(row, cause) == null,
                $"a fresh notice-state row must have no marker for {cause}");

        foreach (var stamped in causes)
        {
            var fresh = new RGBStoreNoticeState { StoreId = "s" };
            RgbReplenishmentNoticeService.StampMarker(fresh, stamped, DateTimeOffset.UnixEpoch);
            foreach (var other in causes)
            {
                var marker = RgbReplenishmentNoticeService.MarkerOf(fresh, other);
                if (other == stamped)
                    Assert.True(marker != null, $"stamping {stamped} must set its own marker");
                else
                    Assert.True(marker == null,
                        $"stamping {stamped} must NOT suppress {other}: a store notified for one cause has "
                        + "to receive the others once that cause clears, so the three causes cannot share "
                        + "a marker column");
            }
        }
    }
}
