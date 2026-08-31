using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RefusalsNeverPromiseRefreshAsAnUnconditionalRemedyTests
{
    public static TheoryData<string, string> RefusalsThatSendTheOperatorToTheRefreshButton() => new()
    {
        {
            "the refusal shown when a restore found a wallet record already held for this store",
            RGBWalletService.RestoreFoundThisStoreAlreadyHoldsAWalletRecordRefusal
        },
        {
            "the refusal shown when restored wallet data could not be brought online",
            RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
                "signet", new RgbLibException("get_address failed: Indexer error: connection refused"))
        }
    };

    [Theory]
    [MemberData(nameof(RefusalsThatSendTheOperatorToTheRefreshButton))]
    public void RefreshIsOfferedOnlyWithTheConditionUnderWhichItActuallyReleasesTheHold(
        string whichRefusal, string refusal)
    {
        Assert.Contains("Refresh", refusal);

        Assert.True(
            !refusal.Contains("is brought online by the Refresh button", StringComparison.Ordinal)
            && !refusal.Contains("which brings that same restored wallet online and releases the hold",
                StringComparison.Ordinal),
            $"{whichRefusal} read \"{refusal}\" and promises the Refresh button will bring the held "
            + "wallet online. RefreshWalletAsync reaches ClearNeedsRecoveryAsync only after "
            + "ReconcileWalletRecoveryAsync and GetBtcBalanceAsync(sync: true) have both opened that "
            + "wallet through rgb-lib, so wallet data rgb-lib cannot open — one of the causes this same "
            + "refusal names — fails Refresh identically on every press. The promise is then false in "
            + "exactly the case the operator cannot escape from");
    }

    [Theory]
    [MemberData(nameof(RefusalsThatSendTheOperatorToTheRefreshButton))]
    public void TheDeadEndBehindAFailingRefreshIsNamed_IncludingThatItNeedsHelpOnTheServerItself(
        string whichRefusal, string refusal)
    {
        Assert.True(refusal.Contains("cannot be deleted", StringComparison.Ordinal),
            $"{whichRefusal} read \"{refusal}\" and never says the held wallet cannot be deleted. "
            + "DeleteWalletAsync throws RgbWalletQuarantinedException while NeedsRecovery is set, so an "
            + "operator told only to Refresh will try Delete next and be refused with no explanation");

        Assert.True(refusal.Contains("someone with access to this server", StringComparison.Ordinal),
            $"{whichRefusal} read \"{refusal}\" and offers no route at all once Refresh keeps failing. "
            + "Both restore endpoints redirect while GetWalletForStoreAsync returns any row and that row "
            + "cannot be deleted, so a store Owner holding a second good backup has no browser-reachable "
            + "step left. Saying plainly that the server operator has to remove the record is the only "
            + "true thing this message can offer; inventing a self-service step would strand them longer");
    }

    [Theory]
    [MemberData(nameof(RefusalsThatSendTheOperatorToTheRefreshButton))]
    public void NamingTheDeadEndStillLeavesNoHostPathInAMessageRenderedVerbatimInABrowser(
        string whichRefusal, string refusal)
    {
        Assert.True(!refusal.Contains("/Users/", StringComparison.Ordinal)
            && !refusal.Contains("/var/", StringComparison.Ordinal)
            && !refusal.Contains(".btcpayserver", StringComparison.Ordinal)
            && !refusal.Contains("rgb-wallets", StringComparison.Ordinal)
            && !refusal.Contains(":\\", StringComparison.Ordinal),
            $"{whichRefusal} read \"{refusal}\" and names a server filesystem location. Telling the "
            + "operator that a person with server access must remove the record must not turn into "
            + "telling every store Owner where this server keeps wallet data");
    }

    [Fact]
    public void TheBringOnlineRefusalDescribesTheRollbackAsAttempted_BecauseItsOwnNextSentenceDeclinesToKnow()
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet", new RgbLibException("get_address failed: Indexer error: connection refused"));

        Assert.True(!refusal.Contains("so the restore was rolled back", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\" and states that the restore was rolled back, then opens the "
            + "very next sentence with \"Which of two states this store is now in is something you can "
            + "see and this server cannot\". RollBackTheJustPublishedRestoreAsync catches the failure of "
            + "its own row removal and logs that it claims nothing about that row, so the outcome is "
            + "unknown here and the first sentence may only describe the attempt");

        Assert.Contains("Which of two states this store is now in", refusal);
    }
}
