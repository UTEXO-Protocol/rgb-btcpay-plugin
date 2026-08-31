using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// A BTC send broadcasts once and then reads the indexer's reply. Losing that reply is not the same
/// as the broadcast being refused, and the difference decides whether sending again pays the
/// destination twice. These cover the two halves of telling them apart: the proof that a transaction
/// the indexer hands back really is the one this server signed, and the control flow that refuses to
/// report an outcome the server did not establish.
/// </summary>
public class RgbSendBtcBroadcastOutcomeTests
{
    const string WalletServiceFile = "Services/RGBWalletService.cs";

    static Exception OperatorFacingBroadcastFailure() =>
        new InvalidOperationException("Electrum: connection closed");

    static Transaction SampleTransaction(uint lockTime)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(1000),
            new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ScriptPubKey));
        tx.LockTime = lockTime;
        return tx;
    }

    [Fact]
    public void ATransactionTheIndexerHandsBackUnchangedCountsAsProofItReachedTheNetwork()
    {
        var tx = SampleTransaction(101);

        Assert.True(RGBWalletService.TheIndexerReturnedExactlyThisTransaction(
            tx.ToHex(), tx, Network.RegTest));
    }

    [Fact]
    public void TheProofSurvivesTheWhitespaceAnEsploraHexBodyCanCarry()
    {
        var tx = SampleTransaction(102);

        Assert.True(RGBWalletService.TheIndexerReturnedExactlyThisTransaction(
            $"  {tx.ToHex()}\n", tx, Network.RegTest));
    }

    [Fact]
    public void AWitnessStrippedCopyIsNotProofBecauseTheTxidWouldStillMatch()
    {
        var signed = SampleTransaction(103);
        signed.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[64]));
        var stripped = Transaction.Parse(signed.ToHex(), Network.RegTest);
        stripped.Inputs[0].WitScript = WitScript.Empty;

        Assert.Equal(signed.GetHash(), stripped.GetHash());
        Assert.NotEqual(signed.GetWitHash(), stripped.GetWitHash());

        Assert.False(RGBWalletService.TheIndexerReturnedExactlyThisTransaction(
            stripped.ToHex(), signed, Network.RegTest),
            "a txid commits to no witness, so comparing only GetHash would let an indexer hand back a "
            + "witness-stripped or witness-corrupted copy and have it read as proof the signed "
            + "transaction reached the network");
    }

    [Fact]
    public void ADifferentTransactionIsNotProofTheSignedOneReachedTheNetwork()
    {
        var signed = SampleTransaction(104);
        var other = SampleTransaction(105);
        Assert.NotEqual(signed.GetHash(), other.GetHash());

        Assert.False(RGBWalletService.TheIndexerReturnedExactlyThisTransaction(
            other.ToHex(), signed, Network.RegTest),
            "an indexer answering with some other transaction would otherwise be read as proof the "
            + "signed one was broadcast, and the operator would be told a payment is on its way that "
            + "never left this server");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not hex at all")]
    [InlineData("deadbeef")]
    public void UnreadableIndexerOutputIsNeverReadAsProof(string rawHex)
    {
        var tx = SampleTransaction(106);

        Assert.False(RGBWalletService.TheIndexerReturnedExactlyThisTransaction(
            rawHex, tx, Network.RegTest),
            "this runs on the failure arm of a broadcast, so it must answer 'not established' rather "
            + "than throw a second exception over the first");
    }

    [Fact]
    public void TheRefusalNamesTheTransactionTheOperatorHasToLookUp()
    {
        var txid = SampleTransaction(107).GetHash().ToString();
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(txid, OperatorFacingBroadcastFailure());

        Assert.Contains(txid, refusal, StringComparison.Ordinal);
        Assert.Contains("block explorer", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRefusalNeverClaimsTheTransactionWasNotSent()
    {
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(
            SampleTransaction(108).GetHash().ToString(), OperatorFacingBroadcastFailure());

        Assert.Contains("could not confirm", refusal, StringComparison.OrdinalIgnoreCase);
        foreach (var falseClaim in new[]
                 {
                     "was not sent", "did not send", "nothing was broadcast",
                     "sending again is safe", "nothing was sent"
                 })
            Assert.DoesNotContain(falseClaim, refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRefusalRepeatsAnOperatorFacingBroadcastReasonRatherThanReplacingIt()
    {
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(
            SampleTransaction(111).GetHash().ToString(),
            new InvalidOperationException("Electrum error: min relay fee not met"));

        Assert.Contains("min relay fee not met", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSendsTheOperatorToTheLogWhenTheReasonIsNotTheirsToRead()
    {
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(
            SampleTransaction(112).GetHash().ToString(),
            new IOException("/srv/btcpay/keys/wallet.dat unreachable"));

        Assert.DoesNotContain("wallet.dat", refusal, StringComparison.Ordinal);
        Assert.Contains("server log", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNetworkProbeCarriesItsOwnDeadlineSoAStalledIndexerCannotHoldTheSendLock()
    {
        Assert.True(RGBWalletService.BroadcastReconciliationDeadline > TimeSpan.Zero
            && RGBWalletService.BroadcastReconciliationDeadline <= TimeSpan.FromMinutes(1),
            "the probe runs while this wallet's send lock is held, so its deadline has to be short "
            + "enough that a black-holed indexer cannot block every later send on that wallet");

        var probe = MethodNamed("TheNetworkAlreadyHoldsTheSignedTransactionAsync");
        var tokenArguments = probe.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) is "ConnectAsync" or "GetRawTransactionAsync")
            .SelectMany(i => i.ArgumentList.Arguments)
            .Select(a => a.Expression.ToString())
            .ToList();

        Assert.True(tokenArguments.Count > 0, "the probe makes no network call this pin can read");
        Assert.DoesNotContain("CancellationToken.None", tokenArguments);
    }

    [Fact]
    public void TheRefusalWarnsThatSendingAgainCanPayTwice()
    {
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(
            SampleTransaction(109).GetHash().ToString(), OperatorFacingBroadcastFailure());

        Assert.Contains("second time", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheRefusalCarriesNoServerFilesystemLocation()
    {
        var refusal = RGBWalletService.RefusalForABroadcastThisServerCouldNotAccountFor(
            SampleTransaction(110).GetHash().ToString(), OperatorFacingBroadcastFailure());

        Assert.DoesNotContain("/", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBroadcastReplyIsReadInsideAHandlerRatherThanAllowedToEscape()
    {
        var broadcast = BroadcastInvocationInTheBtcSendPath();

        Assert.True(
            broadcast.Ancestors().OfType<TryStatementSyntax>().Any(),
            "the BTC broadcast call is no longer inside a try, so a lost reply once again reaches the "
            + "operator as an undifferentiated failure and the next attempt can pay the destination "
            + "a second time");
    }

    [Fact]
    public void NothingIsReportedFromTheBroadcastFailureArmWithoutFirstAskingTheNetwork()
    {
        var handler = BroadcastFailureHandler();
        var probe = handler.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "TheNetworkAlreadyHoldsTheSignedTransactionAsync")
            .ToList();

        Assert.True(probe.Count == 1,
            $"the broadcast failure handler calls the network probe {probe.Count} times; exactly one "
            + "call is what makes the reported outcome something this server established rather than "
            + "assumed");

        var throwStatements = handler.DescendantNodes().OfType<ThrowStatementSyntax>().ToList();
        Assert.True(throwStatements.Count == 1,
            $"the handler throws {throwStatements.Count} times; this pin reads exactly one refusal");
        Assert.True(throwStatements[0].SpanStart > probe[0].SpanStart,
            "the handler refuses before it asks the network whether the transaction is already there, "
            + "so a lost reply is reported as a failure even when the payment is on its way");
    }

    [Fact]
    public void TheNetworkProbeAnswersNotEstablishedRatherThanThrowingOverTheBroadcastFailure()
    {
        var probe = MethodNamed("TheNetworkAlreadyHoldsTheSignedTransactionAsync");
        var catches = probe.DescendantNodes().OfType<CatchClauseSyntax>().ToList();

        Assert.True(catches.Count == 1,
            $"the probe has {catches.Count} catch clauses; this pin reads exactly one");

        var returns = catches[0].Block.DescendantNodes().OfType<ReturnStatementSyntax>().ToList();
        Assert.True(
            returns.Count == 1
            && returns[0].Expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.FalseLiteralExpression),
            "the probe's catch must answer false. Answering true would report a payment as on its way "
            + "on the strength of an unreachable indexer, and rethrowing would replace the actionable "
            + "refusal with whatever the probe failed with.");
    }

    [Fact]
    public void TheElectrumTlsHandshakeObservesTheDeadlineItsCallerPassedIn()
    {
        var tree = PluginCompilation.Shared.Tree("Services/ElectrumClient.cs");
        var handshakes = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "AuthenticateAsClientAsync")
            .ToList();

        Assert.True(handshakes.Count == 1,
            $"expected exactly one TLS handshake in ElectrumClient, found {handshakes.Count}");
        Assert.Contains("ct", handshakes[0].ArgumentList.Arguments
            .Select(a => a.Expression.ToString()).ToList());
    }

    static InvocationExpressionSyntax BroadcastInvocationInTheBtcSendPath()
    {
        var method = MethodNamed("SendBtcInternalAsync");
        var matches = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => NameOf(i) == "BroadcastTransactionAsync")
            .ToList();

        Assert.True(matches.Count == 1,
            $"SendBtcInternalAsync broadcasts {matches.Count} times; this pin reads exactly one");
        return matches[0];
    }

    static CatchClauseSyntax BroadcastFailureHandler()
    {
        var tryStatement = BroadcastInvocationInTheBtcSendPath()
            .Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
        Assert.True(tryStatement != null,
            "the BTC broadcast call is not inside a try, so there is no failure arm to read and a lost "
            + "reply reaches the operator as an undifferentiated failure");

        var catches = tryStatement!.Catches.ToList();

        Assert.True(catches.Count == 1,
            $"the broadcast try has {catches.Count} catch clauses; this pin reads exactly one");
        return catches[0];
    }

    static MethodDeclarationSyntax MethodNamed(string name)
    {
        var matches = PluginCompilation.Shared.Tree(WalletServiceFile).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name)
            .ToList();

        Assert.True(matches.Count == 1,
            $"expected exactly one {name} in {WalletServiceFile}, found {matches.Count}");
        return matches[0];
    }

    static string NameOf(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        _ => string.Empty
    };
}
