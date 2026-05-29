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
            ProxyEndpoint = "rpc://regtest.thunderstack.org:3000/json-rpc"
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
        },
        ["signet"] = new NetworkSettings
        {
            ElectrumUrl = "ssl://electrum.iriswallet.com:50033",
            ProxyEndpoint = "rpcs://proxy.iriswallet.com/0.2/json-rpc"
        },
        ["utexo"] = new NetworkSettings
        {
            ElectrumUrl = "tcp://esplora-api.utexo.com:50001",
            ProxyEndpoint = "rpcs://rgb-proxy.utexo.com/json-rpc"
        }
    };

    public static NetworkSettings GetForNetwork(string network)
    {
        var key = network.ToLowerInvariant();
        if (!Defaults.TryGetValue(key, out var settings))
            throw new ArgumentException($"Unknown RGB network: {network}. Expected one of: {string.Join(", ", Defaults.Keys)}");
        return settings;
    }

    public static string[] AvailableNetworks => ["regtest", "testnet", "signet", "utexo", "mainnet"];

    public static bool AllowsPlainElectrum(string network) =>
        network.Equals("regtest", StringComparison.OrdinalIgnoreCase)
        || network.Equals("utexo", StringComparison.OrdinalIgnoreCase);
}

public class RGBConfiguration
{
    [JsonPropertyName("rgb_base_dir")]
    public string RgbBaseDir { get; set; } = "/data";

    [JsonPropertyName("max_allocations_per_utxo")]
    public int MaxAllocationsPerUtxo { get; set; } = 10;

    [JsonPropertyName("allow_private_transport_endpoints")]
    public bool AllowPrivateTransportEndpoints { get; set; }

    public RGBConfiguration() { }

    public RGBConfiguration(string rgbBaseDir)
    {
        RgbBaseDir = rgbBaseDir;
    }

    static readonly object _migrationLock = new();

    public string GetWalletDataDir(string walletId, string walletNetwork)
    {
        var networkFolder = MapNetworkFolder(walletNetwork);
        var newPath = Path.Combine(RgbBaseDir, networkFolder, "rgb-wallets", walletId);
        if (Directory.Exists(newPath)) return newPath;

        var legacyPath = Path.Combine(RgbBaseDir, networkFolder, networkFolder, "rgb-wallets", walletId);
        lock (_migrationLock)
        {
            if (Directory.Exists(newPath)) return newPath;
            if (Directory.Exists(legacyPath))
            {
                var newParent = Path.GetDirectoryName(newPath)!;
                Directory.CreateDirectory(newParent);
                Directory.Move(legacyPath, newPath);
            }
        }

        return newPath;
    }

    internal static string MapNetworkFolder(string network) => network.ToLowerInvariant() switch
    {
        "mainnet" or "main" => "Main",
        "testnet" => "TestNet",
        "signet" => "Signet",
        "utexo" => "Utexo",
        "regtest" => "RegTest",
        _ => throw new ArgumentException($"Unknown RGB network: {network}")
    };

    public static NetworkSettings GetNetworkSettings(string walletNetwork)
    {
        var envElectrum = Environment.GetEnvironmentVariable("RGB_ELECTRUM_URL");
        var envProxy = Environment.GetEnvironmentVariable("RGB_PROXY_ENDPOINT");

        var defaults = NetworkSettings.GetForNetwork(walletNetwork);
        return new NetworkSettings
        {
            ElectrumUrl = !string.IsNullOrEmpty(envElectrum) ? envElectrum : defaults.ElectrumUrl,
            ProxyEndpoint = !string.IsNullOrEmpty(envProxy) ? envProxy : defaults.ProxyEndpoint
        };
    }
}
