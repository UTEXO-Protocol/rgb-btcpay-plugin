using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.RgbUtexo.Data.Entities;

public class RGBWallet
{
    [Key]
    public string Id { get; set; } = "";
    
    [Required]
    public string StoreId { get; set; } = "";
    
    public string Name { get; set; } = "RGB Wallet";
    
    [Required]
    public string XpubVanilla { get; set; } = "";
    
    [Required]
    public string XpubColored { get; set; } = "";
    
    [Required]
    public string MasterFingerprint { get; set; } = "";
    
    public string EncryptedMnemonic { get; set; } = "";
    
    [Required]
    public string Network { get; set; } = "";  // Set from BTCPay network configuration
    
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncAt { get; set; }
    public bool IsActive { get; set; } = true;
    public int MaxAllocationsPerUtxo { get; set; } = 10;
}


