namespace BTCPayServer.Plugins.RgbUtexo.Data.Entities;

public class RGBStoreNoticeState
{
    public string StoreId { get; set; } = "";
    public DateTimeOffset? NotAuthorizedNoticeSentAt { get; set; }
    public DateTimeOffset? CapDisabledNoticeSentAt { get; set; }
    public DateTimeOffset? ConfigOutOfBoundsNoticeSentAt { get; set; }
    public DateTimeOffset? PricingCodeHasNoRuleNoticeSentAt { get; set; }
}
