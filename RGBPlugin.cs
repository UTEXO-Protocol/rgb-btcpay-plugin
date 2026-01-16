using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RGB.Data;
using BTCPayServer.Plugins.RGB.PaymentHandler;
using BTCPayServer.Plugins.RGB.Services;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using System.Text.Json;

namespace BTCPayServer.Plugins.RGB;

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

        services.AddNodeJS();
        services.Configure<NodeJSProcessOptions>(opts =>
        {
            var pluginDir = Path.GetDirectoryName(typeof(RGBPlugin).Assembly.Location);
            opts.ProjectPath = Path.Combine(pluginDir ?? ".", "Scripts");
        });
        
        ConfigureSdkPath();

        services.AddHttpClient("RgbNode");
        services.AddSingleton<RgbNodeClient>();
        services.AddSingleton<RgbSdkService>();
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
        
        var nodeUrl = ResolveNodeUrl(netType);
        if (nodeUrl == null) return null;
        
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
                    if (!IsValidRgbNodeUrl(fromFile.RgbNodeUrl))
                        throw new InvalidOperationException($"Invalid rgb_node_url in {configPath}");
                    return fromFile;
                }
            }
            catch (JsonException)
            {
            }
        }

        return new RGBConfiguration(nodeUrl, network);
    }

    private static string? ResolveNodeUrl(ChainName net)
    {
        var env = Environment.GetEnvironmentVariable("RGB_NODE_URL");
        if (!string.IsNullOrEmpty(env))
        {
            if (!IsValidRgbNodeUrl(env))
                throw new InvalidOperationException($"Invalid RGB_NODE_URL: {env}. Must be a valid HTTP/HTTPS URL.");
            return env;
        }

        return net.ToString() switch
        {
            "Main" => "https://rgb-node.thunderstack.org",
            "TestNet" => "https://rgb-node.test.thunderstack.org",
            "Regtest" => IsRunningInDocker() ? "http://host.docker.internal:8000" : "http://127.0.0.1:8000",
            "Signet" => null,
            _ => null
        };
    }
    
    private static bool IsValidRgbNodeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
            
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
            
        if (uri.Scheme != "http" && uri.Scheme != "https")
            return false;
        
        if (url.Contains("..") || url.Contains("<") || url.Contains(">"))
            return false;
            
        return true;
    }

    private static bool IsRunningInDocker() =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" 
        || File.Exists("/.dockerenv");

    private static void ConfigureSdkPath()
    {
        var dir = Path.GetDirectoryName(typeof(RGBPlugin).Assembly.Location) ?? ".";
        var sdk = Path.Combine(dir, "Scripts", "rgb-sdk", "index.cjs");
        if (File.Exists(sdk))
            Environment.SetEnvironmentVariable("RGB_SDK_PATH", Path.GetFullPath(sdk));
    }
}
