using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.RGB.Data.Entities;

public class RGBAsset
{
    [Key]
    public string AssetId { get; set; } = "";
    
    public string WalletId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public int Precision { get; set; }
    public long IssuedSupply { get; set; }
    public bool AcceptForPayment { get; set; } = true;
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    
    public RGBWallet? Wallet { get; set; }
}


