using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// M9 regression: create regtest + testnet + mainnet signers from the same mnemonic,
/// verify each can produce a valid signature on a canned PSBT using the correct
/// derivation paths (coin type 0 for mainnet, 1 for testnet/regtest).
/// </summary>
public class MultiNetworkSigningTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public async Task EachNetwork_ProducesValidSignature_VanillaPath(string networkName)
    {
        var network = networkName switch
        {
            "regtest" => Network.RegTest,
            "testnet" => Network.TestNet,
            "mainnet" => Network.Main,
            _ => throw new ArgumentException(networkName)
        };

        using var signer = new MemoryWalletSigner(TestMnemonic, network);
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var isTestnet = network != Network.Main;
        var vanillaPath = isTestnet ? "m/84'/1'/0'/0/0" : "m/84'/0'/0'/0/0";
        var key = masterKey.Derive(new KeyPath(vanillaPath));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(Money.Satoshis(900), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(1000), addr.ScriptPubKey);

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network,
            new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });

        var result = PSBT.Parse(signed, network);
        Assert.True(result.Inputs[0].PartialSigs.Count > 0 || result.Inputs[0].FinalScriptWitness != null,
            $"{networkName}: vanilla path should produce valid signature");
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public async Task EachNetwork_ProducesValidSignature_TaprootRgbPath(string networkName)
    {
        var network = networkName switch
        {
            "regtest" => Network.RegTest,
            "testnet" => Network.TestNet,
            "mainnet" => Network.Main,
            _ => throw new ArgumentException(networkName)
        };

        using var signer = new MemoryWalletSigner(TestMnemonic, network);
        var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
        var fp = masterKey.GetPublicKey().GetHDFingerPrint();

        var isTestnet = network != Network.Main;
        var rgbCoinType = isTestnet ? 827167 : 827166;
        var rgbPath = $"m/86'/{rgbCoinType}'/0'/0/0";
        var key = masterKey.Derive(new KeyPath(rgbPath));
        var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.TaprootBIP86, network);

        var tx = Transaction.Create(network);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(Money.Satoshis(900), addr);

        var psbt = PSBT.FromTransaction(tx, network);
        psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(1000), addr.ScriptPubKey);

        psbt.Inputs[0].HDTaprootKeyPaths.Add(
            key.GetPublicKey().GetTaprootFullPubKey(),
            new TaprootKeyPath(new RootedKeyPath(fp, new KeyPath($"86'/{rgbCoinType}'/0'/0/0"))));

        var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network,
            new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });

        var result = PSBT.Parse(signed, network);
        Assert.True(result.Inputs[0].TaprootKeySignature != null || result.Inputs[0].FinalScriptWitness != null,
            $"{networkName}: RGB taproot path should produce valid signature");
    }

    [Fact]
    public void AllThreeNetworks_DeriveDistinctXpubs()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var testnet = new MemoryWalletSigner(TestMnemonic, Network.TestNet);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);

        Assert.NotEqual(regtest.XpubRgbLibVanilla, mainnet.XpubRgbLibVanilla);

        Assert.Equal(regtest.XpubRgbLibVanilla, testnet.XpubRgbLibVanilla);

        Assert.StartsWith("tpub", regtest.XpubRgbLibVanilla);
        Assert.StartsWith("tpub", testnet.XpubRgbLibVanilla);
        Assert.StartsWith("xpub", mainnet.XpubRgbLibVanilla);
    }

    [Fact]
    public async Task Provider_AllThreeNetworks_EachSignsCorrectly()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("regtest-w", TestMnemonic, Network.RegTest);
        provider.RegisterSigner("testnet-w", TestMnemonic, Network.TestNet);
        provider.RegisterSigner("mainnet-w", TestMnemonic, Network.Main);

        var networks = new[]
        {
            ("regtest-w", Network.RegTest),
            ("testnet-w", Network.TestNet),
            ("mainnet-w", Network.Main)
        };

        foreach (var (walletId, network) in networks)
        {
            var signer = await provider.GetSignerAsync(walletId);
            Assert.NotNull(signer);

            var isTestnet = network != Network.Main;
            var expectedPrefix = isTestnet ? "tpub" : "xpub";
            Assert.StartsWith(expectedPrefix, signer!.XpubRgbLibVanilla);

            var masterKey = new Mnemonic(TestMnemonic).DeriveExtKey();
            var vanillaPath = isTestnet ? "m/84'/1'/0'/0/0" : "m/84'/0'/0'/0/0";
            var key = masterKey.Derive(new KeyPath(vanillaPath));
            var addr = key.GetPublicKey().GetAddress(ScriptPubKeyType.Segwit, network);

            var tx = Transaction.Create(network);
            tx.Inputs.Add(new OutPoint(uint256.One, 0));
            tx.Outputs.Add(Money.Satoshis(900), addr);
            var psbt = PSBT.FromTransaction(tx, network);
            psbt.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(1000), addr.ScriptPubKey);

            var signed = await signer.SignPsbtAsync(psbt.ToBase64(), network,
                new SigningPolicy { MaxUnknownOutputSats = 1000, MaxFeePercent = 20 });
            var result = PSBT.Parse(signed, network);
            Assert.True(result.Inputs[0].PartialSigs.Count > 0 || result.Inputs[0].FinalScriptWitness != null,
                $"{walletId}: signer from provider should produce valid signature");
        }
    }

    static RgbWalletSignerProvider CreateProvider()
    {
        var provider = new RgbWalletSignerProvider(null!, null!, null!);
        typeof(RgbWalletSignerProvider)
            .GetField("_started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(provider, new TaskCompletionSource());
        ((TaskCompletionSource)typeof(RgbWalletSignerProvider)
            .GetField("_started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(provider)!).SetResult();
        return provider;
    }
}
