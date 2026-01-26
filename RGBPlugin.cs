using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo;

public class RGBPlugin : BaseBTCPayServerPlugin
{
    internal const string PluginNavKey = nameof(RGBPlugin) + "Nav";
    internal static readonly PaymentMethodId RGBPaymentMethodId = new("RGB");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies =>
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.1.0" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var ctx = (PluginServiceCollection)services;

        var config = LoadConfiguration(ctx);
        if (config == null) return;

        services.AddSingleton(config);
        services.AddSingleton<RGBPluginDbContextFactory>();
        services.AddDbContext<RGBPluginDbContext>((sp, opts) =>
        {
            sp.GetRequiredService<RGBPluginDbContextFactory>().ConfigureBuilder(opts);
        });
        services.AddStartupTask<RGBPluginMigrationRunner>();

        services.AddSingleton<IRgbLibService, RgbLibService>();
        services.AddSingleton<MnemonicProtectionService>();
        services.AddSingleton<RgbWalletSignerProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<RgbWalletSignerProvider>());
        services.AddSingleton<RGBWalletService>();
        services.AddSingleton<RGBPaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(sp => sp.GetRequiredService<RGBPaymentMethodHandler>());

        services.AddSingleton<RGBCheckoutModelExtension>();
        services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<RGBCheckoutModelExtension>());

        services.AddSingleton<RGBInvoiceListener>();
        services.AddHostedService(sp => sp.GetRequiredService<RGBInvoiceListener>());
        services.AddUIExtension("checkout-end", "RGB/RGBMethodCheckout");
        services.AddUIExtension("store-wallets-nav", "/Views/RGB/RGBWalletNav.cshtml");
        services.AddDefaultPrettyName(RGBPaymentMethodId, "RGB");
    }

    private static RGBConfiguration? LoadConfiguration(PluginServiceCollection ctx)
    {
        var netType = DefaultConfiguration.GetNetworkType(
            ctx.BootstrapServices.GetRequiredService<IConfiguration>());

        var network = netType.ToString() switch
        {
            "Main" => "mainnet",
            "TestNet" => "testnet",
            "Signet" => "signet",
            _ => "regtest"
        };

        var dataDir = new DataDirectories()
            .Configure(ctx.BootstrapServices.GetRequiredService<IConfiguration>())
            .DataDir;

        var configPath = Path.Combine(dataDir, "rgb.json");

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var fromFile = JsonSerializer.Deserialize<RGBConfiguration>(json);
                if (fromFile != null)
                {
                    return fromFile;
                }
            }
            catch (JsonException ex)
            {
                var logger = ctx.BootstrapServices.GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>();
                logger?.LogWarning(ex, "Failed to parse rgb.json config at {Path}, using defaults", configPath);
            }
        }

        var electrumUrl = ResolveElectrumUrl(netType);
        var rgbDataDir = ResolveRgbDataDir(dataDir);
        var proxyEndpoint = ResolveProxyEndpoint(netType);

        return new RGBConfiguration(network, electrumUrl, rgbDataDir, proxyEndpoint);
    }

    private static string ResolveElectrumUrl(ChainName net)
    {
        var env = Environment.GetEnvironmentVariable("RGB_ELECTRUM_URL");
        if (!string.IsNullOrEmpty(env))
            return env;

        return net.ToString() switch
        {
            "Main" => "ssl://electrum.blockstream.info:60002",
            "TestNet" => "ssl://electrum.blockstream.info:60002",
            "Regtest" => IsRunningInDocker() ? "tcp://electrs:50001" : "tcp://127.0.0.1:50001",
            _ => "tcp://127.0.0.1:50001"
        };
    }

    private static string ResolveRgbDataDir(string btcPayDataDir)
    {
        var env = Environment.GetEnvironmentVariable("RGB_DATA_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;

        return Path.Combine(btcPayDataDir, "rgb-wallets");
    }

    private static string ResolveProxyEndpoint(ChainName net)
    {
        var env = Environment.GetEnvironmentVariable("RGB_PROXY_ENDPOINT");
        if (!string.IsNullOrEmpty(env))
            return env;

        return net.ToString() switch
        {
            "Main" => "rpc://proxy.iriswallet.com/0.2/json-rpc",
            "TestNet" => "rpc://proxy.iriswallet.com/0.2/json-rpc",
            _ => "rpc://proxy.iriswallet.com/0.2/json-rpc"
        };
    }

    private static bool IsRunningInDocker() =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
        || File.Exists("/.dockerenv");
}
