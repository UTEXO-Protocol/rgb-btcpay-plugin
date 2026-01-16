namespace BTCPayServer.Plugins.RGB.PaymentHandler;

public class RGBPaymentData
{
    public string RecipientId { get; set; } = "";
    public string? Txid { get; set; }
    public string? AssetId { get; set; }
    public long Amount { get; set; }
    public int TransferIdx { get; set; }
    
    public RGBPaymentData()
    {
    }

    public RGBPaymentData(string recipientId, string? txid, long amount)
    {
        RecipientId = recipientId;
        Txid = txid;
        Amount = amount;
    }
}


