namespace BTCPayServer.Plugins.RGB.PaymentHandler;

public class RGBPromptDetails
{
    public string WalletId { get; set; } = "";
    public string RgbInvoiceId { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string? AssetId { get; set; }
    public string? AssetTicker { get; set; }
    public string? AssetName { get; set; }
    public int AssetPrecision { get; set; } = 0;
    public long AmountInAssetUnits { get; set; }
}


