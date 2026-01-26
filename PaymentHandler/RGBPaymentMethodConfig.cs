using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodConfig
{
    [JsonPropertyName("walletId")] public string WalletId { get; set; } = "";
    [JsonPropertyName("defaultAssetId")] public string? DefaultAssetId { get; set; }
    [JsonPropertyName("acceptAnyAsset")] public bool AcceptAnyAsset { get; set; } = false;
    [JsonPropertyName("useWitnessReceive")] public bool UseWitnessReceive { get; set; } = true;
}


