using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodConfig
{
    [JsonPropertyName("walletId")] [JsonProperty("walletId")] public string WalletId { get; set; } = "";
    [JsonPropertyName("defaultAssetId")] [JsonProperty("defaultAssetId")] public string? DefaultAssetId { get; set; }
    [JsonPropertyName("useWitnessReceive")] [JsonProperty("useWitnessReceive")] public bool UseWitnessReceive { get; set; } = true;
    [JsonPropertyName("utxoCount")] [JsonProperty("utxoCount")] public int UtxoCount { get; set; } = 4;
    [JsonPropertyName("utxoSize")] [JsonProperty("utxoSize")] public int UtxoSize { get; set; } = 1000;
    [JsonPropertyName("minConfirmations")] [JsonProperty("minConfirmations")] public int MinConfirmations { get; set; } = 1;
}


