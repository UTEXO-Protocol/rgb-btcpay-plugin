using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbIntentVerifierTests
{
    static readonly Network Net = Network.RegTest;
    const string ContractId = "rgb:2WBcas9-yA7soVimc-C1SQ34ry8-adfawij2j-asd23f8sd-abcdef";
    const string RecipientSeal = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    class FakeChainClient : IBitcoinChainClient
    {
        public Func<string, string>? RawTx;
        public Func<Script, IReadOnlyList<Outpoint>>? Unspent;
        public bool UnspentRowsAreConfirmed = true;
        public int UnspentCallCount;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
            => Task.FromResult(RawTx?.Invoke(txid) ?? throw new InvalidOperationException("no raw tx"));
        public Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
            Script script, CancellationToken ct = default)
        {
            UnspentCallCount++;
            return Task.FromResult<IReadOnlyList<UnspentWithConfirmation>>(
                (Unspent?.Invoke(script) ?? Array.Empty<Outpoint>())
                    .Select(o => new UnspentWithConfirmation(o, UnspentRowsAreConfirmed))
                    .ToList());
        }
        public void Dispose() { }
    }

    class Ctx
    {
        public required string Mnemonic;
        public required MemoryWalletSigner Signer;
        public required PSBT Psbt;
        public required string UnsignedTxid;
        public required Script ChangeScript;
        public required KeyPath ChangePath;
        public required RgbDecodeInvoiceResult Decode;
        public required RgbValidateV2Result Validate;
        public required List<string> Staged;
        public long OperatorAmount = 100;
        public string OperatorAssetId = ContractId;
        public FakeChainClient Chain = new();

        public Task Run() => RgbIntentVerifier.VerifyAsync(
            Decode, Validate, Psbt, UnsignedTxid, Signer, Net, OperatorAmount, OperatorAssetId, Staged, Chain);
    }

    static Ctx Valid()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var signer = new MemoryWalletSigner(mnemonic, Net);
        var master = new Mnemonic(mnemonic).DeriveExtKey();
        var changePath = new KeyPath("m/86'/827167'/0'/0/0");
        var changePubkey = master.Derive(changePath).GetPublicKey();
        var changeScript = changePubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

        var opret = new Script(OpcodeType.OP_RETURN, Op.GetPushOp(Convert.FromHexString(RecipientSeal)));
        var tx = Net.CreateTransaction();
        tx.Inputs.Add(new OutPoint(uint256.Parse("1111111111111111111111111111111111111111111111111111111111111111"), 3));
        tx.Outputs.Add(new TxOut(Money.Zero, opret));
        tx.Outputs.Add(new TxOut(Money.Coins(1), changeScript));
        var psbt = tx.CreatePSBT(Net);
        AddTaprootProof(psbt.Outputs[1], master, changePath);
        var unsignedTxid = psbt.GetGlobalTransaction().GetHash().ToString();
        var prevout = $"{psbt.GetGlobalTransaction().Inputs[0].PrevOut.Hash}:{psbt.GetGlobalTransaction().Inputs[0].PrevOut.N}";

        return new Ctx
        {
            Mnemonic = mnemonic,
            Signer = signer,
            Psbt = psbt,
            UnsignedTxid = unsignedTxid,
            ChangeScript = changeScript,
            ChangePath = changePath,
            Decode = new RgbDecodeInvoiceResult
            {
                ContractId = ContractId,
                AmountKind = "amount",
                Amount = 100,
                RecipientSeal = RecipientSeal,
                RecipientChainNet = "bcrt",
                Expiry = null,
                Transports = ["rpc://proxy.example/0.2/json-rpc"]
            },
            Validate = new RgbValidateV2Result
            {
                ValidationVersion = 2,
                ContractId = ContractId,
                ChainNet = "bcrt",
                WitnessTxid = unsignedTxid,
                Prevouts = [prevout],
                InputsAccounted = true,
                CommitmentMatches = true,
                WitnessIdMatches = true,
                CommittedContractIds = [ContractId],
                VerifiedContractIds = [ContractId],
                MainTransitionId = "main-transition",
                VerifiedTransitionIds = ["main-transition"],
                Legs =
                [
                    new RgbLeg { AssignmentType = 4000, SealKind = "confidentialSeal", SealBytes = RecipientSeal, Amount = 100 },
                    new RgbLeg { AssignmentType = 4000, SealKind = "revealedWitnessVout", WitnessVout = 1, Amount = 900 }
                ]
            },
            Staged = ["rpc://proxy.example/0.2/json-rpc"]
        };
    }

    [Fact]
    public async Task ValidTransfer_Passes()
    {
        await Valid().Run();
    }

    [Fact]
    public async Task WrongAsset_Rejected()
    {
        var c = Valid();
        c.Validate.ContractId = "rgb:different-contract-id";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorApprovedDifferentAsset_Rejected()
    {
        var c = Valid();
        c.OperatorAssetId = "rgb:some-other-asset-the-operator-picked";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorApprovedEmptyAsset_Rejected()
    {
        var c = Valid();
        c.OperatorAssetId = "";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorAssetId_RgbPrefixTolerant_Passes()
    {
        var c = Valid();
        c.OperatorAssetId = ContractId.Substring(4);
        await c.Run();
    }

    [Fact]
    public async Task OperatorApprovedDifferentAmount_EmbeddedInvoice_Rejected()
    {
        var c = Valid();
        c.OperatorAmount = 50;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongWitnessTxid_Rejected()
    {
        var c = Valid();
        c.Validate.WitnessTxid = "0000000000000000000000000000000000000000000000000000000000000000";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongPrevout_Rejected()
    {
        var c = Valid();
        c.Validate.Prevouts = ["deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef0:0"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongRecipientSeal_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].SealBytes = "00" + RecipientSeal[2..];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongRecipientAmount_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].Amount = 99;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task RecipientAssignmentTypeNotAsset_Rejected()
    {
        var c = Valid();
        c.Validate.Legs[0].AssignmentType = 9999;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task NonOwnChangeLeg_Rejected()
    {
        var c = Valid();
        using var foreignKey = new Key();
        var foreign = foreignKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs[1].ScriptPubKey = foreign;
        c.Psbt = tx.CreatePSBT(Net);
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task MainWitnessChange_GenericWalletAccount_Rejected()
    {
        var c = Valid();
        SetMainWitnessChange(c, new KeyPath("m/86'/1'/0'/0/5"));
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task MainWitnessChange_RgbInternalBranch_Rejected()
    {
        var c = Valid();
        SetMainWitnessChange(c, new KeyPath("m/86'/827167'/0'/1/5"));
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task MainWitnessChange_RgbP2wpkh_Rejected()
    {
        var c = Valid();
        SetMainWitnessChange(c, new KeyPath("m/86'/827167'/0'/0/5"), taproot: false);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcealedNonRecipientLeg_Rejected()
    {
        var c = Valid();
        c.Validate.Legs.Add(new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "confidentialSeal",
            SealBytes = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            Amount = 5
        });
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task WrongConsignmentNetwork_Rejected()
    {
        var c = Valid();
        c.Validate.ChainNet = "bc";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnmappedConsignmentNetwork_FailClosed()
    {
        var c = Valid();
        c.Validate.ChainNet = "tb4";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task RecipientNetworkMismatch_Rejected()
    {
        var c = Valid();
        c.Decode.RecipientChainNet = "bc";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task NonPlainTaprootOutput_Rejected()
    {
        var c = Valid();
        using var foreignKey = new Key();
        var foreign = foreignKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs.Add(new TxOut(Money.Coins(1), foreign));
        c.Psbt = tx.CreatePSBT(Net);
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task EndpointMismatch_Rejected()
    {
        var c = Valid();
        c.Staged = ["rpc://attacker.example/0.2/json-rpc"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task EndpointSchemeTranslated_RpcToHttp_Passes()
    {
        var c = Valid();
        c.Decode.Transports = ["rpc://proxy.example/0.2/json-rpc"];
        c.Staged = ["http://proxy.example/0.2/json-rpc"];
        await c.Run();
    }

    [Fact]
    public async Task EndpointSchemeTranslated_RpcsToHttps_Passes()
    {
        var c = Valid();
        c.Decode.Transports = ["rpcs://proxy.iriswallet.com/0.2/json-rpc"];
        c.Staged = ["https://proxy.iriswallet.com/0.2/json-rpc"];
        await c.Run();
    }

    [Fact]
    public async Task EndpointSchemeTranslated_DifferentHost_Rejected()
    {
        var c = Valid();
        c.Decode.Transports = ["rpc://proxy.example/0.2/json-rpc"];
        c.Staged = ["http://attacker.example/0.2/json-rpc"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommitmentNotMatching_Rejected()
    {
        var c = Valid();
        c.Validate.CommitmentMatches = false;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommitmentWitnessMismatch_Rejected()
    {
        var c = Valid();
        c.Validate.WitnessIdMatches = false;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task CommittedContractSetWrong_Rejected()
    {
        var c = Valid();
        c.Validate.CommittedContractIds = [ContractId, "rgb:extra-contract"];
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ExpiredInvoice_Rejected()
    {
        var c = Valid();
        c.Decode.Expiry = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointChange_OwnedAndUnspent_Passes()
    {
        var c = Valid();
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(fundingTxid, 0) };
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            DerivationPath = c.ChangePath.ToString(),
            Amount = 900
        };
        await c.Run();
    }

    [Fact]
    public async Task TheGateReachesTheSameVerdictWhetherTheIndexerRowsAreConfirmedOrNot()
    {
        var confirmed = await RunConcreteChangeVerificationWithChainRowsConfirmed(true);
        var unconfirmed = await RunConcreteChangeVerificationWithChainRowsConfirmed(false);

        Assert.Equal("accepted", confirmed);
        Assert.True(confirmed == unconfirmed,
            "The pre-sign gate matches staged inputs by outpoint and must not change its verdict "
            + "when confirmation state changes; if this fails, widening IBitcoinChainClient altered "
            + $"the gate's contract, which this change is required to leave untouched. Confirmed rows gave '{confirmed}', unconfirmed rows gave '{unconfirmed}'.");
    }

    async Task<string> RunConcreteChangeVerificationWithChainRowsConfirmed(bool rowsAreConfirmed)
    {
        var c = Valid();
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(fundingTxid, 0) };
        c.Chain.UnspentRowsAreConfirmed = rowsAreConfirmed;
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            DerivationPath = c.ChangePath.ToString(),
            Amount = 900
        };

        string verdict;
        try
        {
            await c.Run();
            verdict = "accepted";
        }
        catch (RgbIntentVerificationException ex)
        {
            verdict = $"rejected: {ex.Message}";
        }

        Assert.True(c.Chain.UnspentCallCount > 0,
            "This fixture must actually reach the gate's unspent lookup, or it proves nothing about "
            + "whether confirmation state can influence the gate's verdict.");
        return verdict;
    }

    [Fact]
    public async Task MainConcreteChange_GenericWalletAccount_Rejected()
    {
        var c = Valid();
        SetMainConcreteChange(c, new KeyPath("m/86'/1'/0'/0/6"));
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task MainConcreteChange_RgbInternalBranch_Rejected()
    {
        var c = Valid();
        SetMainConcreteChange(c, new KeyPath("m/86'/827167'/0'/1/6"));
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task MainConcreteChange_RgbP2wpkh_Rejected()
    {
        var c = Valid();
        SetMainConcreteChange(c, new KeyPath("m/86'/827167'/0'/0/6"), taproot: false);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task DuplicateRecipientLeg_Rejected()
    {
        var c = Valid();
        c.Validate.Legs.Add(new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "confidentialSeal",
            SealBytes = RecipientSeal,
            Amount = 100
        });
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnknownChangeSealKind_FailClosed()
    {
        var c = Valid();
        c.Validate.Legs[1].SealKind = "tapretFirst";
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointIsPsbtInput_Rejected()
    {
        var c = Valid();
        var inputPrevout = $"{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.Hash}:{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.N}";
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = inputPrevout,
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointNonOwnScript_Rejected()
    {
        var c = Valid();
        using var foreignKey = new Key();
        var foreign = foreignKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), foreign));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(fundingTxid, 0) };
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task UnexpectedAmountKind_FailClosed()
    {
        var c = Valid();
        c.Decode.AmountKind = "bogus";
        c.Decode.Amount = null;
        c.OperatorAmount = 100;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task OperatorTypedAmount_WhenInvoiceOmitsAmount_Passes()
    {
        var c = Valid();
        c.Decode.AmountKind = "absent";
        c.Decode.Amount = null;
        c.OperatorAmount = 100;
        await c.Run();
    }

    [Fact]
    public async Task OperatorTypedAmountMismatch_Rejected()
    {
        var c = Valid();
        c.Decode.AmountKind = "absent";
        c.Decode.Amount = null;
        c.OperatorAmount = 50;
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task ConcreteOutpointChange_Spent_Rejected()
    {
        var c = Valid();
        var fundingTx = Net.CreateTransaction();
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0));
        fundingTx.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var fundingTxid = fundingTx.GetHash().ToString();
        c.Chain.RawTx = _ => fundingTx.ToHex();
        c.Chain.Unspent = _ => Array.Empty<Outpoint>();
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    // Finding B / H2 regression: a concrete-outpoint change leg whose outpoint is itself one of
    // the tx inputs (co-spent) must be rejected — otherwise the "retained" change is being burned.
    [Fact]
    public async Task H2_ConcreteChangeOutpointIsCoSpentInput_Rejected()
    {
        var c = Valid();
        var inputPrevout = $"{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.Hash}:{c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut.N}";
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = inputPrevout,
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    // Finding B / H2 regression: the concrete-outpoint funding tx must actually hash to the
    // claimed txid — a substituted funding tx must not pass the retained-UTXO check.
    [Fact]
    public async Task H2_ConcreteChangeFundingTxidMismatch_Rejected()
    {
        var c = Valid();
        var realFunding = Net.CreateTransaction();
        realFunding.Inputs.Add(new OutPoint(uint256.One, 0));
        realFunding.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var otherFunding = Net.CreateTransaction();
        otherFunding.Inputs.Add(new OutPoint(uint256.Parse("2222222222222222222222222222222222222222222222222222222222222222"), 0));
        otherFunding.Outputs.Add(new TxOut(Money.Coins(1), c.ChangeScript));
        var claimedTxid = realFunding.GetHash().ToString();
        c.Chain.RawTx = _ => otherFunding.ToHex();
        c.Chain.Unspent = _ => new List<Outpoint> { new(claimedTxid, 0) };
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{claimedTxid}:0",
            Amount = 900
        };
        await Assert.ThrowsAsync<RgbIntentVerificationException>(c.Run);
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredWitnessOutput_Passes()
    {
        var (c, validate) = V2WithWitnessCarry(rgbColored: true);
        await RunV2(c, validate);
    }

    [Fact]
    public async Task V2_ForeignCarryToGenericOwnedWitnessOutput_Rejected()
    {
        var (c, validate) = V2WithWitnessCarry(rgbColored: false);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredInternalBranchWitnessOutput_Rejected()
    {
        var (c, validate) = V2WithWitnessCarry(rgbColored: true, branch: 1);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredP2wpkhWitnessOutput_Rejected()
    {
        var (c, validate) = V2WithWitnessCarry(rgbColored: true, taproot: false);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    [Fact]
    public async Task V2_ExtraVerifiedTransitionWithoutCarryProof_Rejected()
    {
        var (c, validate) = V2WithWitnessCarry(rgbColored: true);
        validate.CarryForwards.Clear();
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredConcreteOutput_Passes()
    {
        var (c, validate) = V2WithConcreteCarry();
        await RunV2(c, validate);
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredInternalBranchConcreteOutput_Rejected()
    {
        var (c, validate) = V2WithConcreteCarry(branch: 1);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    [Fact]
    public async Task V2_ForeignCarryToRgbColoredP2wpkhConcreteOutput_Rejected()
    {
        var (c, validate) = V2WithConcreteCarry(taproot: false);
        await Assert.ThrowsAsync<RgbIntentVerificationException>(() => RunV2(c, validate));
    }

    static (Ctx Context, RgbValidateV2Result Validate) V2WithWitnessCarry(
        bool rgbColored, uint branch = 0, bool taproot = true)
    {
        var c = Valid();
        var path = new KeyPath(rgbColored
            ? $"m/86'/827167'/0'/{branch}/7"
            : $"m/86'/1'/0'/{branch}/7");
        var master = new Mnemonic(c.Mnemonic).DeriveExtKey();
        var pubkey = master.Derive(path).GetPublicKey();
        var script = pubkey.GetAddress(
            taproot ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs.Add(new TxOut(Money.Coins(1), script));
        c.Psbt = tx.CreatePSBT(Net);
        AddTaprootProof(c.Psbt.Outputs[1], master, c.ChangePath);
        var fingerprint = master.GetPublicKey().GetHDFingerPrint();
        if (taproot)
            c.Psbt.Outputs[^1].HDTaprootKeyPaths.Add(pubkey.GetTaprootFullPubKey(),
                new TaprootKeyPath(new RootedKeyPath(fingerprint, path)));
        else
            c.Psbt.Outputs[^1].HDKeyPaths.Add(pubkey, new RootedKeyPath(fingerprint, path));
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;

        var validate = V2Base(c);
        AddCarry(validate, c.Validate.Prevouts[0], "rgb:foreign-contract", "carry-1",
            "revealedWitnessVout", (uint)(tx.Outputs.Count - 1), null, null);
        return (c, validate);
    }

    static (Ctx Context, RgbValidateV2Result Validate) V2WithConcreteCarry(
        uint branch = 0, bool taproot = true)
    {
        var c = Valid();
        var path = new KeyPath($"m/86'/827167'/0'/{branch}/9");
        var master = new Mnemonic(c.Mnemonic).DeriveExtKey();
        var pubkey = master.Derive(path).GetPublicKey();
        var script = pubkey.GetAddress(
            taproot ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new OutPoint(uint256.One, 0));
        funding.Outputs.Add(new TxOut(Money.Coins(1), script));
        var fundingTxid = funding.GetHash().ToString();
        c.Chain.RawTx = _ => funding.ToHex();
        c.Chain.Unspent = _ => [new Outpoint(fundingTxid, 0)];

        var validate = V2Base(c);
        AddCarry(validate, c.Validate.Prevouts[0], "rgb:foreign-contract", "carry-1",
            "revealedConcreteOutpoint", null, $"{fundingTxid}:0", path.ToString());
        return (c, validate);
    }

    static RgbValidateV2Result V2Base(Ctx c) => new()
    {
        ValidationVersion = 2,
        ContractId = c.Validate.ContractId,
        ChainNet = c.Validate.ChainNet,
        WitnessTxid = c.Validate.WitnessTxid,
        Prevouts = c.Validate.Prevouts,
        Legs = c.Validate.Legs,
        InputsAccounted = true,
        Inputs = c.Validate.Inputs,
        CommitmentMatches = true,
        WitnessIdMatches = true,
        CommittedContractIds = [ContractId],
        VerifiedContractIds = [ContractId],
        MainTransitionId = "main-transition",
        VerifiedTransitionIds = ["main-transition"]
    };

    static void AddCarry(RgbValidateV2Result validate, string inputOutpoint, string contractId,
        string transitionId, string successorKind, uint? witnessVout, string? successorOutpoint,
        string? derivationPath)
    {
        validate.CarryForwards.Add(new RgbCarryForwardProof
        {
            ContractId = contractId,
            Opout = $"opout-{transitionId}",
            TransitionId = transitionId,
            InputOutpoint = inputOutpoint,
            AssignmentType = 4000,
            StateKind = "amount",
            Amount = 42,
            SuccessorKind = successorKind,
            WitnessVout = witnessVout,
            SuccessorOutpoint = successorOutpoint,
            DerivationPath = derivationPath
        });
        validate.CommittedContractIds.Add(contractId);
        validate.VerifiedContractIds.Add(contractId);
        validate.VerifiedTransitionIds.Add(transitionId);
    }

    static Task RunV2(Ctx c, RgbValidateV2Result validate) => RgbIntentVerifier.VerifyAsync(
        c.Decode, validate, c.Psbt, c.UnsignedTxid, c.Signer, Net, c.OperatorAmount,
        c.OperatorAssetId, c.Staged, c.Chain);

    static void SetMainWitnessChange(Ctx c, KeyPath path, bool taproot = true)
    {
        var master = new Mnemonic(c.Mnemonic).DeriveExtKey();
        var pubkey = master.Derive(path).GetPublicKey();
        var tx = c.Psbt.GetGlobalTransaction();
        tx.Outputs[1].ScriptPubKey = pubkey.GetAddress(
            taproot ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        c.Psbt = tx.CreatePSBT(Net);
        if (taproot)
            AddTaprootProof(c.Psbt.Outputs[1], master, path);
        else
            c.Psbt.Outputs[1].HDKeyPaths.Add(pubkey,
                new RootedKeyPath(master.GetPublicKey().GetHDFingerPrint(), path));
        c.UnsignedTxid = c.Psbt.GetGlobalTransaction().GetHash().ToString();
        c.Validate.WitnessTxid = c.UnsignedTxid;
    }

    static void SetMainConcreteChange(Ctx c, KeyPath path, bool taproot = true)
    {
        var master = new Mnemonic(c.Mnemonic).DeriveExtKey();
        var script = master.Derive(path).GetPublicKey().GetAddress(
            taproot ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Net).ScriptPubKey;
        var funding = Net.CreateTransaction();
        funding.Inputs.Add(new OutPoint(uint256.One, 0));
        funding.Outputs.Add(new TxOut(Money.Coins(1), script));
        var fundingTxid = funding.GetHash().ToString();
        c.Chain.RawTx = _ => funding.ToHex();
        c.Chain.Unspent = _ => [new Outpoint(fundingTxid, 0)];
        c.Validate.Legs[1] = new RgbLeg
        {
            AssignmentType = 4000,
            SealKind = "revealedConcreteOutpoint",
            Outpoint = $"{fundingTxid}:0",
            DerivationPath = path.ToString(),
            Amount = 900
        };
    }

    static void AddTaprootProof(PSBTOutput output, ExtKey master, KeyPath path)
    {
        var pubkey = master.Derive(path).GetPublicKey();
        output.HDTaprootKeyPaths.Add(pubkey.GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(
                master.GetPublicKey().GetHDFingerPrint(), path)));
    }
}
