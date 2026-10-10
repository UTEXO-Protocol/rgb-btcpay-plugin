using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class AssetMatchingTests
{
    [Fact]
    public void CorrectAsset_ReturnsTrue()
    {
        Assert.True(RGBInvoiceListener.IsAssetMatch("USDT_ASSET_ID", "USDT_ASSET_ID"));
    }

    [Fact]
    public void WrongAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("USDT_ASSET_ID", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void NullInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch(null, "JUNK_ASSET_ID"));
    }

    [Fact]
    public void EmptyInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void WhitespaceInvoiceAsset_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("  ", "JUNK_ASSET_ID"));
    }

    [Fact]
    public void CaseSensitive_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("usdt_asset_id", "USDT_ASSET_ID"));
    }

    [Fact]
    public void BothEmpty_ReturnsFalse()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch("", ""));
    }

    [Fact]
    public void LegacyWildcardInvoice_NeverMatchesAnyTransfer()
    {
        var transferAssets = new[] { "USDT", "USDC", "BTC", "", "rgb:some-asset-id" };
        foreach (var transfer in transferAssets)
            Assert.False(RGBInvoiceListener.IsAssetMatch(null, transfer),
                $"Null-asset invoice must reject transfer with asset '{transfer}'");
    }

    // Same 32-byte ContractId under different presentations must settle.
    const string ContractA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";

    [Fact]
    public void SameContract_PrefixedVsBarePresentation_ReturnsTrue()
    {
        const string bare = "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw";
        Assert.True(RGBInvoiceListener.IsAssetMatch(ContractA, bare));
        Assert.True(RGBInvoiceListener.IsAssetMatch(bare, ContractA));
    }

    [Fact]
    public void SameContract_MnemonicSuffix_ReturnsTrue()
    {
        Assert.True(RGBInvoiceListener.IsAssetMatch(
            ContractA, ContractA + "#some-mnemonic-words-here"));
    }

    [Fact]
    public void DifferentContracts_ReturnsFalse()
    {
        const string contractB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";
        Assert.False(RGBInvoiceListener.IsAssetMatch(ContractA, contractB));
    }

    [Fact]
    public void UnparseableTransfer_NeverMatchesCanonicalInvoice()
    {
        Assert.False(RGBInvoiceListener.IsAssetMatch(ContractA, "JUNK_ASSET_ID"));
        Assert.False(RGBInvoiceListener.IsAssetMatch(ContractA, ""));
    }
}
