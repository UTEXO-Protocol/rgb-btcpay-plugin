using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreRollbackRefusalClaimsOnlyWhatItAchievedTests
{
    static readonly Exception AnIndexerOutage =
        new RgbLibException("get_address failed: Indexer error: connection refused");

    static string Refusal() =>
        RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline("signet", AnIndexerOutage);

    static string WalletServiceSource() => File.ReadAllText(
        Path.Combine(PluginCompilation.RepoRootPath, "Services", "RGBWalletService.cs"))
        .Replace("\r\n", "\n");

    [Fact]
    public void TheRefusalDescribesBothStatesTheOperatorCanMeet_RatherThanPredictingOne()
    {
        var refusal = Refusal();

        Assert.True(refusal.Contains("held pending recovery", StringComparison.Ordinal)
            && refusal.Contains("Refresh", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\". RollBackTheJustPublishedRestoreAsync can leave the wallet "
            + "row it tried to delete in place, and what the operator then meets is this store's RGB "
            + "page showing that restored wallet held pending recovery, which the first successful "
            + "Refresh releases. An operator given no description of that state and no step that works "
            + "in it has no way forward at all");

        Assert.True(refusal.Contains("restore the same backup again", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\". The other state the rollback can leave is no row at all, "
            + "in which case this store's RGB page offers the restore form and restoring the same "
            + "unmodified backup is the whole remedy. A refusal that describes only the held-wallet "
            + "state talks that operator out of the one step that would have worked");
    }

    [Fact]
    public void TheRefusalNeverStatesWhichOfThoseStatesTheRollbackActuallyLeft()
    {
        var refusal = Refusal();

        Assert.DoesNotContain("nothing it left behind has to be cleared", refusal);
        Assert.True(!refusal.Contains("was removed again", StringComparison.Ordinal)
            && !refusal.Contains("that record still exists", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\" and asserts what the rollback left behind. It cannot know "
            + "that: a SaveChangesAsync that threw can be one whose DELETE committed and whose "
            + "acknowledgement was lost, and the read-back that would settle it can fail for the same "
            + "reason the write did. Whichever half of such an assertion is wrong strands the operator "
            + "— either hunting for a record that is gone, or told to Refresh a page that is showing "
            + "them the restore form");
    }

    [Fact]
    public void NoStatementAboutTheRowSurvivesAsAParameter_BecauseNoCallerCanSupplyOneTruthfully()
    {
        var source = WalletServiceSource();

        Assert.True(
            !source.Contains("TheRowThisAttemptTriedToWriteIsProvablyAbsentAsync", StringComparison.Ordinal),
            "a read-back that answers \"absent\" and \"could not be read\" with the same false is back "
            + "in RGBWalletService. Every consumer of that boolean reads it as \"the row survived\", so "
            + "an unreachable database produces a refusal asserting a record exists — and when the "
            + "database recovers and the write had in fact committed, the operator is sent to a page "
            + "that is showing them the restore form");

        Assert.True(
            source.Contains(
                "async Task RollBackTheJustPublishedRestoreAsync(RGBWallet wallet, string walletDataDir)",
                StringComparison.Ordinal),
            "RollBackTheJustPublishedRestoreAsync returns a verdict on the row again. Its only "
            + "unhappy-path evidence is that SaveChangesAsync threw, which is not evidence at all, so "
            + "the verdict can only be a guess dressed as a fact in an operator-facing sentence");

        Assert.True(
            source.Contains(
                "RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(\n"
                + "        string walletNetwork, Exception failure)",
                StringComparison.Ordinal),
            "the bring-online refusal takes a claim about the wallet row again. The refusal is rendered "
            + "verbatim to a store Owner with no shell access, so a claim no caller can establish is a "
            + "claim that will sometimes be false in the one message the operator has to act on");
    }

    [Fact]
    public void ARollbackDeletesTheRestoredWalletDataOnlyAfterARemovalThatReturnedNormally()
    {
        var body = RollbackBody();

        var proofOfRemoval = body.IndexOf(
            "theRowRemovalReturnedNormallyWhichIsTheOnlyProofTheRowIsGone = true;", StringComparison.Ordinal);
        var earlyReturn = body.IndexOf(
            "if (!theRowRemovalReturnedNormallyWhichIsTheOnlyProofTheRowIsGone) return;",
            StringComparison.Ordinal);
        var dataDelete = body.IndexOf("Directory.Delete(walletDataDir, true);", StringComparison.Ordinal);

        Assert.True(proofOfRemoval > 0 && earlyReturn > proofOfRemoval && dataDelete > earlyReturn,
            "the restored wallet data may only be deleted after a SaveChangesAsync that returned "
            + "normally, which is the one outcome that proves the row is gone. Deleting it anywhere a "
            + "row may survive leaves that row pointing at nothing, and rgb-lib silently creates a fresh "
            + "empty wallet at that path — so the store would present an empty wallet as the restored "
            + $"one while the operator's assets stayed only in the backup. The rollback body was: {body}");
    }

    [Fact]
    public void NoInsertFailurePathOfARestoreDeletesTheWalletDataItJustPublished()
    {
        var source = WalletServiceSource();
        var insertStart = source.IndexOf(
            "                    ctx.RGBWallets.Add(wallet);\n"
            + "                    await ctx.SaveChangesAsync(ct);",
            StringComparison.Ordinal);
        var insertEnd = source.IndexOf(
            "await _rgbLib.GetOrCreateWalletAsync(wallet.Id, ct);",
            Math.Max(insertStart, 0), StringComparison.Ordinal);
        Assert.True(insertStart > 0 && insertEnd > insertStart,
            "could not locate the restore's wallet-row insert and its catches in RGBWalletService.cs; "
            + "this pin proves nothing until its anchors match the current shape of that step");

        Assert.DoesNotContain("Directory.Delete(walletDataDir", source[insertStart..insertEnd]);
    }

    [Fact]
    public void TheDuplicateRowRefusalGivesNoInstructionThatOnlyOneOfTheCollidingRowsAllows()
    {
        var source = WalletServiceSource();

        Assert.True(
            !source.Contains("Delete it first if you want to restore a different one.",
                StringComparison.Ordinal),
            "the duplicate-key refusal tells the operator to delete the wallet this store holds. "
            + "BaseDbContextFactory configures EnableRetryOnFailure and the restored row's id is fixed "
            + "client-side, so the row that collided can be this same attempt's own committed row — "
            + "which is born NeedsRecovery and which DeleteWalletAsync refuses to delete. That "
            + "instruction is then unfollowable, in exactly the case where the operator's assets are "
            + "at stake");
    }

    [Fact]
    public void BothOutcomesStillTellTheOperatorTheirBackupIsIntact_BecauseThatIsWhatStopsAPanic()
    {
        Assert.Contains("backup file is undamaged", Refusal());
    }

    [Fact]
    public void NeitherOutcomeNamesAHostFilesystemPath_BecauseTheRefusalIsRenderedVerbatimInABrowser()
    {
        foreach (var refusal in new[] { Refusal() })
        {
            Assert.DoesNotContain("/Users/", refusal);
            Assert.DoesNotContain("/var/", refusal);
            Assert.DoesNotContain(".btcpayserver", refusal);
            Assert.DoesNotContain("rgb-wallets", refusal);
            Assert.DoesNotContain(":\\", refusal);
        }
    }

    [Fact]
    public void TheRefusalNeverConcludesThatTheBackupIsAtFault_BecauseItsTaxonomyIsIncomplete()
    {
        var refusal = Refusal();

        Assert.DoesNotContain("the wallet data in the backup is at fault", refusal);
        Assert.DoesNotContain("Two things reach this point", refusal);
    }

    [Fact]
    public void ADeletionWhoseRowWasAlreadyGoneCompletes_RatherThanReEnablingRgbOnAWalletlessStore()
    {
        var source = WalletServiceSource();
        var deleteStart = source.IndexOf(
            "public async Task DeleteWalletAsync(", StringComparison.Ordinal);
        var deleteEnd = source.IndexOf(
            "\n    public async Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(",
            Math.Max(deleteStart, 0), StringComparison.Ordinal);
        Assert.True(deleteStart > 0 && deleteEnd > deleteStart,
            "could not locate DeleteWalletAsync in RGBWalletService.cs; this pin proves nothing until "
            + "its anchors match the current shape of that method");
        var body = source[deleteStart..deleteEnd];

        Assert.True(
            body.Contains("catch (DbUpdateConcurrencyException noRowWasThereForTheDeleteToAffect)",
                StringComparison.Ordinal),
            "the row delete reports failure when it affects no rows. EnableRetryOnFailure re-executes a "
            + "SaveChangesAsync whose acknowledgement was lost, and the second execution of a DELETE "
            + "whose row is already gone affects nothing and throws exactly this. The wallet record is "
            + "provably absent at that point, yet the caller is told the deletion failed and answers by "
            + "restoring the store's RGB payment configuration and clearing the exclusion — re-enabling "
            + "RGB on a store whose wallet no longer exists, while telling the operator it failed. "
            + $"DeleteWalletAsync read: {body}");
    }

    static string RollbackBody()
    {
        var source = WalletServiceSource();
        var start = source.IndexOf(
            "async Task RollBackTheJustPublishedRestoreAsync(", StringComparison.Ordinal);
        Assert.True(start > 0,
            "could not locate RollBackTheJustPublishedRestoreAsync; this pin proves nothing until its "
            + "anchor matches the current shape of that method");
        var end = source.IndexOf("\n    internal const string IndexerUrlEnvironmentVariable",
            start, StringComparison.Ordinal);
        return source[start..end];
    }
}
