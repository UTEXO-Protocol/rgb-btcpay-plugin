using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class NetworkHelper
{
    public static Network GetNetwork(string network) => network.ToLowerInvariant() switch
    {
        "mainnet" or "main" => Network.Main,
        "testnet" or "test" => Network.TestNet,
        "signet" => Network.GetNetwork("signet") ?? Network.TestNet,
        _ => Network.RegTest
    };

    public static string MapNetworkToRgbLibFormat(string network) => network.ToLowerInvariant() switch
    {
        "mainnet" or "main" => "Mainnet",
        "testnet" or "test" => "Testnet",
        "signet" => "Signet",
        _ => "Regtest"
    };
}



