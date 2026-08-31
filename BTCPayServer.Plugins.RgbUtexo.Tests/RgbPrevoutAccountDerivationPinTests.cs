using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPrevoutAccountDerivationPinTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    static Network NetworkFor(string name) => name switch
    {
        "regtest" => Network.RegTest,
        "testnet" => Network.TestNet,
        "mainnet" => Network.Main,
        _ => throw new ArgumentException(name)
    };

    static string BitcoinCoinType(Network network) => network == Network.Main ? "0" : "1";

    static string RgbCoinType(Network network) => network == Network.Main ? "827166" : "827167";

    static MemoryWalletSigner.PrevoutAccount Classify(MemoryWalletSigner signer, string accountPath,
        string describedAs)
    {
        var path = new KeyPath($"{accountPath}/0/0");
        Assert.True(signer.TryClassifyAccount(path, out var account),
            $"{path} is {describedAs} and must classify; TryClassifyAccount refused it outright. "
            + "_allowedAccountPrefixes no longer contains this derivation, or TryClassifyAccount no "
            + "longer compares against the element that holds it.");
        return account;
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public void RgbLibVanillaIsBoundToTheBip86BitcoinCoinTypeDerivation(string networkName)
    {
        var network = NetworkFor(networkName);
        using var signer = new MemoryWalletSigner(TestMnemonic, network);
        var accountPath = $"86'/{BitcoinCoinType(network)}'/0'";

        var account = Classify(signer, accountPath, "rgb-lib's VANILLA keychain account");

        Assert.True(account == MemoryWalletSigner.PrevoutAccount.RgbLibVanilla,
            $"{networkName}: m/{accountPath} is the account rgb-lib returns as account_xpub_vanilla, so it "
            + $"must classify as {MemoryWalletSigner.PrevoutAccount.RgbLibVanilla}; it classified as "
            + $"{account}. Any other answer means _allowedAccountPrefixes has been reordered or "
            + "TryClassifyAccount indexes it differently: the vanilla-only input guard would then accept "
            + "allocation-bearing colored inputs.");
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public void RgbLibColoredIsBoundToTheBip86RgbCoinTypeDerivation(string networkName)
    {
        var network = NetworkFor(networkName);
        using var signer = new MemoryWalletSigner(TestMnemonic, network);
        var accountPath = $"86'/{RgbCoinType(network)}'/0'";

        var account = Classify(signer, accountPath, "rgb-lib's COLORED keychain account");

        Assert.True(account == MemoryWalletSigner.PrevoutAccount.RgbLibColored,
            $"{networkName}: m/{accountPath} is the account rgb-lib returns as account_xpub_colored and is "
            + $"where every RGB allocation lives, so it must classify as "
            + $"{MemoryWalletSigner.PrevoutAccount.RgbLibColored}; it classified as {account}. Any other "
            + "answer means _allowedAccountPrefixes has been reordered or TryClassifyAccount indexes it "
            + "differently: carry-forward successors on the real colored account would then be refused and "
            + "every multi-contract send would break.");
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("testnet")]
    [InlineData("mainnet")]
    public void UnusedBip84IsBoundToTheBip84Derivation(string networkName)
    {
        var network = NetworkFor(networkName);
        using var signer = new MemoryWalletSigner(TestMnemonic, network);
        var accountPath = $"84'/{BitcoinCoinType(network)}'/0'";

        var account = Classify(signer, accountPath, "the vestigial BIP84 account");

        Assert.True(account == MemoryWalletSigner.PrevoutAccount.UnusedBip84,
            $"{networkName}: m/{accountPath} is a BIP84 account rgb-lib never produces a descriptor for, so "
            + $"it must classify as {MemoryWalletSigner.PrevoutAccount.UnusedBip84}; it classified as "
            + $"{account}.");
    }
}
