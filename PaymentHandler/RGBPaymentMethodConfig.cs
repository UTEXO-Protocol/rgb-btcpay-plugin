using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public class RGBPaymentMethodConfig
{
    [JsonPropertyName("walletId")] public string WalletId { get; set; } = "";
    [JsonPropertyName("defaultAssetId")] public string? DefaultAssetId { get; set; }
    [JsonPropertyName("acceptAnyAsset")] public bool AcceptAnyAsset { get; set; } = false;
    [JsonPropertyName("useWitnessReceive")] public bool UseWitnessReceive { get; set; } = true;
    [JsonPropertyName("utxoCount")] public int UtxoCount { get; set; } = 4;
    [JsonPropertyName("utxoSize")] public int UtxoSize { get; set; } = 1000;
    [JsonPropertyName("maxAllocationsPerUtxo")] public int MaxAllocationsPerUtxo { get; set; } = 10;
}


