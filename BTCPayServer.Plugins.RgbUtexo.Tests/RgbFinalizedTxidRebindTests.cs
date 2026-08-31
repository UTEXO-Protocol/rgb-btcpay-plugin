using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbFinalizedTxidRebindTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    static readonly Network Net = Network.RegTest;
    const string RgbVanillaAccount = "m/86'/1'/0'";
    const string RgbColoredAccount = "m/86'/827167'/0'";

    static ExtKey Master() => new Mnemonic(TestMnemonic).DeriveExtKey();

    static PubKey Pub(string account, uint idx) =>
        Master().Derive(new KeyPath($"{account}/0/{idx}")).GetPublicKey();

    static Script Taproot(string account, uint idx = 0) =>
        Pub(account, idx).GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

    static Script ChangeScript => Taproot(RgbVanillaAccount, 9);

    static SigningPolicy AssetSendPolicy() => new()
    {
        MaxUnknownOutputSats = 0,
        MaxFeeSats = 50_000,
        AllowedScripts = new HashSet<Script> { ChangeScript },
        MaxOutputCount = 10,
        RequireUnfinalizedWitnessProgramInputs = true
    };

    static (Transaction Funding, PSBT Psbt) Build(params Script[] inputScripts)
    {
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        foreach (var script in inputScripts)
            funding.Outputs.Add(Money.Satoshis(100_000), script);

        var tx = Transaction.Create(Net);
        for (var i = 0; i < inputScripts.Length; i++)
            tx.Inputs.Add(new OutPoint(funding, (uint)i), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000 * inputScripts.Length), ChangeScript);

        var psbt = PSBT.FromTransaction(tx, Net);
        for (var i = 0; i < inputScripts.Length; i++)
            psbt.Inputs[i].WitnessUtxo = funding.Outputs[i];
        return (funding, psbt);
    }

    static async Task<string?> RefusalFor(PSBT psbt, SigningPolicy? policy = null)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        try
        {
            await signer.SignPsbtAsync(psbt.ToBase64(), Net, policy ?? AssetSendPolicy());
            return null;
        }
        catch (InvalidOperationException refusal) { return refusal.Message; }
    }

    [Fact]
    public void LegacyPrevout_ChangesTheTransactionIdAtFinalization()
    {
        var key = new Key();
        var legacy = key.PubKey.GetAddress(ScriptPubKeyType.Legacy, Net).ScriptPubKey;
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        funding.Outputs.Add(Money.Satoshis(100_000), legacy);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(funding, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000), ChangeScript);
        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].NonWitnessUtxo = funding;

        var unsignedTxid = psbt.GetGlobalTransaction().GetHash().ToString();
        psbt.SignWithKeys(key);
        Assert.True(psbt.TryFinalize(out var errors),
            errors == null ? "finalization failed" : string.Join("; ", errors));
        var finalizedTxid = psbt.ExtractTransaction().GetHash().ToString();

        Assert.NotEqual(unsignedTxid, finalizedTxid);
    }

    [Fact]
    public async Task AssetSendPolicy_RefusesANonWitnessPrevout()
    {
        var legacy = Pub(RgbVanillaAccount, 0).GetAddress(ScriptPubKeyType.Legacy, Net).ScriptPubKey;
        var funding = Transaction.Create(Net);
        funding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        funding.Outputs.Add(Money.Satoshis(100_000), legacy);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(funding, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000), ChangeScript);
        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].NonWitnessUtxo = funding;

        Assert.Contains("not a witness program", await RefusalFor(psbt));
    }

    [Fact]
    public async Task AssetSendPolicy_RefusesAProducerSuppliedFinalScriptSig()
    {
        var (_, psbt) = Build(Taproot(RgbColoredAccount));
        psbt.Inputs[0].FinalScriptSig = new Script(OpcodeType.OP_TRUE);

        Assert.Contains("final script data", await RefusalFor(psbt));
    }

    [Fact]
    public async Task AssetSendPolicy_RefusesAProducerSuppliedFinalScriptWitness()
    {
        var (_, psbt) = Build(Taproot(RgbColoredAccount));
        psbt.Inputs[0].FinalScriptWitness = new WitScript(Op.GetPushOp(new byte[64]));

        Assert.Contains("final script data", await RefusalFor(psbt));
    }

    [Fact]
    public async Task AssetSendPolicy_RefusesAConflictingUtxoPair()
    {
        var (funding, psbt) = Build(Taproot(RgbColoredAccount));
        psbt.Inputs[0].NonWitnessUtxo = funding;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(5_000), Taproot(RgbColoredAccount));

        Assert.Contains("conflicting utxo fields", await RefusalFor(psbt));
    }

    [Fact]
    public async Task AssetSendPolicy_StillSignsColoredKeychainInputs()
    {
        var (_, psbt) = Build(Taproot(RgbColoredAccount), Taproot(RgbColoredAccount, 1));

        Assert.Null(await RefusalFor(psbt));

        var vanillaOnly = AssetSendPolicy();
        vanillaOnly.RequireRgbVanillaKeychainInputs = true;
        var (_, sameInputs) = Build(Taproot(RgbColoredAccount), Taproot(RgbColoredAccount, 1));
        Assert.Contains("vanilla keychain", await RefusalFor(sameInputs, vanillaOnly));
    }

    [Fact]
    public void JournalledRecoveryPsbt_IsAcceptedOnlyWhileItsUnsignedIdSurvivesFinalization()
    {
        var key = new Key();
        var witnessFunding = Transaction.Create(Net);
        witnessFunding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        witnessFunding.Outputs.Add(Money.Satoshis(100_000),
            key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey);
        var witnessSpend = Transaction.Create(Net);
        witnessSpend.Inputs.Add(new OutPoint(witnessFunding, 0), Script.Empty);
        witnessSpend.Outputs.Add(new TxOut(Money.Satoshis(99_000), ChangeScript));
        var witnessPsbt = PSBT.FromTransaction(witnessSpend, Net);
        witnessPsbt.Inputs[0].WitnessUtxo = witnessFunding.Outputs[0];
        witnessPsbt.SignWithKeys(key);
        Assert.True(witnessPsbt.TryFinalize(out _));
        var witnessTxid = witnessPsbt.ExtractTransaction().GetHash().ToString();

        Assert.True(RGBWalletService.RecoveredPsbtKeepsItsUnsignedTransactionId(
                witnessPsbt.ToBase64(), witnessTxid, "regtest"),
            "an all-witness journal is the healthy case and must still replay; refusing it would "
            + "strand every legitimate crash recovery.");

        var legacyFunding = Transaction.Create(Net);
        legacyFunding.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        legacyFunding.Outputs.Add(Money.Satoshis(100_000),
            key.PubKey.GetAddress(ScriptPubKeyType.Legacy, Net).ScriptPubKey);
        var legacySpend = Transaction.Create(Net);
        legacySpend.Inputs.Add(new OutPoint(legacyFunding, 0), Script.Empty);
        legacySpend.Outputs.Add(new TxOut(Money.Satoshis(99_000), ChangeScript));
        var legacyPsbt = PSBT.FromTransaction(legacySpend, Net);
        legacyPsbt.Inputs[0].NonWitnessUtxo = legacyFunding;
        legacyPsbt.SignWithKeys(key);
        Assert.True(legacyPsbt.TryFinalize(out _));
        var legacyTxid = legacyPsbt.ExtractTransaction().GetHash().ToString();

        RGBWalletService.ValidateRecoveryPsbt(
            legacyPsbt.ToBase64(), legacyPsbt.ExtractTransaction().ToHex(), legacyTxid, "regtest");
        Assert.False(RGBWalletService.RecoveredPsbtKeepsItsUnsignedTransactionId(
                legacyPsbt.ToBase64(), legacyTxid, "regtest"),
            "ValidateRecoveryPsbt accepts this PSBT — it agrees with its own journal — so it cannot "
            + "be the check that catches a moved identity. This predicate is.");
    }

    [Fact]
    public void RecoveryReplayIsGatedOnTheJournalledIdentityBeforeSendEnd()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var reconcile = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "ReconcileWalletRecoveryAsync"));

        var replay = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RunNativeSendIsolatedAsync",
                ContainingType.Name: "RGBWalletService"
            });
        var gate = reconcile.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RecoveredPsbtKeepsItsUnsignedTransactionId",
                ContainingType.Name: "RGBWalletService"
            });

        Assert.True(gate.SpanStart < replay.SpanStart,
            "the identity check must run before send_end, not after: once the replay commits, the "
            + "native state is already keyed to an identity nothing can restore.");
        Assert.Contains(replay.Ancestors().OfType<IfStatementSyntax>(),
            i => i.Condition.DescendantNodesAndSelf().Contains(gate));
    }

    [Fact]
    public void FinalizedTransactionIsReboundToTheGateVerifiedTransactionId()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var send = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));

        var gateCall = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "RunIntentGateAsync",
                ContainingType.Name: "RGBWalletService"
            });
        var gateSymbol = (IMethodSymbol)model.GetSymbolInfo(gateCall).Symbol!;
        Assert.True(gateSymbol.ReturnType.ToString() == "System.Threading.Tasks.Task<string>",
            "RunIntentGateAsync must hand its caller the transaction id every gate check was bound "
            + $"to; it returns `{gateSymbol.ReturnType}`, which the caller cannot re-check against the "
            + "transaction finalization actually produces.");

        var gateLocal = gateCall.Ancestors().OfType<AssignmentExpressionSyntax>().Single().Left.ToString();
        var extract = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol { Name: "ExtractTransaction" });

        var comparison = send.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains(gateLocal, StringComparison.Ordinal));
        Assert.True(extract.SpanStart < comparison.SpanStart,
            "the re-check must read the transaction finalization produced, so it has to sit after "
            + "ExtractTransaction; a comparison before it can only restate the unsigned id.");

        var sendEndWrite = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "WriteSendEnd",
                ContainingType.Name: "RgbSendRecoveryJournal"
            });
        Assert.True(comparison.SpanStart < sendEndWrite.SpanStart,
            "the re-check must refuse BEFORE the send_end journal records the new transaction: past "
            + "that point recovery replays the very transaction the gate never verified.");

        var rejections = send.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FailStagedTransferForIntentRejectionAsync",
                ContainingType.Name: "RGBWalletService"
            }).ToList();
        Assert.True(rejections.Count == 2,
            $"both post-send_begin refusals — the gate's and the txid re-bind's — must fail the staged "
            + $"transfer through the same helper; {rejections.Count} call(s) do. Refusing without it "
            + "leaves the wallet quarantined by a journal nothing will discharge, which strands funds.");
        Assert.Contains(rejections, i => comparison.Span.Contains(i.Span));

        var helper = RoslynPins.BodyOf(RoslynPins.Method(
            tree, "RGBWalletService", "FailStagedTransferForIntentRejectionAsync"));
        Assert.Single(helper.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "FailTransfersAsync",
                ContainingType.Name: "IRgbLibService"
            });
    }

    [Fact]
    public void AssetSendSigningPolicyConstrainsItsInputScriptTypes()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var send = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));
        var policy = send.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
            .Single(o => o.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "MaxUnknownOutputSats") == true);

        Assert.Contains(policy.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "RequireUnfinalizedWitnessProgramInputs"
                 && a.Right.ToString() == "true");
        Assert.DoesNotContain(policy.Initializer.Expressions.OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "RequireRgbVanillaKeychainInputs");

        var signerTree = plugin.Tree("Services/MemoryWalletSigner.cs");
        var signBody = RoslynPins.BodyOf(RoslynPins.Method(
            signerTree, "MemoryWalletSigner", "SignPsbtAsync")).ToString();
        var guarded = signBody.IndexOf(
            "if (policy.RequireUnfinalizedWitnessProgramInputs || policy.RequireRgbVanillaKeychainInputs)",
            StringComparison.Ordinal);
        Assert.True(guarded >= 0,
            "the script-type guard must run for the asset-send flag AND for the vanilla-keychain "
            + "flag, so neither path can lose it: SignPsbtAsync no longer contains that condition.");
    }
}
