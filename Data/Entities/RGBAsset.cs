namespace BTCPayServer.Plugins.RgbUtexo.Data.Entities;

public class RGBAsset
{
    public string AssetId { get; set; } = "";
    
    public string WalletId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public int Precision { get; set; }
    public ulong IssuedSupply { get; set; }
    public bool AcceptForPayment { get; set; } = false;
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    public RGBWallet? Wallet { get; set; }
}


