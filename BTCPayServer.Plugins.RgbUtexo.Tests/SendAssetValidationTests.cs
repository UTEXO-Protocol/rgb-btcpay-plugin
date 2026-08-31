using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SendAssetValidationTests
{
    const string AssetId = "rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-TMsGMSA";
    const string OtherAssetId = "rgb:AAAA-BBBB-CCCC";

    static RgbInvoiceData MakeInvoice(string? assetId = null, long? amount = null, long expirationTs = 0) => new()
    {
        RecipientId = "utxob:abc123",
        AssetId = assetId,
        Amount = amount,
        ExpirationTimestamp = expirationTs,
        TransportEndpoints = ["rpc://proxy.example.com:3000/json-rpc"]
    };

    static List<RgbAsset> MakeAssets(string assetId = AssetId, ulong spendable = 10000) =>
    [
        new() { AssetId = assetId, Ticker = "USDT", Name = "Tether", Precision = 0, SpendableBalance = spendable }
    ];

    [Fact]
    public void ValidRequest_ReturnsAsset()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: 100);
        var (resolvedId, asset) = RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 100, MakeAssets());
        Assert.Equal(AssetId, resolvedId);
        Assert.Equal("USDT", asset.Ticker);
    }

    [Fact]
    public void InvoiceAssetNull_UsesSelectedAsset()
    {
        var invoice = MakeInvoice(assetId: null, amount: 100);
        var (resolvedId, _) = RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 100, MakeAssets());
        Assert.Equal(AssetId, resolvedId);
    }

    [Fact]
    public void BothAssetIdsNull_Throws()
    {
        var invoice = MakeInvoice(assetId: null);
        var ex = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, "", 100, MakeAssets()));
        Assert.Contains("Asset ID must be provided", ex.Message);
    }

    [Fact]
    public void AssetIdMismatch_Throws()
    {
        var invoice = MakeInvoice(assetId: AssetId);
        var ex = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, OtherAssetId, 100, MakeAssets()));
        Assert.Contains("different asset", ex.Message);
    }

    [Fact]
    public void AmountMismatch_Throws()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: 200);
        var ex = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 100, MakeAssets()));
        Assert.Contains("requires exactly", ex.Message);
    }

    [Fact]
    public void AssetNotInWallet_Throws()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: 100);
        var ex = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 100, []));
        Assert.Contains("not found in wallet", ex.Message);
    }

    [Fact]
    public void InsufficientBalance_Throws()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: 500);
        var ex = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 500, MakeAssets(spendable: 100)));
        Assert.Contains("Insufficient", ex.Message);
    }

    [Fact]
    public void ExactBalance_Passes()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: 100);
        var (_, asset) = RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 100, MakeAssets(spendable: 100));
        Assert.Equal(100ul, asset.SpendableBalance);
    }

    [Fact]
    public void InvoiceAmountNull_AnyAmountAccepted()
    {
        var invoice = MakeInvoice(assetId: AssetId, amount: null);
        var (_, asset) = RGBWalletService.ValidateSendAssetRequest(invoice, AssetId, 999, MakeAssets());
        Assert.NotNull(asset);
    }
}
