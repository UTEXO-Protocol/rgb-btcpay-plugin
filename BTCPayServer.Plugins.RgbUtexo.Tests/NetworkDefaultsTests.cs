using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("EnvVars")]
public class NetworkDefaultsTests
{
    [Fact]
    public void Utexo_DefaultsToUtexoEndpoints()
    {
        var utexo = NetworkSettings.GetForNetwork("utexo");
        Assert.Equal("tcp://esplora-api.utexo.com:50001", utexo.ElectrumUrl);
        Assert.Equal("rpcs://rgb-proxy.utexo.com/json-rpc", utexo.ProxyEndpoint);
    }

    [Fact]
    public void Signet_DefaultsUnchanged()
    {
        var signet = NetworkSettings.GetForNetwork("signet");
        Assert.Equal("ssl://electrum.iriswallet.com:50033", signet.ElectrumUrl);
        Assert.Equal("rpcs://proxy.iriswallet.com/0.2/json-rpc", signet.ProxyEndpoint);
    }

    [Fact]
    public void OtherNetworks_DefaultsUnchanged()
    {
        var testnet = NetworkSettings.GetForNetwork("testnet");
        Assert.Equal("ssl://electrum.iriswallet.com:50013", testnet.ElectrumUrl);
        Assert.Equal("rpcs://proxy.iriswallet.com/0.2/json-rpc", testnet.ProxyEndpoint);

        var mainnet = NetworkSettings.GetForNetwork("mainnet");
        Assert.Equal("ssl://electrum.iriswallet.com:50003", mainnet.ElectrumUrl);
        Assert.Equal("rpcs://proxy.iriswallet.com/0.2/json-rpc", mainnet.ProxyEndpoint);

        var regtest = NetworkSettings.GetForNetwork("regtest");
        Assert.Equal("tcp://regtest.thunderstack.org:50001", regtest.ElectrumUrl);
        Assert.Equal("rpc://regtest.thunderstack.org:3000/json-rpc", regtest.ProxyEndpoint);
    }

    [Fact]
    public void AvailableNetworks_IncludesUtexo()
    {
        var nets = NetworkSettings.AvailableNetworks;
        Assert.Equal(5, nets.Length);
        Assert.Contains("regtest", nets);
        Assert.Contains("testnet", nets);
        Assert.Contains("signet", nets);
        Assert.Contains("utexo", nets);
        Assert.Contains("mainnet", nets);
    }

    [Fact]
    public void MapNetworkFolder_Utexo_ReturnsUtexo()
    {
        Assert.Equal("Utexo", RGBConfiguration.MapNetworkFolder("utexo"));
        Assert.Equal("Signet", RGBConfiguration.MapNetworkFolder("signet"));
    }

    [Fact]
    public void NetworkHelper_Utexo_MapsCorrectly()
    {
        Assert.Equal("Signet", NetworkHelper.MapNetworkToRgbLibFormat("utexo"));
        Assert.Equal(Network.GetNetwork("signet"), NetworkHelper.GetNetwork("utexo"));
    }

    [Fact]
    public void Utexo_EnvOverrideWinsOverDefault()
    {
        var priorElectrum = Environment.GetEnvironmentVariable("RGB_ELECTRUM_URL");
        var priorProxy = Environment.GetEnvironmentVariable("RGB_PROXY_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("RGB_ELECTRUM_URL", "http://override-electrum");
            Environment.SetEnvironmentVariable("RGB_PROXY_ENDPOINT", "rpc://override-proxy");

            var resolved = RGBConfiguration.GetNetworkSettings("utexo");
            Assert.Equal("http://override-electrum", resolved.ElectrumUrl);
            Assert.Equal("rpc://override-proxy", resolved.ProxyEndpoint);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RGB_ELECTRUM_URL", priorElectrum);
            Environment.SetEnvironmentVariable("RGB_PROXY_ENDPOINT", priorProxy);
        }
    }

    [Fact]
    public void AllowedRgbNetworksFor_Signet_IncludesBoth()
    {
        var allowed = RGBController.AllowedRgbNetworksFor(new ChainName("Signet"));
        Assert.Contains("signet", allowed);
        Assert.Contains("utexo", allowed);
        Assert.Equal("signet", allowed[0]);
    }

    [Fact]
    public void AllowedRgbNetworksFor_OtherChains_AreSingletons()
    {
        Assert.Equal(new[] { "mainnet" }, RGBController.AllowedRgbNetworksFor(new ChainName("Mainnet")));
        Assert.Equal(new[] { "testnet" }, RGBController.AllowedRgbNetworksFor(new ChainName("Testnet")));
        Assert.Equal(new[] { "regtest" }, RGBController.AllowedRgbNetworksFor(new ChainName("Regtest")));
    }

    [Fact]
    public void MapChainNameToRgbNetwork_Signet_DefaultsToSignet()
    {
        Assert.Equal("signet", RGBController.MapChainNameToRgbNetwork(new ChainName("Signet")));
    }

    [Fact]
    public void RGBSetupViewModel_AvailableNetworksDefault_MatchesNetworkSettings()
    {
        var defaults = new RGBSetupViewModel().AvailableNetworks;
        Assert.Equal(NetworkSettings.AvailableNetworks, defaults);
    }

    [Fact]
    public void AllowsPlainElectrum_RegtestAndUtexo_True()
    {
        Assert.True(NetworkSettings.AllowsPlainElectrum("regtest"));
        Assert.True(NetworkSettings.AllowsPlainElectrum("utexo"));
        Assert.True(NetworkSettings.AllowsPlainElectrum("Utexo"));
        Assert.True(NetworkSettings.AllowsPlainElectrum("REGTEST"));
    }

    [Fact]
    public void AllowsPlainElectrum_OtherNetworks_False()
    {
        Assert.False(NetworkSettings.AllowsPlainElectrum("signet"));
        Assert.False(NetworkSettings.AllowsPlainElectrum("testnet"));
        Assert.False(NetworkSettings.AllowsPlainElectrum("mainnet"));
        Assert.False(NetworkSettings.AllowsPlainElectrum("unknown"));
    }

    [Fact]
    public void ValidateSelectedNetwork_SignetChain_AcceptsSignetAndUtexo()
    {
        Assert.Null(RGBController.ValidateSelectedNetwork("signet", new ChainName("Signet")));
        Assert.Null(RGBController.ValidateSelectedNetwork("utexo", new ChainName("Signet")));
    }

    [Fact]
    public void ValidateSelectedNetwork_MainnetChain_RejectsUtexo()
    {
        var err1 = RGBController.ValidateSelectedNetwork("utexo", new ChainName("Mainnet"));
        Assert.NotNull(err1);
        Assert.Contains("not allowed", err1);

        var err2 = RGBController.ValidateSelectedNetwork("signet", new ChainName("Mainnet"));
        Assert.NotNull(err2);
        Assert.Contains("not allowed", err2);
    }

    [Fact]
    public void ValidateSelectedNetwork_InvalidInput_ReturnsInvalidSelection()
    {
        Assert.Equal("Invalid network selection", RGBController.ValidateSelectedNetwork(null, new ChainName("Signet")));
        Assert.Equal("Invalid network selection", RGBController.ValidateSelectedNetwork("", new ChainName("Signet")));
        Assert.Equal("Invalid network selection", RGBController.ValidateSelectedNetwork("   ", new ChainName("Signet")));
        Assert.Equal("Invalid network selection", RGBController.ValidateSelectedNetwork("fake-network", new ChainName("Signet")));
    }
}
