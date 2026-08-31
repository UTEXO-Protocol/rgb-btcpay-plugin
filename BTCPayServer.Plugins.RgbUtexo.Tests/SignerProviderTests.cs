using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SignerProviderTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void RegisterSigner_Regtest_DerivesTestnetCoinType()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-regtest", TestMnemonic, Network.RegTest);

        var signer = GetSigner(provider, "wallet-regtest");
        Assert.NotNull(signer);
        Assert.StartsWith("tpub", signer!.XpubRgbLibVanilla);
    }

    [Fact]
    public void RegisterSigner_Testnet_DerivesTestnetCoinType()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-testnet", TestMnemonic, Network.TestNet);

        var signer = GetSigner(provider, "wallet-testnet");
        Assert.NotNull(signer);
        Assert.StartsWith("tpub", signer!.XpubRgbLibVanilla);
    }

    [Fact]
    public void RegisterSigner_Mainnet_DerivesMainnetCoinType()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("wallet-mainnet", TestMnemonic, Network.Main);

        var signer = GetSigner(provider, "wallet-mainnet");
        Assert.NotNull(signer);
        Assert.StartsWith("xpub", signer!.XpubRgbLibVanilla);
    }

    [Fact]
    public void MultipleNetworks_EachSignerDerivesCorrectKeys()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("regtest", TestMnemonic, Network.RegTest);
        provider.RegisterSigner("mainnet", TestMnemonic, Network.Main);

        var regtest = GetSigner(provider, "regtest");
        var mainnet = GetSigner(provider, "mainnet");

        Assert.NotNull(regtest);
        Assert.NotNull(mainnet);
        Assert.NotEqual(regtest!.XpubRgbLibVanilla, mainnet!.XpubRgbLibVanilla);
        Assert.StartsWith("tpub", regtest.XpubRgbLibVanilla);
        Assert.StartsWith("xpub", mainnet.XpubRgbLibVanilla);
    }

    [Fact]
    public void RegisterSigner_SameWalletId_Overwrites()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("w1", TestMnemonic, Network.RegTest);
        var first = GetSigner(provider, "w1");

        provider.RegisterSigner("w1", TestMnemonic, Network.Main);
        var second = GetSigner(provider, "w1");

        Assert.NotEqual(first!.XpubRgbLibVanilla, second!.XpubRgbLibVanilla);
    }

    [Fact]
    public void UnloadSigner_RemovesSigner()
    {
        var provider = CreateProvider();
        provider.RegisterSigner("w1", TestMnemonic, Network.RegTest);
        Assert.NotNull(GetSigner(provider, "w1"));

        provider.UnloadSigner("w1");
        Assert.Null(GetSigner(provider, "w1"));
    }

    [Fact]
    public async Task GetSignerAsync_UnknownWallet_ReturnsNull()
    {
        var provider = CreateProvider();
        var signer = await provider.GetSignerAsync("nonexistent");
        Assert.Null(signer);
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

    static IRgbWalletSigner? GetSigner(RgbWalletSignerProvider provider, string walletId)
    {
        return provider.GetSignerAsync(walletId).GetAwaiter().GetResult();
    }
}
