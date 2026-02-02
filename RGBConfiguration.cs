using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo;

public class NetworkSettings
{
    public string ElectrumUrl { get; set; } = "";
    public string ProxyEndpoint { get; set; } = "";
    
    public static readonly Dictionary<string, NetworkSettings> Defaults = new()
    {
        ["regtest"] = new NetworkSettings
        {
            ElectrumUrl = "tcp://regtest.thunderstack.org:50001",
            ProxyEndpoint = "rpc://proxy.iriswallet.com/0.2/json-rpc"
        },
        ["testnet"] = new NetworkSettings
        {
            ElectrumUrl = "ssl://electrum.iriswallet.com:50013",
            ProxyEndpoint = "rpcs://proxy.iriswallet.com/0.2/json-rpc"
        },
        ["mainnet"] = new NetworkSettings
        {
            ElectrumUrl = "ssl://electrum.iriswallet.com:50003",
            ProxyEndpoint = "rpcs://proxy.iriswallet.com/0.2/json-rpc"
        }
    };
    
    public static NetworkSettings GetForNetwork(string network)
    {
        var key = network.ToLowerInvariant();
        return Defaults.TryGetValue(key, out var settings) ? settings : Defaults["regtest"];
    }
    
    public static string[] AvailableNetworks => ["regtest", "testnet", "mainnet"];
}

public class RGBConfiguration
{
    [JsonPropertyName("network")]
    public string Network { get; set; } = "regtest";

    [JsonPropertyName("electrum_url")]
    public string ElectrumUrl { get; set; } = "tcp://electrs:50001";

    [JsonPropertyName("rgb_data_dir")]
    public string RgbDataDir { get; set; } = "/data/rgb-wallets";

    [JsonPropertyName("max_allocations_per_utxo")]
    public int MaxAllocationsPerUtxo { get; set; } = 10;
    
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
    
    public NetworkSettings GetNetworkSettings(string? walletNetwork = null)
    {
        var network = walletNetwork ?? Network;
        var envElectrum = Environment.GetEnvironmentVariable("RGB_ELECTRUM_URL");
        var envProxy = Environment.GetEnvironmentVariable("RGB_PROXY_ENDPOINT");
        
        var defaults = NetworkSettings.GetForNetwork(network);
        return new NetworkSettings
        {
            ElectrumUrl = !string.IsNullOrEmpty(envElectrum) ? envElectrum : defaults.ElectrumUrl,
            ProxyEndpoint = !string.IsNullOrEmpty(envProxy) ? envProxy : defaults.ProxyEndpoint
        };
    }
}
