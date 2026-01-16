using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.RGB.Data.Entities;

public class RGBInvoice
{
    [Key]
    public string Id { get; set; } = "";
    
    [Required]
    public string WalletId { get; set; } = "";
    
    public string? BtcPayInvoiceId { get; set; }
    
    [Required]
    public string Invoice { get; set; } = "";
    
    [Required]
    public string RecipientId { get; set; } = "";
    
    public string? AssetId { get; set; }
    public long? Amount { get; set; }
    public long? ReceivedAmount { get; set; }
    public long? ExpirationTimestamp { get; set; }
    public int? BatchTransferIdx { get; set; }
    public RGBInvoiceStatus Status { get; set; } = RGBInvoiceStatus.Pending;
    public bool IsBlind { get; set; }
    public string? Txid { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SettledAt { get; set; }
    
    public RGBWallet? Wallet { get; set; }
}

public enum RGBInvoiceStatus
{
    Pending = 0,
    WaitingConfirmations = 1,
    Settled = 2,
    Failed = 3,
    Expired = 4
}


