using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RestoreReservedSingleFileNameTests
{
    public RestoreReservedSingleFileNameTests()
    {
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
    }

    const string SyntheticMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string FakeLibFingerprint = "00000000";

    const string SyntheticTransactionId =
        "0000000000000000000000000000000000000000000000000000000000000001";

    const string SyntheticAssetIdWithoutTheRgbPrefix =
        "AAAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF";

    [Fact]
    public void AnEmptyDirectoryAtTheParentLeaseName_IsFoundSoTheWalletIsNeverBrickedByAcquireParent()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, RgbNativeSendLease.ParentFileName));

        Assert.Equal(RgbNativeSendLease.ParentFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Theory]
    [InlineData(RgbNativeSendLease.ParentFileName)]
    [InlineData(RgbNativeSendLease.WorkerFileName)]
    [InlineData(RgbNativeSendLease.WalletAccessFileName)]
    [InlineData(RgbNativeSendLease.RgbRuntimeLockFileName)]
    [InlineData(RgbSendRecoveryJournal.FileName)]
    [InlineData(RgbSendRecoveryJournal.TransferFasciaFileName)]
    [InlineData(RgbSendRecoveryJournal.TransferSignedPsbtFileName)]
    [InlineData(RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName)]
    [InlineData(RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30NonWatchOnlyBdkStoreRepairTempFileName)]
    [InlineData(RgbWalletDirectoryReservedNames
        .PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib)]
    public void EveryReservedSingleFileNameIsFoundAsADirectory_BecauseEachIsOpenedOrRenamedAsARegularFileOnASendOrDeletePath(
        string reservedName)
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, reservedName));

        Assert.Equal(reservedName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void TheReservedNameSetIsExactlyTenPluginOwnedSingleFilePathsPlusThreeWrittenOnlyByThePinnedRgbLib()
    {
        Assert.Equal(
            new[]
            {
                RgbNativeSendLease.ParentFileName,
                RgbNativeSendLease.WorkerFileName,
                RgbNativeSendLease.WalletAccessFileName,
                RgbNativeSendLease.RgbRuntimeLockFileName,
                RgbSendRecoveryJournal.FileName,
                "fascia",
                "signed.psbt",
                "index.dat",
                "stash.dat",
                "state.dat",
                "bdk_db_watch_only.recovering",
                "bdk_db.recovering",
                "consignment_out"
            },
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);

        Assert.All(
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories.Take(10),
            name => Assert.False(
                RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant
                    .Contains(name),
                "the first ten reserved names are sourced from the managed constants that own them "
                + "(RgbNativeSendLease.*, RgbSendRecoveryJournal.*, RgbStockDurability.StockFiles), so they cannot "
                + "drift away from their writer without a compile break"));

        Assert.Equal(
            new[] { "index.dat", "stash.dat", "state.dat" },
            RgbStockDurability.StockFiles);

        Assert.Equal("fascia", RgbSendRecoveryJournal.TransferFasciaFileName);
        Assert.Equal("signed.psbt", RgbSendRecoveryJournal.TransferSignedPsbtFileName);

        Assert.Equal(
            new[] { "bdk_db_watch_only.recovering", "bdk_db.recovering" },
            RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant);

        Assert.Equal(
            new[] { "consignment_out" },
            RgbWalletDirectoryReservedNames
                .NamesWrittenByThePinnedRgbLibsSendEndConsignmentPathAndHavingNoManagedWriter);

        Assert.Equal(
            RgbStockDurability.WatchOnlyBdkStoreFileName + ".recovering",
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
    }

    [Fact]
    public void TheSendConsignmentNameIsAThirdKind_ItHasNoManagedWriterAndIsNotABdkRepairTempSoItsRereadTriggerDiffers()
    {
        var consignment = RgbWalletDirectoryReservedNames
            .NamesWrittenByThePinnedRgbLibsSendEndConsignmentPathAndHavingNoManagedWriter;

        Assert.All(consignment, name => Assert.DoesNotContain(name,
            RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant));

        Assert.All(consignment, name => Assert.False(
            name.EndsWith(
                RgbWalletDirectoryReservedNames
                    .PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib,
                StringComparison.Ordinal),
            $"\"{name}\" carries the BDK repair-temp suffix, so it belongs in the list whose pin asserts that "
            + "suffix; this list exists because the send consignment name is re-read out of gen_consignments "
            + "instead, and folding it into the repair-temp list would falsify that pin's premise"));

        var pluginOwnedConstants = new[]
        {
            RgbNativeSendLease.ParentFileName,
            RgbNativeSendLease.WorkerFileName,
            RgbNativeSendLease.WalletAccessFileName,
            RgbNativeSendLease.RgbRuntimeLockFileName,
            RgbSendRecoveryJournal.FileName,
            RgbSendRecoveryJournal.TransferFasciaFileName,
            RgbSendRecoveryJournal.TransferSignedPsbtFileName
        }.Concat(RgbStockDurability.StockFiles).ToArray();
        Assert.All(consignment, name => Assert.DoesNotContain(name, pluginOwnedConstants));
    }

    [Fact]
    public void TheTwoBdkRepairTempNamesAreDifferentInKind_TheyHaveNoManagedWriterAndAreCreatedByThePinnedRgbLibItself()
    {
        var writtenByRgbLib =
            RgbWalletDirectoryReservedNames.NamesWrittenByThePinnedRgbLibNotByThisPluginAndHavingNoManagedConstant;

        Assert.All(writtenByRgbLib, name => Assert.EndsWith(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib,
            name));

        var pluginOwnedConstants = new[]
        {
            RgbNativeSendLease.ParentFileName,
            RgbNativeSendLease.WorkerFileName,
            RgbNativeSendLease.WalletAccessFileName,
            RgbNativeSendLease.RgbRuntimeLockFileName,
            RgbSendRecoveryJournal.FileName,
            RgbSendRecoveryJournal.TransferFasciaFileName,
            RgbSendRecoveryJournal.TransferSignedPsbtFileName
        }.Concat(RgbStockDurability.StockFiles).ToArray();
        Assert.All(writtenByRgbLib, name => Assert.DoesNotContain(name, pluginOwnedConstants));

        Assert.All(writtenByRgbLib, name => Assert.Equal(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30BdkStoreRepairTempSuffixReReadLoadOrRecoverBdkStoreWhenBumpingRgbLib,
            Path.GetExtension(name)));
    }

    [Fact]
    public void TheRgbLibVersionThisReservedNameWasReadOutOfIsStillTheOneReferenced_SoAnUpgradeForcesARereadOfLoadOrRecoverBdkStore()
    {
        var csproj = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "BTCPayServer.Plugins.RgbUtexo.csproj"));

        Assert.True(
            csproj.Contains("Include=\"RgbLib\" Version=\"0.3.0-beta.30\""),
            "the reserved names \"bdk_db_watch_only.recovering\" and \"bdk_db.recovering\" have no managed constant "
            + "behind them; they were read out of rgb-lib 0.3.0-beta.30's load_or_recover_bdk_store, which composes "
            + "the path as path.with_extension(\"recovering\"), calls fs::remove_file on it while DISCARDING the "
            + "error, and then fails Store::create if a directory still stands there. If RgbLib is being bumped, "
            + "re-read load_or_recover_bdk_store in the new version and re-derive this list before changing this pin");
    }

    [Fact]
    public void TheRgbLibVersionTheSendConsignmentNameWasReadOutOfIsStillTheOneReferenced_SoAnUpgradeForcesARereadOfGenConsignments()
    {
        var csproj = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "BTCPayServer.Plugins.RgbUtexo.csproj"));

        Assert.True(
            csproj.Contains("Include=\"RgbLib\" Version=\"0.3.0-beta.30\""),
            "the reserved name \"consignment_out\" has no managed writer behind it; it was read out of rgb-lib "
            + "0.3.0-beta.30's wallet/mod.rs CONSIGNMENT_FILE, which gen_consignments joins onto "
            + "transfers/<txid>/<asset_id> and hands to Consignment::save_file, i.e. std::fs::File::create. "
            + "File::create cannot truncate a directory and does not unlink it, and gen_consignments runs at the "
            + "very top of send_end_impl, before the signed PSBT is written, so a directory planted there fails "
            + "every send and every send-end recovery replay identically and forever. If RgbLib is being bumped, "
            + "re-read gen_consignments and get_send_consignment_path_impl in the new version and re-derive this "
            + "name before changing this pin");
    }

    [Fact]
    public void ADirectoryAtTheSendRecoveryJournalName_IsFoundBecauseTheJournalIsRenamedOntoThatPathAfterNeedsRecoveryIsAlreadyCommitted()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, RgbSendRecoveryJournal.FileName));

        Assert.Equal(RgbSendRecoveryJournal.FileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheJournalPathDefeatsBothTheFileExistsSendGateAndTheJournalWrite_WhichIsWhyRestoreMustRefuseIt()
    {
        using var walletData = new TempTree();
        var journalPath = RgbSendRecoveryJournal.PathFor(walletData.Path, FakeLibFingerprint);
        Directory.CreateDirectory(journalPath);

        Assert.False(File.Exists(journalPath),
            "File.Exists is false for a directory, so every pre-send and pre-delete gate that spells the "
            + "quarantine check as File.Exists(journal) admits the send");

        var thrown = Record.Exception(() =>
            RgbSendRecoveryJournal.Write(journalPath, RgbSendRecoveryPhase.Staged));

        Assert.True(thrown is IOException or UnauthorizedAccessException,
            $"renaming the journal onto a directory threw {thrown?.GetType().Name ?? "nothing"}; the write must "
            + "fail so that this test keeps describing the real brick, which is a send that has already "
            + "committed NeedsRecovery and then cannot write its journal");
        Assert.True(Directory.Exists(journalPath),
            "the failed write left no directory behind, so the condition would be self-clearing; it is not, "
            + "which is why it must be refused at restore time instead");
    }

    [Fact]
    public void ReservedNamesPresentAsRegularFilesAreAccepted_BecauseAGenuineBackupOfASentWalletCarriesThem()
    {
        using var staging = new TempTree();
        foreach (var reservedName in RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories)
            staging.WriteFile(Path.Combine(FakeLibFingerprint, reservedName), 32);

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ACaseVariantOfAReservedNameIsFound_BecauseMacOsAndWindowsResolveItToTheSameSingleFilePath()
    {
        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, ".SEND-Helper-Parent"));

        Assert.Equal(RgbNativeSendLease.ParentFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void AnOrdinaryWalletTreeIsAccepted_SoTheRefusalCannotStrandAHealthyBackup()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, "rgb_lib_db"), 128);
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "assets"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "media"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "transfers"));

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void TheRefusalMessageNamesTheOffendingEntryAndTellsTheOperatorWhatToDoWithoutShellAccess()
    {
        var message = RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal(
            RgbNativeSendLease.ParentFileName);

        Assert.Contains(RgbNativeSendLease.ParentFileName, message);
        Assert.Contains("Restore a backup taken by this plugin", message);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheParentLeaseName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(RgbNativeSendLease.ParentFileName);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheSendRecoveryJournalName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(RgbSendRecoveryJournal.FileName);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheWatchOnlyBdkStoreRepairTempName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
    }

    [Fact]
    public void ADirectoryAtTheWatchOnlyBdkStoreRepairTempNameIsFound_BecauseRgbLibsSelfHealingRemoveFileCannotRemoveItAndItsErrorIsDiscarded()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, RgbStockDurability.WatchOnlyBdkStoreFileName), 1024);
        staging.MakeDir(Path.Combine(FakeLibFingerprint,
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName));

        Assert.Equal(
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheBdkRepairTempNameSurvivesAFileDelete_WhichIsWhyTheConditionIsNotSelfClearingAndMustBeRefusedAtRestore()
    {
        using var walletData = new TempTree();
        var repairTempPath = Path.Combine(walletData.Path, FakeLibFingerprint,
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30WatchOnlyBdkStoreRepairTempFileName);
        Directory.CreateDirectory(repairTempPath);

        Assert.False(File.Exists(repairTempPath),
            "a directory is invisible to a regular-file existence probe, which is exactly why rgb-lib's "
            + "fs::remove_file of the repair temp path is a no-op it then discards");

        var deleteAttempt = Record.Exception(() => File.Delete(repairTempPath));
        Assert.True(deleteAttempt is UnauthorizedAccessException or IOException,
            $"deleting the planted directory as a file threw {deleteAttempt?.GetType().Name ?? "nothing"}; rgb-lib "
            + "spells this as `let _ = fs::remove_file(&tmp_path)` and drops the error, so the directory stays");
        Assert.True(Directory.Exists(repairTempPath),
            "the planted directory cleared itself, so an interrupted BDK append would self-heal and no refusal "
            + "would be needed; it does not, and every later wallet reconstruction fails at Store::create");
    }

    [Fact]
    public void ADirectoryAtAnyRgbStockDatIsFound_BecauseTheDurabilityFsyncCannotOpenItAndNoPathEverUnlinksIt()
    {
        foreach (var stockFile in RgbStockDurability.StockFiles)
        {
            using var staging = new TempTree();
            foreach (var sibling in RgbStockDurability.StockFiles.Where(n => n != stockFile))
                staging.WriteFile(Path.Combine(FakeLibFingerprint, "rgb", sibling), 64);
            staging.MakeDir(Path.Combine(FakeLibFingerprint, "rgb", stockFile));

            var stockDir = RgbStockDurability.ResolveStockDir(staging.Path, FakeLibFingerprint);
            var thrown = Record.Exception(() => RgbStockDurability.FsyncStockDats(stockDir));
            Assert.True(thrown is FileNotFoundException,
                $"fsyncing a stock dir holding a directory at \"{stockFile}\" threw "
                + $"{thrown?.GetType().Name ?? "nothing"}; File.Exists is false for a directory, so the durability "
                + "barrier cannot open it. This shape alone is NOT the brick: with all three .dat present the "
                + "restore's own eager consistency check fails first and rolls back cleanly, so no wallet ever "
                + "reaches this fsync in this state. What this pins is the supporting property that the refusal "
                + "is real and never self-heals; the permanent variant is the absent-stash.dat panic covered by "
                + "ADirectoryAtAStockDatIsFoundEvenWhenStashDatIsAbsent");
            Assert.True(Directory.Exists(Path.Combine(stockDir, stockFile)),
                "the failed fsync cleared the planted directory, so the condition would be self-healing; it is not, "
                + "and nothing on any later path unlinks it either");

            Assert.Equal(stockFile, RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
        }
    }

    [Fact]
    public void ADirectoryAtAStockDatIsFoundEvenWhenStashDatIsAbsent_BecauseThatShapeMakesRgbLibPanicToAbortAfterTheRestoreIsAlreadyCommitted()
    {
        foreach (var stockFile in RgbStockDurability.StockFiles.Where(name => name != "stash.dat"))
        {
            using var staging = new TempTree();
            staging.MakeDir(Path.Combine(FakeLibFingerprint, "rgb", stockFile));

            Assert.False(File.Exists(Path.Combine(staging.Path, FakeLibFingerprint, "rgb", "stash.dat")),
                "this shape must omit stash.dat; with all three .dat entries present Stock::load fails on a decode "
                + "or an EISDIR read rather than on NotFound, so load_rgb_runtime's or_else does not take its "
                + "recovery branch, rgb-lib returns Err, and the restore's own eager consistency check rolls the "
                + "whole restore back, which is a clean refusal and not a brick");

            Assert.True(Directory.Exists(Path.Combine(staging.Path, FakeLibFingerprint, "rgb", stockFile)),
                $"the planted directory at \"{stockFile}\" cleared itself; it does not, and with stash.dat absent "
                + "Stock::load's first read IS NotFound, so load_rgb_runtime takes its recovery branch and runs "
                + "Stock::in_memory().make_persistent(provider, true).expect(\"unable to save stock\"), which stores "
                + "stash then state then index through std::fs::File::create. File::create over this directory "
                + "returns Err, .expect panics, and rgb-lib's c-ffi has no catch_unwind, so the panic aborts the "
                + "BTCPay process. That abort lands after Directory.Move and after the wallet row is committed with "
                + "NeedsRecovery set, so it cannot be rolled back: every restart re-aborts on wallet open and the "
                + "row can never be deleted");

            Assert.Equal(stockFile, RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
        }
    }

    [Fact]
    public void ADirectoryAtTheTransferFasciaNameIsFound_BecauseTheAckBroadcastRecoveryFsyncsItAsARegularFileAndCannotClearIt()
    {
        using var staging = new TempTree();
        var walletDir = Path.Combine(staging.Path, FakeLibFingerprint);
        var transferRelative = Path.Combine(FakeLibFingerprint, "transfers", SyntheticTransactionId);
        staging.MakeDir(Path.Combine(transferRelative, RgbSendRecoveryJournal.TransferFasciaFileName));

        var thrown = Record.Exception(() =>
            RgbSendRecoveryJournal.FsyncPreSendEndArtifacts(walletDir, SyntheticTransactionId));
        Assert.True(thrown is FileNotFoundException,
            $"fsyncing the pre-send-end artifacts over a directory at the fascia name threw "
            + $"{thrown?.GetType().Name ?? "nothing"}; the required-file probe is File.Exists, which is false for a "
            + "directory, so the ACK-broadcast recovery can never complete");
        Assert.True(Directory.Exists(Path.Combine(
                staging.Path, transferRelative, RgbSendRecoveryJournal.TransferFasciaFileName)),
            "the failed fsync removed the planted directory, so the brick would be self-clearing; it is not, and "
            + "the journal that drives this recovery keeps the wallet quarantined and undeletable");

        Assert.Equal(RgbSendRecoveryJournal.TransferFasciaFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheSignedPsbtNameIsFound_BecauseRenamingOntoItFailsWithoutRemovingItSoTheReplayNeverSucceeds()
    {
        using var staging = new TempTree();
        var walletDir = Path.Combine(staging.Path, FakeLibFingerprint);
        var transferRelative = Path.Combine(FakeLibFingerprint, "transfers", SyntheticTransactionId);
        staging.WriteFile(
            Path.Combine(transferRelative, RgbSendRecoveryJournal.TransferFasciaFileName), 64);
        staging.MakeDir(Path.Combine(transferRelative, RgbSendRecoveryJournal.TransferSignedPsbtFileName));
        var signedPath = Path.Combine(
            staging.Path, transferRelative, RgbSendRecoveryJournal.TransferSignedPsbtFileName);

        var everyStageBeforeTheRename = Record.Exception(() =>
            RgbSendRecoveryJournal.FsyncPreSendEndArtifacts(walletDir, SyntheticTransactionId));
        Assert.True(everyStageBeforeTheRename == null,
            $"the pre-rename stages threw {everyStageBeforeTheRename?.GetType().Name}; this shape must reach the "
            + "File.Move, or the exception recorded below would describe some earlier refusal instead of the "
            + "rename onto the planted directory");

        var transferDir = Path.Combine(staging.Path, transferRelative);
        var temporaryWriteStageProbe = Path.Combine(transferDir, $".probe.{Guid.NewGuid():N}.tmp");
        var theTemporaryWriteStageCannotFailHere = Record.Exception(() =>
        {
            using (var probe = new FileStream(temporaryWriteStageProbe, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                probe.Write("a durable signed psbt"u8);
                probe.Flush(flushToDisk: true);
            }
            File.Delete(temporaryWriteStageProbe);
        });
        Assert.True(theTemporaryWriteStageCannotFailHere == null,
            $"creating, writing and flushing a fresh temporary file in the transfer dir threw "
            + $"{theTemporaryWriteStageCannotFailHere?.GetType().Name}; RestoreAndFsyncAckBroadcastArtifacts "
            + "performs exactly those operations on a fresh-GUID path before its File.Move, and each of them can "
            + "throw an IOException or UnauthorizedAccessException too, so without this probe the assertions below "
            + "would admit a regression that failed at the temporary write and never reached the rename at all");

        var thrown = Record.Exception(() => RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
            walletDir, SyntheticTransactionId, "a durable signed psbt"));
        Assert.False(thrown is DirectoryNotFoundException or FileNotFoundException,
            $"republishing the journalled signed PSBT over a directory threw {thrown?.GetType().Name ?? "nothing"}; "
            + "DirectoryNotFoundException from ResolveTransferDir and FileNotFoundException from FsyncRequiredFile "
            + "are both IOException subclasses raised BEFORE the File.Move this test exists to pin, so admitting "
            + "either would keep the test green while the rename onto the planted directory was never reached");
        Assert.True(thrown is IOException or UnauthorizedAccessException,
            $"republishing the journalled signed PSBT over a directory threw "
            + $"{thrown?.GetType().Name ?? "nothing"}; the rename that publishes it must fail so this test keeps "
            + "describing the real brick");
        Assert.True(Directory.Exists(signedPath),
            "the failed rename removed the planted directory, so the condition would be self-clearing; a rename "
            + "onto a directory does not unlink it, which is why restore must refuse the backup instead");

        Assert.Equal(RgbSendRecoveryJournal.TransferSignedPsbtFileName,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ADirectoryAtTheSendConsignmentNameIsFound_BecauseFileCreateCanNeitherTruncateNorUnlinkItSoEverySendEndReplayFailsIdentically()
    {
        using var staging = new TempTree();
        var consignmentRelative = Path.Combine(
            FakeLibFingerprint, "transfers", SyntheticTransactionId, SyntheticAssetIdWithoutTheRgbPrefix,
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib);
        staging.MakeDir(consignmentRelative);
        var consignmentPath = Path.Combine(staging.Path, consignmentRelative);

        Assert.False(File.Exists(consignmentPath),
            "a directory is invisible to a regular-file existence probe, so nothing on the send path notices it "
            + "before gen_consignments tries to write over it");

        var thrown = Record.Exception(() => File.Create(consignmentPath).Dispose());
        Assert.True(thrown is UnauthorizedAccessException or IOException,
            $"creating the send consignment over a directory threw {thrown?.GetType().Name ?? "nothing"}; "
            + "rgb-lib spells this as Consignment::save_file, i.e. std::fs::File::create, at the very top of "
            + "send_end_impl, so this failure precedes even the signed PSBT write");
        Assert.True(Directory.Exists(consignmentPath),
            "the failed create removed the planted directory, so the condition would be self-clearing; File::create "
            + "does not unlink a directory, so ReconcileWalletRecoveryAsync's send-end replay fails on it forever, "
            + "NeedsRecovery never clears, the wallet can never send or refresh, deletion is refused, and the store "
            + "can no longer create or restore any RGB wallet");

        Assert.Equal(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib,
            RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void ARegularFileAtTheSendConsignmentNameIsAccepted_BecauseEveryGenuineBackupOfAWalletThatHasSentCarriesOne()
    {
        using var staging = new TempTree();
        staging.WriteFile(
            Path.Combine(FakeLibFingerprint, "transfers", SyntheticTransactionId,
                SyntheticAssetIdWithoutTheRgbPrefix,
                RgbWalletDirectoryReservedNames
                    .PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib),
            256);

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheSendConsignmentName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(
            RgbWalletDirectoryReservedNames
                .PinnedRgbLibBeta30SendConsignmentFileNameReReadGenConsignmentsWhenBumpingRgbLib,
            Path.Combine("transfers", SyntheticTransactionId, SyntheticAssetIdWithoutTheRgbPrefix));
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtAnRgbStockDatName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(RgbStockDurability.StockFiles[0], "rgb");
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheTransferFasciaName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(
            RgbSendRecoveryJournal.TransferFasciaFileName,
            Path.Combine("transfers", SyntheticTransactionId));
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsADirectoryAtTheSignedPsbtName_IsRefusedBeforeAnythingIsFinalized()
    {
        await AssertRestoreIsRefusedForDirectoryAt(
            RgbSendRecoveryJournal.TransferSignedPsbtFileName,
            Path.Combine("transfers", SyntheticTransactionId));
    }

    [Fact]
    public void TheCorruptForensicNamesAreNotReserved_BecauseUniqueCorruptPathSkipsAnyPlantedDirectoryAndSelfAvoids()
    {
        Assert.DoesNotContain("bdk_db_watch_only.corrupt",
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);
        Assert.DoesNotContain("bdk_db_watch_only.corrupt.1",
            RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories);

        using var staging = new TempTree();
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "bdk_db_watch_only.corrupt"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "bdk_db_watch_only.corrupt.1"));

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    static async Task AssertRestoreIsRefusedForDirectoryAt(string reservedName, string? nestedUnder = null)
    {
        var relative = nestedUnder == null
            ? Path.Combine(FakeLibFingerprint, reservedName)
            : Path.Combine(FakeLibFingerprint, nestedUnder, reservedName);
        var runner = new StagingShapingRunner(staging =>
            Directory.CreateDirectory(Path.Combine(staging, relative)));
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-reserved-name-{Guid.NewGuid():N}"));
        var svc = BuildService(runner, cfg);
        using var backup = new TempBackup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));

        Assert.Equal(
            RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal(reservedName),
            ex.Message);
        Assert.False(Directory.Exists(runner.StagingDir),
            $"staging dir {runner.StagingDir} survived the refusal; a rejected restore must leave no tree behind");
        var walletsParent = Path.GetDirectoryName(cfg.GetWalletDataDir("probe", "signet"))!;
        var finalizedDirs = Directory.Exists(walletsParent)
            ? Directory.GetDirectories(walletsParent)
            : Array.Empty<string>();
        Assert.True(finalizedDirs.Length == 0,
            $"{finalizedDirs.Length} wallet data dir(s) were finalized under {walletsParent}; the refusal must "
            + "happen before Directory.Move so no unusable wallet dir is ever published");
        try { Directory.Delete(cfg.RgbBaseDir, true); } catch { }
    }

    [Fact]
    public void TheReservedNameCheckRunsInsideRestoreFromBackupAsyncAheadOfDirectoryMove()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "RestoreFromBackupAsync");
        var body = RoslynPins.BodyOf(method);

        var checks = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText:
                "FindDirectoryAtAReservedSingleFileName" })
            .ToList();
        Assert.True(checks.Count == 1,
            $"RestoreFromBackupAsync invokes FindDirectoryAtAReservedSingleFileName {checks.Count} time(s); "
            + "exactly one call must stand between extraction and finalization, or a restored directory at a "
            + "reserved single-file name reaches disk and the wallet can then neither send nor be deleted");
        RoslynPins.AssertBindsToMemberOf(plugin, tree, checks[0].Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService",
            "FindDirectoryAtAReservedSingleFileName",
            "RestoreFromBackupAsync's reserved-single-file-name check");

        var moves = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax access
                        && RoslynPins.NamesBclMember(access, "Directory", "Move"))
            .ToList();
        Assert.True(moves.Count == 1,
            $"RestoreFromBackupAsync performs {moves.Count} Directory.Move call(s); the pin compares the check "
            + "against exactly one finalization point");
        Assert.True(checks[0].SpanStart < moves[0].SpanStart,
            "the reserved-single-file-name check must precede Directory.Move; running it afterwards leaves the hostile "
            + "directory inside the live wallet data dir, which is the permanently unusable wallet this refusal exists to prevent");

        var deferredHost = checks[0].Ancestors()
            .TakeWhile(node => !ReferenceEquals(node, body))
            .FirstOrDefault(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        Assert.True(deferredHost == null,
            $"the only call to FindDirectoryAtAReservedSingleFileName sits inside a {deferredHost?.GetType().Name}; a call "
            + "reachable only through a local function or lambda that nothing invokes satisfies every lexical "
            + "clause above while no restore is ever checked");

        var declarator = checks[0].Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        Assert.True(declarator != null
                    && ReferenceEquals(declarator.Initializer?.Value, checks[0]),
            "the result of FindDirectoryAtAReservedSingleFileName must initialize a local; a call whose returned "
            + "value is discarded satisfies a call-site pin while every hostile backup is still accepted");
        var resultName = declarator!.Identifier.ValueText;
        RoslynPins.AssertNeverReassigned(method, resultName);

        var guards = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => statement.SpanStart > checks[0].SpanStart)
            .Where(statement => statement.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == resultName))
            .Where(statement => statement.Statement is BlockSyntax block
                                && block.Statements.OfType<ThrowStatementSyntax>().Any())
            .ToList();
        Assert.True(guards.Count == 1,
            $"'{resultName}' flows into {guards.Count} condition(s) that directly throw; exactly one must exist, "
            + "or the checked value never refuses the restore it was computed for");
        Assert.True(guards[0].Span.End < moves[0].SpanStart,
            $"the throw guarded by '{resultName}' does not complete before Directory.Move; the refusal must be "
            + "raised while the tree is still staging, not after finalization");

        RoslynPins.AssertNoLocalShadow(method, "FindDirectoryAtAReservedSingleFileName");
    }

    static RGBWalletService BuildService(IRestoreProcessRunner runner, RGBConfiguration cfg)
    {
        var rgbLib = new FakeRgbLib(cfg);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString =
                "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1;Command Timeout=1"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    sealed class StagingShapingRunner : IRestoreProcessRunner
    {
        readonly Action<string> _shape;
        public string StagingDir { get; private set; } = "";

        public StagingShapingRunner(Action<string> shape) => _shape = shape;

        public Task<RestoreRunResult> RunAsync(
            string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)
        {
            StagingDir = stagingDir;
            Directory.CreateDirectory(stagingDir);
            _shape(stagingDir);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"rgb-reserved-name-backup-{Guid.NewGuid():N}.rgb");
            using var fs = File.Create(Path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            using (var enc = zip.CreateEntry("backup.enc").Open())
                enc.Write(new byte[16]);
            using var pub = new StreamWriter(zip.CreateEntry("backup.pub_data").Open());
            pub.Write("""{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""");
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    sealed class TempTree : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rgb-reserved-name-staging-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(Path);

        public void MakeDir(string relative) =>
            Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));

        public void WriteFile(string relative, int bytes)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[bytes]);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
