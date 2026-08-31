using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbOwnOutputDescriptorCouplingTests
{
    const string TestMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string RgbLibVanillaAccount = "m/86'/1'/0'";
    const string RgbLibColoredAccount = "m/86'/827167'/0'";
    const string VestigialBip84Account = "m/84'/1'/0'";

    static readonly Network Net = Network.RegTest;

    static ExtKey Master() => new Mnemonic(TestMnemonic).DeriveExtKey();

    static PSBTOutput SingleOutputCarryingClaim(
        Script outputScript, string claimedPath, bool asTaprootClaim)
    {
        var master = Master();
        var fp = master.GetPublicKey().GetHDFingerPrint();
        var claimedPubKey = master.Derive(new KeyPath(claimedPath)).GetPublicKey();

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(uint256.One, 0), Script.Empty);
        tx.Outputs.Add(new TxOut(Money.Satoshis(90_000), outputScript));
        var psbt = PSBT.FromTransaction(tx, Net);

        var stripped = new KeyPath(claimedPath.Substring(2));
        if (asTaprootClaim)
            psbt.Outputs[0].HDTaprootKeyPaths.Add(
                claimedPubKey.GetTaprootFullPubKey(),
                new TaprootKeyPath(new RootedKeyPath(fp, stripped)));
        else
            psbt.Outputs[0].HDKeyPaths.Add(claimedPubKey, new RootedKeyPath(fp, stripped));

        return psbt.Outputs[0];
    }

    static Script ScriptAt(string path, ScriptPubKeyType type) =>
        Master().Derive(new KeyPath(path)).GetPublicKey().GetAddress(type, Net).ScriptPubKey;

    [Fact]
    public void P2wpkhClaimedOnTheRgbLibVanillaAccount_IsNotOwnOutput_BecauseRgbLibsTrDescriptorNeverCoversAWpkhScript()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{RgbLibVanillaAccount}/1/7";
        var script = ScriptAt(claimed, ScriptPubKeyType.Segwit);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: false);

        Assert.False(signer.IsOwnOutput(output, script, Net),
            "A P2WPKH script derived from the purpose-86' account is seed-owned but sits outside "
            + "rgb-lib's tr() descriptor, so rgb-lib can never rediscover or spend it. Treating it as "
            + "an own output exempts unbounded change from value accounting and strands it permanently.");
    }

    [Fact]
    public void P2wpkhClaimedOnTheRgbLibColoredAccount_IsNotOwnOutput_BecauseRgbLibsTrDescriptorNeverCoversAWpkhScript()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{RgbLibColoredAccount}/0/3";
        var script = ScriptAt(claimed, ScriptPubKeyType.Segwit);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: false);

        Assert.False(signer.IsOwnOutput(output, script, Net),
            "The colored account's descriptor is tr() as well; a P2WPKH script under it is outside "
            + "every descriptor rgb-lib builds and would strand both the sats and any RGB successor.");
    }

    [Fact]
    public void TaprootClaimedOnTheRgbLibVanillaAccountChangeChain_IsStillOwnOutput()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{RgbLibVanillaAccount}/1/7";
        var script = ScriptAt(claimed, ScriptPubKeyType.TaprootBIP86);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: true);

        Assert.True(signer.IsOwnOutput(output, script, Net),
            "This is the exact shape rgb-lib's create_utxos_begin and send_begin emit for BTC change "
            + "(taproot, purpose 86', keychain 1). Refusing it would make every Create-UTXOs and every "
            + "RGB send permanently unsignable — a fund-stranding false reject far worse than the bug.");
    }

    [Fact]
    public void TaprootClaimedOnTheRgbLibColoredAccount_IsStillOwnOutput()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{RgbLibColoredAccount}/0/3";
        var script = ScriptAt(claimed, ScriptPubKeyType.TaprootBIP86);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: true);

        Assert.True(signer.IsOwnOutput(output, script, Net),
            "Colorable UTXOs rgb-lib creates live here; refusing them would break RGB receive entirely.");
    }

    [Fact]
    public void TaprootClaimedOnTheVestigialBip84Account_IsNotOwnOutput_BecauseRgbLibIsNeverHandedThatXpub()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{VestigialBip84Account}/0/0";
        var script = ScriptAt(claimed, ScriptPubKeyType.TaprootBIP86);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: true);

        Assert.False(signer.IsOwnOutput(output, script, Net),
            "rgb-lib is handed only the two purpose-86' account xpubs, so it builds no descriptor over "
            + "the BIP84 account at all. A taproot script there is seed-owned and plugin-unspendable.");
    }

    [Fact]
    public void P2wpkhClaimedOnTheVestigialBip84Account_IsNotOwnOutput_BecauseRgbLibIsNeverHandedThatXpub()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var claimed = $"{VestigialBip84Account}/1/5";
        var script = ScriptAt(claimed, ScriptPubKeyType.Segwit);

        var output = SingleOutputCarryingClaim(script, claimed, asTaprootClaim: false);

        Assert.False(signer.IsOwnOutput(output, script, Net),
            "Coupling script type to BIP purpose alone would leave this open, and a hostile PSBT "
            + "producer would simply relabel its diverted change from 86' to 84' to keep the exemption.");
    }

    [Fact]
    public async Task P2wpkhClaimedOnTheRgbLibVanillaAccount_IsRefusedByTheSendAssetPolicy()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var master = Master();
        var fp = master.GetPublicKey().GetHDFingerPrint();

        var inputKey = master.Derive(new KeyPath($"{RgbLibVanillaAccount}/1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net);

        var divertedPath = $"{RgbLibVanillaAccount}/1/7";
        var divertedKey = master.Derive(new KeyPath(divertedPath));
        var divertedScript = divertedKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, Net).ScriptPubKey;

        var fundingTx = Transaction.Create(Net);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(new TxOut(Money.Satoshis(90_000), divertedScript));

        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Outputs[0].HDKeyPaths.Add(
            divertedKey.GetPublicKey(),
            new RootedKeyPath(fp, new KeyPath(divertedPath.Substring(2))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), Net, SendAssetShapedPolicy()));

        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task TaprootClaimedOnTheRgbLibVanillaAccount_IsAcceptedByTheSendAssetPolicy()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Net);
        var master = Master();
        var fp = master.GetPublicKey().GetHDFingerPrint();

        var inputKey = master.Derive(new KeyPath($"{RgbLibVanillaAccount}/1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net);

        var changePath = $"{RgbLibVanillaAccount}/1/7";
        var changeKey = master.Derive(new KeyPath(changePath));
        var changeScript = changeKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, Net).ScriptPubKey;

        var fundingTx = Transaction.Create(Net);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(Net);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(new TxOut(Money.Satoshis(90_000), changeScript));

        var psbt = PSBT.FromTransaction(tx, Net);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            changeKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath(changePath.Substring(2)))));

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), Net, SendAssetShapedPolicy());

        Assert.False(string.IsNullOrWhiteSpace(signed),
            "rgb-lib's real taproot BTC change must keep its accounting exemption under the very policy "
            + "the RGB send path uses, or the fix converts a theoretical stranding into a total send outage.");
    }

    static SigningPolicy SendAssetShapedPolicy() => new()
    {
        MaxUnknownOutputSats = 0,
        MaxFeeSats = 10_000,
        AllowedScripts = new HashSet<Script>(),
        MaxOutputCount = 10,
        RequireUnfinalizedWitnessProgramInputs = true
    };
}
