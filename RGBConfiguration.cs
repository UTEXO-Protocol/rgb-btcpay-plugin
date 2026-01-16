using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RGB;

public record RGBConfiguration(
    [property: JsonPropertyName("rgb_node_url")] string RgbNodeUrl,
    [property: JsonPropertyName("network")] string Network = "regtest");

public class RGBPaymentMethodConfig
{
    public string WalletId { get; set; } = "";
    public string? DefaultAssetId { get; set; }
    public bool AcceptAnyAsset { get; set; }
    public List<string> AcceptedAssetIds { get; set; } = new();
}
