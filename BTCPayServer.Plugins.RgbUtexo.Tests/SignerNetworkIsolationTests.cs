using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SignerNetworkIsolationTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Theory]
    [InlineData("regtest", "Regtest")]
    [InlineData("testnet", "Testnet")]
    [InlineData("mainnet", "Mainnet")]
    public void SignerXpubRgbLibVanilla_EqualsTheRealBindingsAccountXpubVanilla(
        string networkName, string rgbLibNetworkName)
    {
        var network = networkName switch
        {
            "regtest" => Network.RegTest,
            "testnet" => Network.TestNet,
            "mainnet" => Network.Main,
            _ => throw new ArgumentException(networkName)
        };

        using var doc = JsonDocument.Parse(RgbLibWallet.RestoreKeys(rgbLibNetworkName, TestMnemonic));
        var accountXpubVanilla = doc.RootElement.GetProperty("account_xpub_vanilla").GetString();

        using var signer = new MemoryWalletSigner(TestMnemonic, network);

        Assert.True(signer.XpubRgbLibVanilla == accountXpubVanilla,
            $"{networkName}: signer.XpubRgbLibVanilla must equal the pinned rgb-lib binding's "
            + $"account_xpub_vanilla. Expected '{accountXpubVanilla}', got '{signer.XpubRgbLibVanilla}'. "
            + "This property exists to be substitutable for rgb-lib's vanilla account xpub; a signer that "
            + "derives some other account under this name would build a vanilla descriptor BDK cannot "
            + "authenticate and would refuse every send.");
    }

    [Fact]
    public void SameWallet_DifferentNetworks_DifferentXpubs()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);
        using var testnet = new MemoryWalletSigner(TestMnemonic, Network.TestNet);

        Assert.NotEqual(regtest.XpubRgbLibVanilla, mainnet.XpubRgbLibVanilla);
        Assert.Equal(regtest.XpubRgbLibVanilla, testnet.XpubRgbLibVanilla);
    }

    [Fact]
    public void Regtest_UsesCoinType1()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        Assert.StartsWith("tpub", signer.XpubRgbLibVanilla);
    }

    [Fact]
    public void Mainnet_UsesCoinType0()
    {
        using var signer = new MemoryWalletSigner(TestMnemonic, Network.Main);
        Assert.StartsWith("xpub", signer.XpubRgbLibVanilla);
    }

    [Fact]
    public void MasterFingerprint_ConsistentAcrossNetworks()
    {
        using var regtest = new MemoryWalletSigner(TestMnemonic, Network.RegTest);
        using var mainnet = new MemoryWalletSigner(TestMnemonic, Network.Main);

        Assert.Equal(regtest.MasterFingerprint, mainnet.MasterFingerprint);
    }

    [Fact]
    public void Provider_MultipleWallets_EachGetsOwnNetwork()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("regtest-wallet", TestMnemonic, Network.RegTest);
        provider.RegisterSigner("mainnet-wallet", TestMnemonic, Network.Main);

        var regtestSigner = provider.GetSignerAsync("regtest-wallet").GetAwaiter().GetResult();
        var mainnetSigner = provider.GetSignerAsync("mainnet-wallet").GetAwaiter().GetResult();

        Assert.NotNull(regtestSigner);
        Assert.NotNull(mainnetSigner);
        Assert.StartsWith("tpub", regtestSigner!.XpubRgbLibVanilla);
        Assert.StartsWith("xpub", mainnetSigner!.XpubRgbLibVanilla);
    }

    [Fact]
    public void Provider_ReplacingWallet_ChangesNetwork()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-1", TestMnemonic, Network.RegTest);
        var before = provider.GetSignerAsync("wallet-1").GetAwaiter().GetResult();
        Assert.StartsWith("tpub", before!.XpubRgbLibVanilla);

        provider.RegisterSigner("wallet-1", TestMnemonic, Network.Main);
        var after = provider.GetSignerAsync("wallet-1").GetAwaiter().GetResult();
        Assert.StartsWith("xpub", after!.XpubRgbLibVanilla);
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
