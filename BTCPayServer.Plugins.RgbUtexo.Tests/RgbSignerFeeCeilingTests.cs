using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSignerFeeCeilingTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // A PSBT producer that supplies an authentic NonWitnessUtxo alongside a WitnessUtxo understating
    // the same script used to slip an oversized fee past MaxFeeSats: the ceiling read WitnessUtxo
    // first while NBitcoin signs from NonWitnessUtxo, so the signature committed to the real, larger
    // input value and the difference went to miners. On the Create-UTXOs path MaxFeeSats is the only
    // bound on value leakage, so this drained the wallet's spendable balance.
    //
    // Amounts are chosen so the test discriminates: reading the understated 5_000 gives a 500-sat fee
    // that passes the 10_000 ceiling, while resolving through GetTxOut() gives 95_500 and must fail.
    [Fact]
    public async Task FeeCeiling_AuthenticNonWitnessUtxoWithUnderstatedWitnessUtxo_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        // The funding tx carries a dummy input because PSBT.ToBase64() refuses to serialise a
        // NonWitnessUtxo whose transaction has no inputs.
        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(4_500), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(5_000), addr.ScriptPubKey);

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds max allowed", ex.Message);
    }

    // Liveness companion: the honest shape (both fields agreeing, as every real producer emits since
    // both derive from the same prev tx) must still sign.
    [Fact]
    public async Task FeeCeiling_AgreeingUtxoFields_Signs()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), addr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(95_000), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].NonWitnessUtxo = fundingTx;
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            MaxFeeSats = 10_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);
        Assert.NotEmpty(signed);
    }

    static (PSBT Psbt, BitcoinAddress Address) MultiInputSweep(
        Network network, int inputs, long perInputSats, long feeSats)
    {
        var key = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        for (var i = 0; i < inputs; i++)
            fundingTx.Outputs.Add(Money.Satoshis(perInputSats), addr);

        var tx = Transaction.Create(network);
        for (var i = 0; i < inputs; i++)
            tx.Inputs.Add(new OutPoint(fundingTx, i), Script.Empty);
        var spendable = perInputSats * inputs - feeSats;
        tx.Outputs.Add(Money.Satoshis(1_000), addr);
        tx.Outputs.Add(Money.Satoshis(spendable - 1_000), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        for (var i = 0; i < inputs; i++)
        {
            psbt.Inputs[i].NonWitnessUtxo = fundingTx;
            psbt.Inputs[i].WitnessUtxo = fundingTx.Outputs[i];
        }
        return (psbt, addr);
    }

    static SigningPolicy ProductionCreateUtxosFeePolicy(int requestCount, BitcoinAddress own) =>
        new()
        {
            MaxFeeSats = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount),
            MaxFeeSatsPerAdditionalInput =
                RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount),
            AllowedScripts = new HashSet<Script> { own.ScriptPubKey }
        };

    [Fact]
    public void ProductionCreateUtxosPolicyPair_SumsToExactlyThreeTimesTheHonestFeeForTheShapePresented()
    {
        for (var requestCount = 1; requestCount <= RgbConfigBounds.UtxoCountMax; requestCount++)
        for (var vanillaInputs = 1; vanillaInputs <= 250; vanillaInputs++)
        {
            var honest = RGBWalletService.EstimateTaprootFee(
                vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
            var ceiling = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
                + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount)
                  * (vanillaInputs - 1);
            Assert.True(ceiling == honest * RGBWalletService.CreateUtxosFeeCeilingMultiplier,
                $"requestCount {requestCount}, vanilla inputs {vanillaInputs}: the two policy members sum "
                + $"to {ceiling} sat where three times the honest estimate is "
                + $"{honest * RGBWalletService.CreateUtxosFeeCeilingMultiplier} sat. SCOPE OF THIS TEST, "
                + "stated because an earlier wording claimed a behavioural consequence this test cannot "
                + "observe: it is ARITHMETIC over "
                + $"{nameof(RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput)} and "
                + $"{nameof(RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput)} and it NEVER "
                + "invokes the signer, so it says nothing about the ceiling the signer enforces. That "
                + "the enforced ceiling equals this sum — and therefore that an honest sweep signs — is "
                + "measured only by "
                + $"{nameof(EnforcedCeiling_SignsTheHonestSweepOfDustValueVanillaInputs)} and "
                + $"{nameof(EnforcedCeiling_RefusesOneSatAboveTheShapeBoundedCeilingOnDustValueInputs)}, "
                + "which feed the signer 546-sat inputs. MEASURED: this arithmetic held over the whole "
                + "requestCount 1..20 x inputs 1..250 grid while the enforced ceiling still refused the "
                + "honest fee from 86 dust-valued inputs upward, because an absolute 10000-sat floor and "
                + "a value-proportional rule were min-clamped over this sum inside the signer. Every "
                + "behavioural row of this file besides those two and "
                + $"{nameof(MaxFeeSatsOnlyPolicy_KeepsTheValueProportionalFloorAsItsEffectiveCeiling)} "
                + "funds each input with 100000 sat, which masks both clamps outside those three rows. "
                + "WHY the ceiling must be "
                + "affine in the INPUT count rather than in requestCount: rgb-lib 0.3.0-beta.30, commit "
                + "12da9a6, builds create_utxos_begin_impl's `inputs` from ALL of internal_unspents() "
                + "minus get_reserved_vanilla_outpoints and then calls "
                + "create_split_tx -> add_utxos(inputs).manually_selected_only(), so `num` sets the "
                + "RECIPIENT count and the input count is the wallet's whole non-reserved vanilla set.");
        }
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(20, 7)]
    public void TheRequestCountOnlyCeiling_RefusedTheHonestFeeFromSevenVanillaInputsUpward(
        int requestCount, int vanillaInputs)
    {
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var requestCountOnly = RGBWalletService.EstimateTaprootFee(
                requestCount, requestCount + 1, RGBWalletService.CreateUtxosFeeRate)
            * RGBWalletService.CreateUtxosFeeCeilingMultiplier;
        var shapeBounded = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
            + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount)
              * (vanillaInputs - 1);

        Assert.True(honest <= shapeBounded,
            $"requestCount {requestCount}, vanilla inputs {vanillaInputs}: the shape-bounded ceiling "
            + $"{shapeBounded} sat must admit the honest fee {honest} sat");
        Assert.Equal(
            requestCount == 1 && vanillaInputs >= 7,
            honest > requestCountOnly);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(4, 20)]
    public async Task ShapeBoundedCeiling_SignsTheHonestMultiInputSweep(int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, honest);

        var signed = await signer.SignPsbtAsync(
            psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr));
        Assert.NotEmpty(signed);
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(1, 40)]
    [InlineData(4, 20)]
    public async Task ShapeBoundedCeiling_StillRefusesOneSatAboveThreeTimesTheHonestFee(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var ceiling = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
            + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount) * (vanillaInputs - 1);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, ceiling + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(
                psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.Contains($"exceeds max allowed {ceiling} sat", ex.Message);
    }

    [Fact]
    public async Task ThePerInputTermIsLoadBearing_WithoutItSevenVanillaInputsCanNeverBeSweptAtUtxoCountOne()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            7, 2, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, 7, 100_000, honest);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest, new SigningPolicy
            {
                MaxFeeSats = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(1),
                AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
            }));
        Assert.Contains(
            $"PSBT fee ({honest} sat) exceeds max allowed "
            + $"{RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(1)} sat",
            ex.Message);
    }

    [Fact]
    public async Task MaxFeeSatsPerAdditionalInput_DefaultsToZeroSoEverySingleInputPolicyIsUnchanged()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, 1, 100_000, 924);

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest, new SigningPolicy
        {
            MaxFeeSats = 924,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        });
        Assert.NotEmpty(signed);

        var (tooDear, _) = MultiInputSweep(Network.RegTest, 1, 100_000, 925);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(tooDear.ToBase64(), Network.RegTest, new SigningPolicy
            {
                MaxFeeSats = 924,
                AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
            }));
        Assert.Contains("exceeds max allowed 924 sat", ex.Message);
    }

    const long DustValuedInputSats = 546;

    static long ShapeBoundedCeiling(int requestCount, int vanillaInputs) =>
        RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(requestCount)
        + RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(requestCount) * (vanillaInputs - 1);

    [Theory]
    [InlineData(1, 85)]
    [InlineData(1, 86)]
    [InlineData(1, 250)]
    [InlineData(20, 71)]
    [InlineData(20, 72)]
    public async Task EnforcedCeiling_SignsTheHonestSweepOfDustValueVanillaInputs(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, requestCount + 1, RGBWalletService.CreateUtxosFeeRate);
        var (psbt, addr) = MultiInputSweep(
            Network.RegTest, vanillaInputs, DustValuedInputSats, honest);

        var failure = await Record.ExceptionAsync(() => signer.SignPsbtAsync(
            psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.True(failure is null,
            $"requestCount {requestCount}, {vanillaInputs} vanilla inputs of {DustValuedInputSats} sat: "
            + $"the signer refused the honest fee {honest} sat with '{failure?.Message}' although the "
            + $"shape-bounded ceiling this "
            + $"policy declares is {ShapeBoundedCeiling(requestCount, vanillaInputs)} sat. The inputs are "
            + "DUST-VALUED on purpose: with 100000-sat inputs the value-proportional rule alone admits "
            + "hundreds of thousands of sat, so it and the absolute floor are both masked and this row "
            + "cannot see either. A refusal here is a PERMANENT false-REJECT — every automatic sweep and "
            + "the manual button both throw, the colorable pool is never refilled and RGB payments stop, "
            + "with no configuration able to lift it because neither the floor nor the percentage is a "
            + "policy member the create-UTXOs path sets. Whatever the signer composes MaxFeeSats and "
            + "MaxFeeSatsPerAdditionalInput with must not be able to LOWER their sum.");
    }

    [Theory]
    [InlineData(1, 86)]
    [InlineData(1, 250)]
    [InlineData(20, 72)]
    public async Task EnforcedCeiling_RefusesOneSatAboveTheShapeBoundedCeilingOnDustValueInputs(
        int requestCount, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var ceiling = ShapeBoundedCeiling(requestCount, vanillaInputs);
        var (psbt, addr) = MultiInputSweep(
            Network.RegTest, vanillaInputs, DustValuedInputSats, ceiling + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(
                psbt.ToBase64(), Network.RegTest, ProductionCreateUtxosFeePolicy(requestCount, addr)));
        Assert.True(
            ex.Message.Contains($"PSBT fee ({ceiling + 1} sat) exceeds max allowed {ceiling} sat",
                StringComparison.Ordinal),
            $"requestCount {requestCount}, {vanillaInputs} vanilla inputs of {DustValuedInputSats} sat: "
            + $"the refusal must name {ceiling} sat as the enforced ceiling, it said '{ex.Message}'. This "
            + "asserts the NUMBER, not merely that a refusal happened: a value-proportional or absolute "
            + "clamp under the shape-bounded sum still refuses this over-fee, so a message-blind row "
            + "passes while the honest fee of the same shape is refused too.");
    }

    [Fact]
    public async Task MaxFeeSatsOnlyPolicy_KeepsTheValueProportionalFloorAsItsEffectiveCeiling()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var floor = MemoryWalletSigner.ValueProportionalFeeCeilingFloorSats;
        var (atFloor, addr) = MultiInputSweep(Network.RegTest, 86, DustValuedInputSats, floor);

        SigningPolicy Policy() => new()
        {
            MaxFeeSats = 100_000,
            AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
        };

        var atFloorFailure = await Record.ExceptionAsync(
            () => signer.SignPsbtAsync(atFloor.ToBase64(), Network.RegTest, Policy()));
        Assert.True(atFloorFailure is null,
            $"a policy that declares MaxFeeSats alone must still admit a fee of exactly {floor} sat; it "
            + $"refused with '{atFloorFailure?.Message}'. Deleting the floor to fix the create-UTXOs "
            + "ceiling breaks every MaxFeeSats-only path on a low-value input set.");

        var (justAbove, _) = MultiInputSweep(Network.RegTest, 86, DustValuedInputSats, floor + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(justAbove.ToBase64(), Network.RegTest, Policy()));
        Assert.True(
            ex.Message.Contains($"exceeds max allowed {floor} sat", StringComparison.Ordinal),
            $"a policy that declares MaxFeeSats alone and leaves MaxFeeSatsPerAdditionalInput at its "
            + $"default must keep the value-proportional rule and its {floor}-sat floor as its effective "
            + $"ceiling; here 10 percent of the output value is far below {floor}, so the floor governs "
            + $"and {floor + 1} sat must be refused naming {floor}. It said '{ex.Message}'. The "
            + "create-UTXOs fix must NOT be implemented by deleting the floor or the percentage: SendBtc "
            + "declares MaxFeeSats without a per-input term and relies on this composition unchanged. "
            + "SendAsset used to as well; it now declares MaxFeeSatsPerAdditionalInput too, so it takes "
            + "the linear branch and no longer reaches this floor — see "
            + $"{nameof(SendAssetShapeBoundedCeiling_SignsTheHonestMultiInputSweep)}.");
    }

    [Fact]
    public async Task TheFlatSendAssetCeiling_WithoutAPerInputTermRefusesTheHonestFeeFromFifteenVanillaInputsUpward()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        const float feeRate = 2f;
        var flatCeiling = RGBWalletService.EstimateTaprootFee(3, 3, feeRate) * 3;
        var honestAtFifteen = RGBWalletService.EstimateTaprootFee(15, 2, feeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, 15, 100_000, honestAtFifteen);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest, new SigningPolicy
            {
                MaxFeeSats = flatCeiling,
                AllowedScripts = new HashSet<Script> { addr.ScriptPubKey }
            }));
        Assert.Contains($"PSBT fee ({honestAtFifteen} sat) exceeds max allowed {flatCeiling} sat", ex.Message);
        Assert.True(honestAtFifteen > flatCeiling,
            $"this test documents the pre-fix defect, so its own premise must hold at this feeRate: the "
            + $"honest fee for 15 vanilla inputs at the real 2-output shape ({honestAtFifteen} sat) must "
            + $"exceed the flat, non-scaling ceiling ({flatCeiling} sat) that SendAsset used before it "
            + "declared MaxFeeSatsPerAdditionalInput alongside MaxFeeSats. This demonstrates the SHAPE "
            + "defect at feeRate=2f only: a flat ceiling is bounded in input count while the honest fee "
            + "is not, so some input count is always eventually refused at every feeRate — but which "
            + "exact count is NOT '15 for every feeRate', because rgb-lib builds the real transaction at "
            + "(int)Math.Round(feeRate), not at the unrounded float this synthetic ceiling used on both "
            + "sides here; a feeRate whose rounding lowers the rate rgb-lib actually builds at (1.49 "
            + "rounds to 1, not 2) can push the honest 15-input fee back under a ceiling computed from "
            + "the float, which is exactly why the fix ties the ceiling to the SAME rounded rate rgb-lib "
            + "receives rather than to the float it was called with.");
    }

    static SigningPolicy ProductionSendAssetFeePolicy(float feeRate, BitcoinAddress own) =>
        new()
        {
            MaxFeeSats = RGBWalletService.SendAssetMaxFeeSatsAtOneInput(
                RGBWalletService.SendAssetRoundedFeeRate(feeRate)),
            MaxFeeSatsPerAdditionalInput = RGBWalletService.SendAssetMaxFeeSatsPerAdditionalInput(
                RGBWalletService.SendAssetRoundedFeeRate(feeRate)),
            AllowedScripts = new HashSet<Script> { own.ScriptPubKey }
        };

    static long SendAssetShapeBoundedCeiling(float feeRate, int vanillaInputs)
    {
        var rounded = RGBWalletService.SendAssetRoundedFeeRate(feeRate);
        return RGBWalletService.SendAssetMaxFeeSatsAtOneInput(rounded)
            + RGBWalletService.SendAssetMaxFeeSatsPerAdditionalInput(rounded) * (vanillaInputs - 1);
    }

    [Theory]
    [InlineData(2f, 1)]
    [InlineData(2f, 14)]
    [InlineData(2f, 40)]
    [InlineData(2.3f, 20)]
    [InlineData(1000f, 1)]
    [InlineData(1000f, 3)]
    [InlineData(1.5f, 1)]
    [InlineData(1.5f, 14)]
    public async Task SendAssetShapeBoundedCeiling_SignsTheHonestMultiInputSweep(
        float feeRate, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var rgbLibFeeRate = RGBWalletService.SendAssetRoundedFeeRate(feeRate);
        var honest = RGBWalletService.EstimateTaprootFee(
            vanillaInputs, RGBWalletService.SendAssetFeeShapeOutputCount, rgbLibFeeRate);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, honest);

        var failure = await Record.ExceptionAsync(() => signer.SignPsbtAsync(
            psbt.ToBase64(), Network.RegTest, ProductionSendAssetFeePolicy(feeRate, addr)));
        Assert.True(failure is null,
            $"feeRate {feeRate} (rgb-lib receives {rgbLibFeeRate}), {vanillaInputs} vanilla inputs: "
            + $"SendAsset's production policy refused the honest fee {honest} sat — computed at the "
            + $"SAME rounded rate rgb-lib actually builds at, not at the float — with "
            + $"'{failure?.Message}', although the shape-bounded ceiling it declares is "
            + $"{SendAssetShapeBoundedCeiling(feeRate, vanillaInputs)} sat. Before "
            + $"{nameof(RGBWalletService.SendAssetMaxFeeSatsPerAdditionalInput)} existed, SendAsset's "
            + "ceiling was the flat constant "
            + $"{nameof(RGBWalletService.EstimateTaprootFee)}(3, 3, feeRate) * 3 with no per-input term, "
            + "so every send needing enough vanilla inputs was refused permanently — 15 or more at every "
            + "INTEGER fee rate from 1 through 1000 sat/vB, though at a fractional rate the exact "
            + "threshold shifts, because rgb-lib builds at (int)Math.Round(feeRate) while this flat "
            + "ceiling was computed from the unrounded float on both sides — see "
            + $"{nameof(TheFlatSendAssetCeiling_WithoutAPerInputTermRefusesTheHonestFeeFromFifteenVanillaInputsUpward)}.");
    }

    [Theory]
    [InlineData(2f, 1)]
    [InlineData(2f, 14)]
    [InlineData(2f, 40)]
    [InlineData(1000f, 1)]
    [InlineData(1.5f, 1)]
    [InlineData(1.5f, 14)]
    public async Task SendAssetShapeBoundedCeiling_RefusesOneSatAboveTheDeclaredCeiling(
        float feeRate, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var ceiling = SendAssetShapeBoundedCeiling(feeRate, vanillaInputs);
        var (psbt, addr) = MultiInputSweep(Network.RegTest, vanillaInputs, 100_000, ceiling + 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(
                psbt.ToBase64(), Network.RegTest, ProductionSendAssetFeePolicy(feeRate, addr)));
        Assert.True(
            ex.Message.Contains($"PSBT fee ({ceiling + 1} sat) exceeds max allowed {ceiling} sat",
                StringComparison.Ordinal),
            $"feeRate {feeRate}, {vanillaInputs} vanilla inputs: the refusal must name {ceiling} sat as "
            + $"the enforced ceiling — one sat above it must be refused and the message must say so — "
            + $"it said '{ex.Message}'. A refusal that does not name the number lets a value-proportional "
            + "or absolute clamp underneath the shape-bounded sum masquerade as this ceiling.");
    }

    [Fact]
    public void SendAssetFeeShapeOutputCount_IsExactlyTwoNotThree()
    {
        Assert.True(RGBWalletService.SendAssetFeeShapeOutputCount == 2,
            $"SendAssetFeeShapeOutputCount is {RGBWalletService.SendAssetFeeShapeOutputCount}, not the "
            + "true output count of an asset-send PSBT. rgb-lib's prepare_psbt adds one OP_RETURN, one "
            + "output per witness recipient, and one drain-to change; SendAsset always sends a single "
            + "blinded recipient with witness_data null, so it asks for zero witness-recipient outputs — "
            + "OP_RETURN plus change is exactly 2, and rgb-lib can never build a wider asset-send PSBT "
            + "than that for this plugin. A value above 2 here inflates the fee ceiling's base term by "
            + "3 × 43 × feeRate sat of pure admitted excess PER EXCESS OUTPUT at every input count, for "
            + "a shape this plugin can never produce.");
    }

    [Fact]
    public void SendAssetMaxFeeSatsPerAdditionalInput_IsExactlyTwiceTheHonestMarginalNotThree()
    {
        Assert.True(RGBWalletService.SendAssetFeeMarginalMultiplier == 2,
            $"SendAssetFeeMarginalMultiplier is {RGBWalletService.SendAssetFeeMarginalMultiplier}, not "
            + "the owner-approved 2. This is the specific number, not merely internal self-consistency "
            + "with whatever the constant happens to hold: a 3x marginal was the first draft of this same "
            + "fix, and at feeRate 2 it would admit 230 sat of burnable excess per padded input against an "
            + "honest marginal of 115 — 2 is the value the owner approved, admitting 115.");
        for (var feeRate = 1; feeRate <= 1000; feeRate++)
        {
            var honestMarginal = RGBWalletService.EstimateTaprootFee(
                    2, RGBWalletService.SendAssetFeeShapeOutputCount, feeRate)
                - RGBWalletService.EstimateTaprootFee(
                    1, RGBWalletService.SendAssetFeeShapeOutputCount, feeRate);
            var marginal = RGBWalletService.SendAssetMaxFeeSatsPerAdditionalInput(feeRate);
            Assert.True(marginal == honestMarginal * RGBWalletService.SendAssetFeeMarginalMultiplier,
                $"feeRate {feeRate}: SendAssetMaxFeeSatsPerAdditionalInput is {marginal} sat, expected "
                + $"{honestMarginal * RGBWalletService.SendAssetFeeMarginalMultiplier} sat "
                + $"({RGBWalletService.SendAssetFeeMarginalMultiplier}x the honest per-input marginal fee "
                + $"of {honestMarginal} sat). The owner approved exactly "
                + $"{RGBWalletService.SendAssetFeeMarginalMultiplier}x here, halved from the 3x first "
                + "drafted in this same change — before this fix the path carried no per-input term at "
                + "all, only a flat ceiling. At feeRate 2 a 3x marginal admits 230 sat of burnable excess "
                + "per padded wallet-owned input against the honest 115, with no way to recover it, though "
                + "it cannot reach an attacker's address because MaxUnknownOutputSats stays 0 and "
                + "AllowedScripts still binds.");
        }
    }

    [Theory]
    [InlineData(2f, 100)]
    [InlineData(1000f, 50)]
    public async Task SendAssetMarginalMultiplier_NoLongerAdmitsTheOldThreeTimesMarginalOverpayment(
        float feeRate, int vanillaInputs)
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var rounded = RGBWalletService.SendAssetRoundedFeeRate(feeRate);
        var honestMarginal = RGBWalletService.EstimateTaprootFee(
                2, RGBWalletService.SendAssetFeeShapeOutputCount, rounded)
            - RGBWalletService.EstimateTaprootFee(
                1, RGBWalletService.SendAssetFeeShapeOutputCount, rounded);
        var oldThreeTimesMarginalCeiling = RGBWalletService.SendAssetMaxFeeSatsAtOneInput(rounded)
            + honestMarginal * 3 * (vanillaInputs - 1);
        var newCeiling = SendAssetShapeBoundedCeiling(feeRate, vanillaInputs);
        Assert.True(oldThreeTimesMarginalCeiling > newCeiling,
            $"this test's own premise must hold: the fee a 3x-per-input marginal would have admitted "
            + $"({oldThreeTimesMarginalCeiling} sat) must exceed the 2x-marginal ceiling actually "
            + $"enforced ({newCeiling} sat) at {vanillaInputs} vanilla inputs, feeRate {feeRate} — "
            + "otherwise this test cannot tell the two multipliers apart");
        var (psbt, addr) = MultiInputSweep(
            Network.RegTest, vanillaInputs, 100_000, oldThreeTimesMarginalCeiling);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Network.RegTest,
                ProductionSendAssetFeePolicy(feeRate, addr)));
        Assert.True(
            ex.Message.Contains($"exceeds max allowed {newCeiling} sat", StringComparison.Ordinal),
            $"feeRate {feeRate}, {vanillaInputs} vanilla inputs: a fee of {oldThreeTimesMarginalCeiling} "
            + $"sat — exactly what a 3x-per-input marginal ceiling would have admitted — must now be "
            + $"refused naming the tighter {newCeiling}-sat ceiling. It said '{ex.Message}'. Before the "
            + "marginal multiplier was halved, this same fee signed successfully: with 3x per input, "
            + $"{vanillaInputs} padded wallet-owned vanilla inputs could burn exactly "
            + $"{honestMarginal * (vanillaInputs - 1)} sat more to miners than the ceiling enforced "
            + "today permits, unrecoverably, on every send needing that many inputs.");
    }
}
