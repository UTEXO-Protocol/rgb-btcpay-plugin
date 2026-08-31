using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class MemoryWalletSignerTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void Create_MasterFingerprint_IsNonEmpty()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        Assert.False(string.IsNullOrEmpty(signer.MasterFingerprint));
    }

    [Fact]
    public async Task Disposed_SignPsbt_ThrowsObjectDisposedException()
    {
        var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        signer.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => signer.SignPsbtAsync("cHNidP8BAFUBAAAAAc6HJbFqFfMhfRYDz5GFJHZ/1LQuvCWXVWxcS4BabjsAAAAAAD9////AaRCDwAAAAAAFgAUxTjw3dbuLFBasxijMOddUTnCmtwAAAAATwEEiYuOdAAAAACHJVEfPd3nah9KI/vE/Q7gS27oC3xeIxIDlXXFrB4VyAJqRj0njMvuLj3TfI0AXwfYHXU5WQTQ8Qjk/dY4oMNM9xSh7Y0AAIAAIABAACAAAAAAA==", Network.RegTest, new SigningPolicy()));
    }

    [Fact]
    public async Task Policy_OutputExceedsMaxUnknownSats_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);

        var network = Network.RegTest;
        var key = new Key();
        var unknownAddr = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        var signerVanillaKey = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var signerAddr = signerVanillaKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), signerAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(99_000), unknownAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy { MaxUnknownOutputSats = 546 };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task Policy_FeeExceedsMaxPercent_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;

        var signerKey = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var signerAddr = signerKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);
        var destKey = new Key();
        var destAddr = destKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), signerAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(10_000), destAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            ExpectedDestination = destAddr.ToString(),
            MaxFeePercent = 10.0,
            MaxUnknownOutputSats = 100_000
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("fee", ex.Message.ToLower());
    }

    [Fact]
    public async Task Policy_ExpectedAmountMismatch_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;

        var signerKey = new Mnemonic(TestMnemonic).DeriveExtKey().Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var signerAddr = signerKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);
        var destKey = new Key();
        var destAddr = destKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), signerAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), destAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            ExpectedDestination = destAddr.ToString(),
            ExpectedAmountSats = 50_000,
            MaxUnknownOutputSats = 100_000
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("does not match expected", ex.Message);
    }

    [Fact]
    public async Task HappyPath_RgbLibPsbt_SignsAndFinalizes()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/3"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var changeKey = coloredAccountKey.Derive(new KeyPath("0/5"));
        var changeAddr = changeKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(10_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(1000), changeAddr);
        tx.Outputs.Add(Money.Satoshis(1000), changeAddr);
        tx.Outputs.Add(new TxOut(Money.Zero, Script.Empty));
        tx.Outputs[2].ScriptPubKey = TxNullDataTemplate.Instance.GenerateScriptPubKey(new byte[32]);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/3"))));

        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            changeKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/0/5"))));
        psbt.Outputs[1].HDTaprootKeyPaths.Add(
            changeKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/0/5"))));

        var policy = new SigningPolicy { AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey } };
        var signedBase64 = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);

        var signed = PSBT.Parse(signedBase64, network);
        Assert.True(signed.Inputs[0].TaprootKeySignature != null || signed.Inputs[0].FinalScriptWitness != null,
            "Input should be signed or finalized");
    }

    [Fact]
    public async Task HappyPath_SendBtcPsbt_PopulatesKeyPathsAndSigns()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();

        var vanillaAccountKey = masterKey.Derive(new KeyPath("m/84'/1'/0'"));
        var inputKey = vanillaAccountKey.Derive(new KeyPath("0/2"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var changeKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var changeAddr = changeKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var destAddr = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(50_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(30_000), destAddr);
        tx.Outputs.Add(Money.Satoshis(19_500), changeAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];

        var policy = new SigningPolicy
        {
            ExpectedDestination = destAddr.ToString(),
            ExpectedAmountSats = 30_000,
            AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey }
        };

        var signedBase64 = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);

        var signed = PSBT.Parse(signedBase64, network);
        Assert.True(signed.Inputs[0].PartialSigs.Count > 0 || signed.Inputs[0].FinalScriptWitness != null,
            "Input should be signed via PopulateInputKeyPaths");
    }

    [Fact]
    public async Task SpoofedBip32Metadata_IsOwnOutput_Rejects()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var attackerKey = new Key();
        var attackerAddr = attackerKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), attackerAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));

        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            attackerKey.PubKey.GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/0/99"))));

        var policy = new SigningPolicy();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task AllowedScripts_AttackerAddress_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;

        var attackerAddr = new Key().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(1000), attackerAddr);
        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(2000), attackerAddr);

        var policy = new SigningPolicy
        {
            AllowedScripts = new HashSet<Script> { attackerAddr.ScriptPubKey }
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("not derivable from wallet keys", ex.Message);
    }

    [Fact]
    public async Task DisallowedAccountPath_TreatedAsUnknown()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var wrongAccountKey = masterKey.Derive(new KeyPath("m/99'/0'/0'/0/0"));
        var wrongAddr = wrongAccountKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), wrongAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            wrongAccountKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("99'/0'/0'/0/0"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, new SigningPolicy()));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task InvalidChainIndex_TreatedAsUnknown()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var badChainKey = masterKey.Derive(new KeyPath("m/86'/1'/0'/2/0"));
        var badChainAddr = badChainKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), badChainAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            badChainKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/2/0"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, new SigningPolicy()));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public void Regtest_DerivesDifferentXpubs_ThanMainnet()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);

        Assert.NotEqual(regtest.XpubRgbLibVanilla, mainnet.XpubRgbLibVanilla);
        Assert.StartsWith("tpub", regtest.XpubRgbLibVanilla);
        Assert.StartsWith("xpub", mainnet.XpubRgbLibVanilla);
    }

    [Fact]
    public void SameMnemonic_SameNetwork_SameFingerprint()
    {
        using var a = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var b = new MemoryWalletSigner(TestMnemonic, Network.Main);

        Assert.Equal(a.MasterFingerprint, b.MasterFingerprint);
    }

    [Fact]
    public async Task Mainnet_PopulatesKeyPathsAndSigns()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        var network = Network.Main;
        var fingerprint = new HDFingerprint(Convert.FromHexString(signer.MasterFingerprint));

        var mnemonicObj = new Mnemonic(TestMnemonic);
        var masterKey = mnemonicObj.DeriveExtKey();
        var vanillaKey = masterKey.Derive(new KeyPath("m/84'/0'/0'/0/0"));
        var addr = vanillaKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(Money.Satoshis(900), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(1000), addr.ScriptPubKey);

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network,
            new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });

        var result = PSBT.Parse(signed, network);
        Assert.True(result.Inputs[0].PartialSigs.Count > 0 || result.Inputs[0].FinalScriptWitness != null);
    }

    [Fact]
    public async Task Mainnet_TaprootRgbPath_Signs()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        var network = Network.Main;
        var fingerprint = new HDFingerprint(Convert.FromHexString(signer.MasterFingerprint));

        var mnemonicObj = new Mnemonic(TestMnemonic);
        var masterKey = mnemonicObj.DeriveExtKey();
        var rgbKey = masterKey.Derive(new KeyPath("m/86'/827166'/0'/0/0"));
        var taprootAddr = rgbKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(Money.Satoshis(900), taprootAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(1000), taprootAddr.ScriptPubKey);

        var fullPath = new KeyPath("86'/827166'/0'/0/0");
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            rgbKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fingerprint, fullPath)));

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network,
            new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });

        var result = PSBT.Parse(signed, network);
        Assert.True(result.Inputs[0].TaprootKeySignature != null || result.Inputs[0].FinalScriptWitness != null);
    }

    [Fact]
    public async Task IndexAboveMaxReasonable_TreatedAsUnknown()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var overLimitKey = masterKey.Derive(new KeyPath("m/86'/1'/0'/0/100001"));
        var overLimitAddr = overLimitKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), overLimitAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            overLimitKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/0/100001"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, new SigningPolicy()));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task OpReturn_NonzeroValue_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var changeKey = coloredAccountKey.Derive(new KeyPath("0/0"));
        var changeAddr = changeKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(90_000), changeAddr);
        tx.Outputs.Add(new TxOut(Money.Satoshis(5000),
            TxNullDataTemplate.Instance.GenerateScriptPubKey(new byte[32])));

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));

        var policy = new SigningPolicy { AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey } };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("unspendable", ex.Message);
        Assert.Contains("burn", ex.Message);
    }

    [Fact]
    public async Task OpReturn_ZeroValue_Allowed()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var coloredAccountKey = masterKey.Derive(new KeyPath("m/86'/1'/0'"));
        var inputKey = coloredAccountKey.Derive(new KeyPath("1/0"));
        var inputAddr = inputKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var changeKey = coloredAccountKey.Derive(new KeyPath("0/5"));
        var changeAddr = changeKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(10_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(1000), changeAddr);
        tx.Outputs.Add(new TxOut(Money.Zero,
            TxNullDataTemplate.Instance.GenerateScriptPubKey(new byte[32])));

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            inputKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/0"))));
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            changeKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/0/5"))));

        var policy = new SigningPolicy { AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey } };
        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);
        Assert.NotNull(signed);
    }

    [Fact]
    public async Task StrictMode_OwnDerivedOutsideAllowlist_Throws()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var vanillaKey = masterKey.Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var inputAddr = vanillaKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var allowedChangeKey = masterKey.Derive(new KeyPath("m/84'/1'/0'/1/0"));
        var allowedChangeAddr = allowedChangeKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var rogueOwnKey = masterKey.Derive(new KeyPath("m/84'/1'/0'/1/99"));
        var rogueOwnAddr = rogueOwnKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(100_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(50_000), allowedChangeAddr);
        tx.Outputs.Add(Money.Satoshis(40_000), rogueOwnAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Outputs[1].HDKeyPaths.Add(
            rogueOwnKey.GetPublicKey(),
            new RootedKeyPath(fp, new KeyPath("84'/1'/0'/1/99")));

        var policy = new SigningPolicy
        {
            AllowedScripts = new HashSet<Script> { allowedChangeAddr.ScriptPubKey },
            MaxFeeSats = 100_000,
            StrictAllowedScriptsOnly = true
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => signer.SignPsbtAsync(psbt.ToBase64(), network, policy));
        Assert.Contains("exceeds policy", ex.Message);
    }

    [Fact]
    public async Task NonStrict_OwnRgbLibDescriptorCoveredOutputOutsideAllowlist_Allowed()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        var network = Network.RegTest;
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var vanillaKey = masterKey.Derive(new KeyPath("m/84'/1'/0'/0/0"));
        var inputAddr = vanillaKey.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var ownOtherKey = masterKey.Derive(new KeyPath("m/86'/1'/0'/1/5"));
        var ownOtherAddr = ownOtherKey.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var fundingTx = Transaction.Create(network);
        fundingTx.Outputs.Add(Money.Satoshis(10_000), inputAddr);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(fundingTx, 0), Script.Empty);
        tx.Outputs.Add(Money.Satoshis(9_000), ownOtherAddr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = fundingTx.Outputs[0];
        psbt.Outputs[0].HDTaprootKeyPaths.Add(
            ownOtherKey.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath("86'/1'/0'/1/5"))));

        var policy = new SigningPolicy
        {
            AllowedScripts = new HashSet<Script>(),
            MaxFeeSats = 10_000
        };

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy);
        Assert.NotNull(signed);
    }
}
