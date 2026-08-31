using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreIndexerOutageIsNotBlamedOnTheMnemonicTests
{
    const string MnemonicMismatchClaim = "The mnemonic does not match the keys in this backup.";
    const string SyntheticHostPath = "/Users/someone/.btcpayserver/Main/rgb-wallets/w1/rgb_runtime.lock";

    const string RgbLibsUnreachableIndexerDetail =
        "Invalid indexer: not a valid electrum nor esplora server";

    static Exception AnUnreachableIndexerAsItArrivesFromRgbLibService()
    {
        var native = new RgbLib.RgbLibException(RgbLibsUnreachableIndexerDetail);
        return new RgbWalletConstructionException(
            RgbLibService.WalletBringUpFailureForTheOperator(
                "w1", "signet", native, RgbLibsUnreachableIndexerDetail));
    }

    public static TheoryData<Exception> FailuresThatReachTheBringOnlineStepOfARestore() => new()
    {
        AnUnreachableIndexerAsItArrivesFromRgbLibService(),
        new RgbLibException("get_address failed: Indexer error: connection refused"),
        new InvalidOperationException(
            "native send helper may still own this wallet — refusing concurrent rgb-lib access"),
        new IOException($"The process cannot access the file '{SyntheticHostPath}' because it is in use.")
    };

    [Theory]
    [MemberData(nameof(FailuresThatReachTheBringOnlineStepOfARestore))]
    public void TheRefusalNeverAssertsTheRecoveryPhraseIsWrong_BecauseThatPhraseAlreadyPassedTheFingerprintGate(
        Exception failure)
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet", failure);

        Assert.True(!refusal.Contains(MnemonicMismatchClaim, StringComparison.Ordinal),
            $"the refusal for a {failure.GetType().Name} at the bring-online step read \"{refusal}\" and "
            + "still tells the operator their recovery phrase does not match the backup. Every failure in "
            + "this theory is reached only AFTER the fingerprint gate has confirmed that the phrase's "
            + "master fingerprint names a directory this backup actually carries, so that claim is false "
            + "here by construction. An operator restoring a funded wallet who believes it discards the "
            + "only phrase that can ever open the backup, which strands the assets permanently");
        Assert.Contains("NOT evidence that your recovery phrase is wrong", refusal);
        Assert.Contains("your backup file is undamaged", refusal);
        Assert.Contains(RGBWalletService.IndexerUrlEnvironmentVariable, refusal);
    }

    [Theory]
    [MemberData(nameof(FailuresThatReachTheBringOnlineStepOfARestore))]
    public void TheRefusalNamesNoHostFilesystemPath_SoItCanBeShownToAStoreOwnerVerbatim(Exception failure)
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet", failure);

        Assert.DoesNotContain("/Users/", refusal);
        Assert.DoesNotContain("/var/", refusal);
        Assert.DoesNotContain(".btcpayserver", refusal);
        Assert.DoesNotContain("rgb-wallets", refusal);
        Assert.DoesNotContain(":\\", refusal);
    }

    [Fact]
    public void AnUnreachableIndexerCarriesRgbLibsOwnDiagnosisToTheOperator_BecauseNothingElseIdentifiesIt()
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet", AnUnreachableIndexerAsItArrivesFromRgbLibService());

        Assert.Contains(RgbLibsUnreachableIndexerDetail, refusal);
        Assert.Contains("signet", refusal);
        Assert.True(refusal.Contains("restore the same backup again", StringComparison.Ordinal),
            $"the refusal read \"{refusal}\"; a store Owner has no shell access, so the refusal has to "
            + "state that retrying the same unmodified backup is the whole remedy. Without it the "
            + "operator has no way to learn that the failure was transient");
    }

    [Fact]
    public void ADotnetRuntimeFailureHasItsTextWithheld_BecauseTheRuntimeInterpolatesServerPaths()
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet",
            new UnauthorizedAccessException($"Access to the path '{SyntheticHostPath}' is denied."));

        Assert.DoesNotContain(SyntheticHostPath, refusal);
        Assert.Contains("UnauthorizedAccessException", refusal);
        Assert.Contains("BTCPay server log", refusal);
    }

    [Fact]
    public void TheRefusalReachesTheOperatorVerbatim_BecauseItIsThrownAsAnInvalidOperationException()
    {
        var refusal = RGBWalletService.RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
            "signet", AnUnreachableIndexerAsItArrivesFromRgbLibService());

        Assert.Equal(refusal, RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new InvalidOperationException(refusal), RgbOperatorFacingFailure.EscalateToServerLogs));
    }

    [Fact]
    public void TheMnemonicMismatchClaimSurvivesOnlyAtTheFingerprintGate_NotAtTheBringOnlineStep()
    {
        var source = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "Services", "RGBWalletService.cs"))
            .Replace("\r\n", "\n");

        var occurrences = source.Split(MnemonicMismatchClaim).Length - 1;
        Assert.True(occurrences == 1,
            $"the mnemonic-mismatch claim appears {occurrences} times in RGBWalletService.cs. It has "
            + "exactly one site where it is true — the fingerprint gate, where the phrase's master "
            + "fingerprint matches no directory in the backup. A second occurrence means the "
            + "bring-online step is again asserting a wrong phrase for failures it cannot distinguish "
            + "from an indexer outage");

        var bringOnlineStart = source.IndexOf(
            "await _rgbLib.GetOrCreateWalletAsync(wallet.Id, ct);", StringComparison.Ordinal);
        var bringOnlineEnd = source.IndexOf(
            "_signerProvider.RegisterSigner(wallet.Id, mnemonic, network);",
            Math.Max(bringOnlineStart, 0), StringComparison.Ordinal);
        Assert.True(bringOnlineStart > 0 && bringOnlineEnd > bringOnlineStart,
            "could not locate the restore bring-online step in RGBWalletService.cs; this pin cannot "
            + "prove anything until its anchors are updated to the current shape of that step");
        var bringOnlineStep = source[bringOnlineStart..bringOnlineEnd];

        Assert.DoesNotContain(MnemonicMismatchClaim, bringOnlineStep);
        Assert.Contains("catch (OperationCanceledException ex)", bringOnlineStep);
        Assert.Contains("RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline", bringOnlineStep);
    }
}
