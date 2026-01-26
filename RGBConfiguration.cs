using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo;

public class RGBConfiguration
{
    [JsonPropertyName("network")]
    public string Network { get; set; } = "regtest";

    [JsonPropertyName("electrum_url")]
    public string ElectrumUrl { get; set; } = "tcp://electrs:50001";

    [JsonPropertyName("rgb_data_dir")]
    public string RgbDataDir { get; set; } = "/data/rgb-wallets";

    [JsonPropertyName("max_allocations_per_utxo")]
    public int MaxAllocationsPerUtxo { get; set; } = 5;
    
    [JsonPropertyName("proxy_endpoint")]
    public string ProxyEndpoint { get; set; } = "rpc://proxy.iriswallet.com/0.2/json-rpc";

    public RGBConfiguration() { }

    public RGBConfiguration(string network, string? electrumUrl = null, string? rgbDataDir = null, string? proxyEndpoint = null)
    {
        Network = network;
        if (electrumUrl != null) ElectrumUrl = electrumUrl;
        if (rgbDataDir != null) RgbDataDir = rgbDataDir;
        if (proxyEndpoint != null) ProxyEndpoint = proxyEndpoint;
    }
}
