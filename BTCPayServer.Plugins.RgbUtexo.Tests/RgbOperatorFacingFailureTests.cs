using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbOperatorFacingFailureTests
{
    const string Fallback = "Failed to do the thing. Check server logs for details.";
    const string HostPath = "/Users/someone/.btcpayserver/Main/rgb-wallets/w1/rgb_runtime.lock";

    public static TheoryData<string> RefusalsTheOperatorMustBeAbleToActOn() => new()
    {
        RGBWalletService.RestoreFoundThisStoreAlreadyHoldsAWalletRecordRefusal,
        "Backup could not be loaded with the supplied mnemonic. The mnemonic does not match the keys in this backup.",
        "Restored wallet data exceeds size limit (512MB > 256MB)",
        "A wallet restore was attempted recently. Try again in 42 seconds.",
        "Another wallet restore is already in progress. Try again once it completes.",
        RgbRestoreValidationGate.ConcurrentParentSideValidationRefusalMessage,
        "Insufficient SETL spendable balance: have 3, need 9",
        "Invoice requires exactly 7 — you entered 9",
        "RGB invoice network 'testnet' does not match wallet network 'signet'.",
        "Amount after fee would be below dust limit (546 sats)",
        "Insufficient funds after fee. Maximum sendable: 1,200 sats (from 1,500 sats, fee ~300 sats)",
        RGBWalletService.ReservedDirectoryNameUsedAsRegularFileRefusal("media_files"),
        RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal("rgb_runtime.lock"),
        "Refusing to restore this backup: the key derivation cost is too high. If this backup is genuinely "
        + "yours, raise the restore scrypt memory limit (RGB_RESTORE_SCRYPT_MEMORY_CAP_BYTES) and try again.",
        RgbRestoreUploadBound.RefusalMessage(50 * 1024 * 1024),
        RgbBackupScryptGuard.UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath
    };

    [Theory]
    [MemberData(nameof(RefusalsTheOperatorMustBeAbleToActOn))]
    public void PluginAuthoredRefusal_ReachesTheOperatorVerbatim_BecauseItIsActedOnWithoutShellAccess(
        string refusal)
    {
        var shown = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new InvalidOperationException(refusal), Fallback);

        Assert.Equal(refusal, shown);
    }

    [Theory]
    [MemberData(nameof(RefusalsTheOperatorMustBeAbleToActOn))]
    public void PluginAuthoredRefusal_NamesNoHostFilesystemPath_SoPassingItThroughLeaksNothing(string refusal)
    {
        Assert.DoesNotContain("/Users/", refusal);
        Assert.DoesNotContain("/var/", refusal);
        Assert.DoesNotContain(".btcpayserver", refusal);
        Assert.DoesNotContain("rgb-wallets", refusal);
        Assert.DoesNotContain(":\\", refusal);
    }

    public static TheoryData<Exception> HostPathBearingFailures() => new()
    {
        new IOException($"The process cannot access the file '{HostPath}' because it is in use."),
        new FileNotFoundException($"Could not find file '{HostPath}'.", HostPath),
        new DirectoryNotFoundException($"Could not find a part of the path '{HostPath}'."),
        new UnauthorizedAccessException($"Access to the path '{HostPath}' is denied."),
        new AggregateException(
            "Wallet deletion failed and store configuration rollback also failed",
            new IOException($"Directory not empty : '{HostPath}'"),
            new InvalidOperationException("rollback failed"))
    };

    [Theory]
    [MemberData(nameof(HostPathBearingFailures))]
    public void HostPathBearingFailure_IsReplacedByTheFallback_SoNoAbsolutePathReachesAStoreOwner(Exception ex)
    {
        var shown = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(ex, Fallback);

        Assert.Equal(Fallback, shown);
        Assert.DoesNotContain(HostPath, shown);
        Assert.DoesNotContain("/Users/", shown);
    }

    [Fact]
    public void MissingWalletLookupFailure_ReachesTheOperator_BecauseItNamesWhatIsAbsentNotWhereItLives()
    {
        Assert.Equal("Wallet not found", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new KeyNotFoundException("Wallet not found"), Fallback));
    }

    [Fact]
    public void RgbLibFailure_ReachesTheOperator_BecauseTheNativeDetailIsTheOnlyIndexerDiagnosisAvailable()
    {
        const string nativeDetail = "list_unspents failed: Indexer error: connection refused";

        Assert.Equal(nativeDetail, RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new RgbLibException(nativeDetail), Fallback));
    }

    [Fact]
    public void RgbLibFailurePassesThroughByDesign_EvenThoughRgbLibHasPathInterpolatingErrorVariants()
    {
        const string detailShapedLikeRgbLibsPathBearingVariant =
            "backup failed: The file already exists: /Users/someone/tmp/rgb-backup-wallet.rgb";

        var shown = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new RgbLibException(detailShapedLikeRgbLibsPathBearingVariant), Fallback);

        Assert.True(
            shown == detailShapedLikeRgbLibsPathBearingVariant,
            "RgbLibException passes through to the operator BY DESIGN and this pin records the known, "
            + "accepted exception to that rather than leaving it to be rediscovered. Why pass through: the "
            + "native detail is a store Owner's only self-service diagnosis of indexer-unreachable, "
            + "insufficient-bitcoins and invalid-invoice conditions, and a store Owner is not necessarily a "
            + "server admin, so suppressing it turns an actionable refusal into a permanent false REJECT. "
            + "The exception: rgb-lib 0.3.0-beta.30 src/error.rs interpolates a caller-supplied path into "
            + "the Display of EmptyFile (line 77, \"Empty file: {file_path}\"), FileAlreadyExists (line 105, "
            + "\"The file already exists: {path}\"), InvalidFilePath (line 257, \"Invalid file path: "
            + "{file_path}\") and WalletDirAlreadyExists (line 655), so NO predicate feeding this helper may "
            + "ever be read, or renamed, as a host-path-freeness guarantee. Measured reachability from this "
            + "plugin: EmptyFile and InvalidFilePath are raised only in src/wallet/offline.rs for CFA/UDA "
            + "media paths and inspect_rgb_transfer's fascia path, and the plugin calls neither; "
            + "FileAlreadyExists is raised only in src/wallet/backup.rs backup_raw, which the plugin reaches "
            + "only via RgbLibService.Backup, which frees the native error string WITHOUT reading it into "
            + "the message and throws this plugin's Services.RgbLibException with the constant text "
            + "\"Failed to backup\" — so the interpolated path is discarded before it can reach an "
            + "operator, even though that constant, unlike the package wrapper's differently-typed "
            + "exception it replaced, is now itself trusted and shown verbatim rather than falling back. "
            + "Every plugin-side RgbLibException is built in RgbLibService/RGBWalletService from "
            + "the direct rgblib_* P/Invoke surface, which reaches no path-interpolating variant. Re-check "
            + "that surface before trusting this pin if a new rgblib_* entry point or a rgb-lib bump lands.");
    }

    [Fact]
    public void UnrecognisedFailure_IsReplacedByAFallbackThatStillNamesWhatFailed()
    {
        var shown = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new Exception("some internal detail"), Fallback);

        Assert.Equal(Fallback, shown);
        Assert.Contains("Failed to do the thing", shown);
        Assert.Contains(RgbOperatorFacingFailure.EscalateToServerLogs, shown);
    }

    [Fact]
    public void ControllerHasNoBareExceptionMessageLeftForTheOperator_ExceptTheOneTypeNarrowedCatch()
    {
        var source = ControllerSource();
        var bareSites = source
            .Split('\n')
            .Select((line, index) => (line: line.Trim(), number: index + 1))
            .Where(l => l.line.Contains("ex.Message", StringComparison.Ordinal))
            .ToList();

        Assert.True(bareSites.Count == 1,
            "Every operator-facing catch must route through OperatorFacingLayerMessageOrFallback, whose "
            + "trusted set is the single place this plugin decides which exception text a store Owner may "
            + "see. A bare ex.Message outside a catch already narrowed to a trusted type either leaks a "
            + "runtime message naming a host path, or silently disagrees with the shared set — the "
            + "send-asset handler did the latter and dropped every RgbLibException diagnosis. Bare sites "
            + "found: " + string.Join(", ", bareSites.Select(l => $"line {l.number}: {l.line}")));

        Assert.Contains(
            "catch (InvalidOperationException ex)\n        {\n"
            + "            ModelState.AddModelError(\"BackupFile\", ex.Message);",
            source.Replace("\r\n", "\n"));

        Assert.DoesNotContain("ex is InvalidOperationException or KeyNotFoundException ? ex.Message",
            source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rgb:AAAA-BBBB-CCCC")]
    [InlineData("")]
    [InlineData("rgb:")]
    public void AllocationAbbreviation_ReturnsAShortContractIdUnchanged_SoTheUtxosPageCannotThrowAndHideTheWallet(
        string assetId)
    {
        Assert.Equal(assetId, new RGBAllocationViewModel { AssetId = assetId }
            .AssetIdAbbreviatedKeepingHeadAndTail);
    }

    [Theory]
    [InlineData("rgb:AAAA-BBBB-CCCC")]
    [InlineData("")]
    [InlineData("rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-TMsGMSA")]
    public void AllocationAbbreviation_AgreesWithTheAssetHelper_SoOnlyOneAbbreviationRuleExists(string assetId)
    {
        Assert.Equal(
            new RGBAssetViewModel { AssetId = assetId }.AssetIdAbbreviatedKeepingHeadAndTail,
            new RGBAllocationViewModel { AssetId = assetId }.AssetIdAbbreviatedKeepingHeadAndTail);
    }

    [Fact]
    public void UtxosView_AbbreviatesThroughTheSharedHelper_SoAShortContractIdCannotTakeOutThePage()
    {
        var view = ViewSource("Utxos.cshtml");

        Assert.Contains("@alloc.AssetIdAbbreviatedKeepingHeadAndTail", view);
        Assert.DoesNotContain("@alloc.AssetId.Substring", view);
    }

    [Fact]
    public void SettingsView_AbbreviatesThroughTheSharedHelper_SoAShortContractIdCannotTakeOutThePage()
    {
        var view = ViewSource("Settings.cshtml");

        Assert.Contains("@asset.AssetIdAbbreviatedKeepingHeadAndTail", view);
        Assert.DoesNotContain("@asset.AssetId[..20]", view);
    }

    static string ControllerSource() => ReadRepoFile(
        Path.Combine("Controllers", "RGBController.cs"));

    static string ViewSource(string fileName) => ReadRepoFile(
        Path.Combine("Views", "RGB", fileName));

    static string ReadRepoFile(string relativePath)
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, relativePath);
        Assert.True(File.Exists(path), $"Could not locate {relativePath} at {path}");
        return File.ReadAllText(path);
    }
}
