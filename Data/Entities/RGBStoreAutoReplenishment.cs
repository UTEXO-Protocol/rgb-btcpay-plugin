namespace BTCPayServer.Plugins.RgbUtexo.Data.Entities;

public enum RgbAutoReplenishmentDecision
{
    Undecided = 0,
    Granted = 1,
    Revoked = 2
}

public class RGBStoreAutoReplenishment
{
    public string StoreId { get; set; } = "";
    public RgbAutoReplenishmentDecision Decision { get; set; }
    public string? DecidedForWalletId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
}
