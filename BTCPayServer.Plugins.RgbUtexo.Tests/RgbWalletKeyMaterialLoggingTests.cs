using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbWalletKeyMaterialLoggingTests
{
    [Fact]
    public void WalletConstructionFailureLogsNoKeyMaterialConfigOrDataPath()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, "Services", "RgbLibService.cs");
        Assert.True(File.Exists(path), "Services/RgbLibService.cs is missing; it holds the wallet construction failure log");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.Text == "CreateWalletInternal");
        Assert.True(method != null, "CreateWalletInternal is absent from RgbLibService; the logging pin cannot be checked");

        var catchClause = method!.DescendantNodes().OfType<CatchClauseSyntax>().SingleOrDefault();
        Assert.True(catchClause != null,
            "CreateWalletInternal no longer has exactly one catch clause; re-derive this pin against the new shape "
            + "before adjusting it — the invariant is that no wallet-construction failure path logs key material");

        var logCalls = catchClause!.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax member
                        && member.Name.Identifier.Text.StartsWith("Log", StringComparison.Ordinal))
            .ToList();
        Assert.True(logCalls.Count > 0, "the wallet-construction failure path no longer logs at all; re-derive this pin");

        var forbidden = new[] { "keysJson", "configJson", "masterFingerprint", "dataDir", "xpubVanilla", "xpubColored" };

        foreach (var logCall in logCalls)
        {
            foreach (var argument in logCall.ArgumentList.Arguments)
            {
                var leakedIdentifier = argument.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Select(id => id.Identifier.Text)
                    .FirstOrDefault(name => forbidden.Contains(name));
                Assert.True(leakedIdentifier == null,
                    $"the wallet-construction failure path passes '{leakedIdentifier}' to the logger; the serialized "
                    + "keys config carries both account xpubs, and anyone with log access can derive every address "
                    + "the wallet will ever use from them");
            }
        }
    }

    [Fact]
    public void ConstructionFailureNeitherRethrowsNorLogsTheUnsanitizedNativeMessage()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, "Services", "RgbLibService.cs");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path);
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.Text == "CreateWalletInternal");
        Assert.True(method != null, "CreateWalletInternal is absent from RgbLibService; this pin cannot be checked");

        var catchClause = method!.DescendantNodes().OfType<CatchClauseSyntax>().SingleOrDefault();
        Assert.True(catchClause != null,
            "CreateWalletInternal no longer has exactly one catch clause; re-derive this pin before adjusting it");
        var body = catchClause!.ToString();

        Assert.False(catchClause.DescendantNodes().OfType<ThrowStatementSyntax>().Any(t => t.Expression == null),
            "the wallet-construction failure path rethrows the native exception unchanged. Its message is NOT safe to "
            + "propagate: rgb-lib maps a bdk_wallet descriptor mismatch to Error::IO { details } holding bdk's own "
            + "string, and bdk formats LoadMismatch::Descriptor as 'loaded <descriptor>, expected <descriptor>' with "
            + "each descriptor printed in full — wpkh([fingerprint/path]xpub/*). Three sinks log that rethrown "
            + "exception raw (RgbLibService here, RGBWalletService's consistency check, RGBInvoiceListener once per "
            + "poll cycle because Lazy<T> caches the faulted construction) and two more render ex.Message into HTML "
            + "as RGBController's ConnectionError, so the exception boundary is the only place that covers them all");

        Assert.Contains("RgbNativeMessageSanitizer.Sanitize", body);
        Assert.False(body.Contains("LogError(ex,", StringComparison.Ordinal)
                     || body.Contains("LogError(ex ", StringComparison.Ordinal),
            "the failure path hands the raw exception to the logger, which renders ex.ToString() and with it the "
            + "unsanitized native message");
        Assert.DoesNotContain("ex.Message", body.Replace("Sanitize(ex.Message)", ""));
    }

    [Fact]
    public void ADotnetRuntimeBringUpFailure_NamesItsTypeAndTheLogButNotTheServerPathItsMessageCarries()
    {
        const string installDirBearingMessage =
            "Unable to load shared library 'rgblibcffi': "
            + "/Users/someone/.btcpayserver/Plugins/BTCPayServer.Plugins.RgbUtexo/librgblibcffi.dylib";

        var shown = RgbLibService.WalletBringUpFailureForTheOperator(
            "w1", "signet", new DllNotFoundException(installDirBearingMessage), installDirBearingMessage);

        Assert.DoesNotContain("/Users/", shown);
        Assert.DoesNotContain(".btcpayserver", shown);
        Assert.Contains("DllNotFoundException", shown);
        Assert.Contains(
            RgbLibService.DotnetRuntimeDetailWithheldBecauseItNamesServerFilesystemPaths, shown);
    }

    [Fact]
    public void ANativeBringUpFailure_StillReachesTheOperatorVerbatim_BecauseItIsHisOnlyIndexerDiagnosis()
    {
        const string nativeDetail = "Indexer error: failed to connect to https://esplora.example.com";

        var shown = RgbLibService.WalletBringUpFailureForTheOperator(
            "w1", "signet", new RgbLib.RgbLibException(nativeDetail), nativeDetail);

        Assert.Contains(nativeDetail, shown);
        Assert.DoesNotContain(
            RgbLibService.DotnetRuntimeDetailWithheldBecauseItNamesServerFilesystemPaths, shown);
    }

    [Fact]
    public void ThePluginsOwnRgbLibFailure_StillReachesTheOperatorVerbatim()
    {
        const string nativeDetail = "go_online failed: Invalid indexer";

        var shown = RgbLibService.WalletBringUpFailureForTheOperator(
            "w1", "signet", new RgbLibException(nativeDetail), nativeDetail);

        Assert.Contains(nativeDetail, shown);
    }

    [Fact]
    public void AFrameworkIoBringUpFailure_LosesTheWalletDataDirectoryItsMessageCarries()
    {
        const string walletDirBearingMessage =
            "Access to the path '/Users/someone/.btcpayserver/Main/rgb-wallets/w1' is denied.";

        var shown = RgbLibService.WalletBringUpFailureForTheOperator(
            "w1", "signet", new UnauthorizedAccessException(walletDirBearingMessage),
            walletDirBearingMessage);

        Assert.DoesNotContain("rgb-wallets", shown);
        Assert.Contains("UnauthorizedAccessException", shown);
    }

    [Theory]
    [InlineData("I/O error: Descriptor mismatch for Internal keychain: loaded wpkh([1a2b3c4d/84'/1'/0']tpubDCruH7eLXMYBeuYhZzqhehsPrZr8xsmGU5Sz4dtwJhZxU8UYaYCdMBZdY5norXcbE2sy4zJjhm7L475LcKKszgupYzPEPtMZ3viuWoHUVt5/0/*), expected None")]
    [InlineData("bad key xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFjqJoCu1Rupje8YtGqsefD265TMg7usUDFdp6W1EGMcet8")]
    public void SanitizerRemovesEveryExtendedKeyAndKeyOriginFromANativeMessage(string nativeMessage)
    {
        var sanitized = RgbNativeMessageSanitizer.Sanitize(nativeMessage);

        Assert.DoesNotContain("pub", sanitized.Replace(RgbNativeMessageSanitizer.RedactionPlaceholder, ""));
        Assert.DoesNotContain("1a2b3c4d", sanitized);
        Assert.Contains(RgbNativeMessageSanitizer.RedactionPlaceholder, sanitized);
    }

    [Fact]
    public void SanitizerRedactsKeysItHasNeverSeenSoAForeignBackupsDescriptorIsCoveredToo()
    {
        var foreignKeyNeverHeldByThisPlugin =
            "vpub5ZpXnLZjmhkbLh3vwv1MSDYktoQ2cRb2v8YQFyCkfhqVKZdRLtPdM3Ns2fEJ6yWv5RxrqDFRvxNsi7wtEfW6vy7oEUAKUb2NxwZLR3H8Aqu";
        var sanitized = RgbNativeMessageSanitizer.Sanitize($"loaded wpkh({foreignKeyNeverHeldByThisPlugin}/0/*)");

        Assert.DoesNotContain(foreignKeyNeverHeldByThisPlugin, sanitized);
    }

    [Fact]
    public void SanitizerLeavesAnOrdinaryDiagnosticIntactSoAWedgedWalletStaysDiagnosable()
    {
        const string ordinary = "Indexer error: failed to connect to https://esplora.example.com after 3 attempts";

        Assert.Equal(ordinary, RgbNativeMessageSanitizer.Sanitize(ordinary));
    }

    [Fact]
    public void BackupFingerprintMismatchLogsNoFingerprintValue()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, "Services", "RGBWalletService.cs");
        var source = File.ReadAllText(path);
        var marker = "Mnemonic/backup mismatch";
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "the mnemonic/backup mismatch log is gone; re-derive this pin");
        var statement = source.Substring(index, Math.Min(500, source.Length - index));

        Assert.False(statement.Contains("expectedFingerprint", StringComparison.Ordinal),
            "the mismatch log still emits the fingerprint derived from the operator's recovery phrase");
        Assert.False(statement.Contains("string.Join(\",\", stagingFingerprintDirs)", StringComparison.Ordinal),
            "the mismatch log still emits the fingerprints found inside the backup archive");
    }
}
