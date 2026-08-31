using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Services.Rates;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Configuration;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo;

public class RGBPlugin : BaseBTCPayServerPlugin
{
    internal const string PluginNavKey = nameof(RGBPlugin) + "Nav";
    internal static readonly PaymentMethodId RGBPaymentMethodId = new("RGB");

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies =>
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    ];

    public override void Execute(IServiceCollection services)
    {
        var ctx = (PluginServiceCollection)services;

        // Before LoadConfiguration, not after: that method's IConfiguration lookup and file IO can
        // throw, and a probe placed behind it would be skipped in exactly the degraded startups
        // where an operator most needs to be told the pre-sign gate cannot load. Log-only — sends
        // already fail closed, and hard-failing here would auto-disable the plugin fleet-wide.
        RgbNativeSelfCheck.VerifyOrLog(ctx.BootstrapServices);

        var config = LoadConfiguration(ctx);
        if (config == null) return;

        services.AddSingleton(config);
        services.AddSingleton<RGBPluginDbContextFactory>();
        services.AddDbContext<RGBPluginDbContext>((sp, opts) =>
        {
            sp.GetRequiredService<RGBPluginDbContextFactory>().ConfigureBuilder(opts);
        });
        services.AddStartupTask<RGBPluginMigrationRunner>();

        services.AddSingleton<CurrencyDataProvider, RgbCurrencyDataProvider>();
        services.AddSingleton<IRgbRateSource, RgbRateSource>();
        services.AddSingleton<IRgbPricingCodeCollisionGuard, RgbPricingCodeCollisionGuard>();
        services.AddSingleton<IRgbLibService, RgbLibService>();
        services.AddSingleton<MnemonicProtectionService>();
        services.AddSingleton<RgbWalletSignerProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<RgbWalletSignerProvider>());
        services.AddSingleton<IRestoreProcessRunner, RestoreProcessRunner>();
        services.AddSingleton<RestoreExecutor>();
        services.AddSingleton<INativeSendProcessRunner, NativeSendProcessRunner>();
        services.AddSingleton<RGBWalletService>();
        services.AddSingleton<IRGBWalletService>(sp => sp.GetRequiredService<RGBWalletService>());
        services.AddSingleton<RGBPaymentMethodHandler>();
        services.AddSingleton<IPaymentMethodHandler>(sp => sp.GetRequiredService<RGBPaymentMethodHandler>());

        services.AddSingleton<RGBCheckoutModelExtension>();
        services.AddSingleton<ICheckoutModelExtension>(sp => sp.GetRequiredService<RGBCheckoutModelExtension>());

        services.AddSingleton<RgbAutoReplenishmentAuthorizationStore>();
        services.AddSingleton<RgbReplenishmentNoticeService>();
        services.AddSingleton<IRgbNoticeRaiser>(sp => sp.GetRequiredService<RgbReplenishmentNoticeService>());
        services.AddSingleton<INotificationHandler, RgbReplenishmentBlockedNotification.Handler>();
        services.AddSingleton<RGBInvoiceListener>();
        services.AddHostedService(sp => sp.GetRequiredService<RGBInvoiceListener>());
        services.AddSingleton<INotificationHandler, RgbSeedViewedNotification.Handler>();
        services.AddHostedService<RgbSeedViewedEventSubscriber>();
        services.AddHostedService<RgbVanillaReservationStartupProbe>();
        services.AddUIExtension("checkout-end", "RGB/RGBMethodCheckout");
        services.AddUIExtension("checkout-end", "/Views/RGB/RGBCheckoutStyles.cshtml");
        services.AddUIExtension("store-wallets-nav", "/Views/RGB/RGBWalletNav.cshtml");
        services.AddDefaultPrettyName(RGBPaymentMethodId, "RGB");
    }

    private static RGBConfiguration? LoadConfiguration(PluginServiceCollection ctx)
    {
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
                    ApplyResolvedRgbBaseDir(fromFile, dataDir, log:
                        ctx.BootstrapServices.GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>());
                    ApplyEnvironmentOverrides(fromFile);
                    return fromFile;
                }
            }
            catch (JsonException ex)
            {
                var logger = ctx.BootstrapServices.GetService<ILoggerFactory>()?.CreateLogger<RGBPlugin>();
                logger?.LogWarning(ex, "Failed to parse rgb.json config at {Path}, using defaults", configPath);
            }
        }

        var rgbBaseDir = ResolveRgbBaseDir(dataDir);
        var cfg = new RGBConfiguration(rgbBaseDir);
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
            cfg.AllowPrivateTransportEndpoints = true;
        ApplyEnvironmentOverrides(cfg);
        return cfg;
    }

    /// <summary>
    /// Applies environment-variable overrides for the colorable-UTXO, native-send and restore knobs.
    ///
    /// WHY these exist at all: rgb.json is the only other delivery mechanism, and writing that file is
    /// hazardous — it replaces the whole configuration object, so a file that omits rgb_base_dir can
    /// leave every wallet path under the literal default "/data". An operator who wants to bound or
    /// disable unattended signing, or to raise a deadline, must not be forced to take that risk to do
    /// it, so every control here is settable without touching the file at all.
    ///
    /// An unparseable value is ignored rather than treated as zero: zero is a meaningful setting for
    /// MaxAutoColorableUtxos (it disables automatic creation) and must never be reached by accident.
    /// </summary>
    internal static void ApplyEnvironmentOverrides(RGBConfiguration cfg, Func<string, string?>? readEnv = null)
    {
        var read = readEnv ?? Environment.GetEnvironmentVariable;

        if (int.TryParse(read("RGB_MAX_AUTO_COLORABLE_UTXOS"), out var cap))
            cfg.MaxAutoColorableUtxos = cap;

        if (int.TryParse(read("RGB_MAX_MANUAL_COLORABLE_UTXOS"), out var manualCeiling))
            cfg.MaxManualColorableUtxos = manualCeiling;

        if (int.TryParse(read("RGB_AUTO_UTXO_COOLDOWN_MINUTES"), out var cooldown))
            cfg.AutoUtxoCooldownMinutes = cooldown;

        if (int.TryParse(read("RGB_AUTO_UTXO_MAX_BACKOFF_MINUTES"), out var backoff))
            cfg.AutoUtxoMaxBackoffMinutes = backoff;

        // The scrypt ceiling in particular MUST be reachable without editing rgb.json: it is the one
        // restore bound that can refuse a genuine backup outright (see RgbBackupScryptGuard), so the
        // recovery path for a false reject has to be as cheap as possible.
        if (long.TryParse(read("RGB_RESTORE_SCRYPT_MEMORY_CAP_BYTES"), out var scryptCap) && scryptCap > 0)
            cfg.RestoreScryptMemoryCapBytes = scryptCap;

        if (int.TryParse(read("RGB_RESTORE_MAX_STAGING_ENTRIES"), out var stagingEntries) && stagingEntries > 0)
            cfg.RestoreMaxStagingEntries = stagingEntries;

        if (int.TryParse(read("RGB_RESTORE_KILL_COOLDOWN_SECONDS"), out var killCooldown) && killCooldown >= 0)
            cfg.RestoreKillCooldownSeconds = killCooldown;

        if (int.TryParse(read("RGB_NATIVE_SEND_TIMEOUT_SECONDS"), out var sendTimeout) && sendTimeout > 0)
            cfg.NativeSendTimeoutSeconds = Math.Clamp(sendTimeout,
                RGBConfiguration.NativeSendSecondsMin, RGBConfiguration.NativeSendSecondsMax);

        if (int.TryParse(read("RGB_NATIVE_SEND_CPU_LIMIT_SECONDS"), out var sendCpu) && sendCpu > 0)
            cfg.NativeSendCpuLimitSeconds = Math.Clamp(sendCpu,
                RGBConfiguration.NativeSendSecondsMin, RGBConfiguration.NativeSendSecondsMax);

        if (long.TryParse(read("RGB_NATIVE_SEND_RAM_CAP_BYTES"), out var sendRam) && sendRam > 0)
            cfg.NativeSendRamCapBytes = Math.Clamp(sendRam,
                RGBConfiguration.NativeSendRamMinBytes, RGBConfiguration.NativeSendRamMaxBytes);

        if (int.TryParse(read("RGB_RESTORE_TIMEOUT_SECONDS"), out var restoreTimeout) && restoreTimeout > 0)
            cfg.RestoreTimeoutSeconds = Math.Clamp(restoreTimeout,
                RGBConfiguration.RestoreSecondsMin, RGBConfiguration.RestoreSecondsMax);

        if (int.TryParse(read("RGB_RESTORE_CPU_LIMIT_SECONDS"), out var restoreCpu) && restoreCpu > 0)
            cfg.RestoreCpuLimitSeconds = Math.Clamp(restoreCpu,
                RGBConfiguration.RestoreSecondsMin, RGBConfiguration.RestoreSecondsMax);

        // The restore child now enforces this budget on itself as a hard address-space rlimit, so it is
        // the one restore bound that can newly refuse a genuine backup mid-flight. Reaching it without
        // editing rgb.json is the same argument the scrypt ceiling above already won.
        if (long.TryParse(read("RGB_RESTORE_RAM_CAP_BYTES"), out var restoreRam) && restoreRam > 0)
            cfg.RestoreRamCapBytes = Math.Clamp(restoreRam,
                RGBConfiguration.RestoreRamMinBytes, RGBConfiguration.RestoreRamMaxBytes);

        if (long.TryParse(read("RGB_RESTORE_DISK_CAP_BYTES"),
                out var stagingCapForTheDecompressedWalletDirectoryEveryOtherGateMeasuresCompressed)
            && stagingCapForTheDecompressedWalletDirectoryEveryOtherGateMeasuresCompressed > 0)
            cfg.RestoreDiskCapBytes = Math.Clamp(
                stagingCapForTheDecompressedWalletDirectoryEveryOtherGateMeasuresCompressed,
                RGBConfiguration.RestoreDiskCapMinBytes, RGBConfiguration.RestoreDiskCapMaxBytes);

        if (long.TryParse(read(RgbRestoreUploadBound.EnvironmentVariableName), out var uploadBound)
            && uploadBound > 0)
            cfg.RestoreUploadMaxBytes = Math.Clamp(uploadBound,
                RGBConfiguration.RestoreUploadBoundMinBytes, RGBConfiguration.RestoreUploadBoundMaxBytes);

        if (int.TryParse(read(RgbRestoreUploadConcurrencyGate.EnvironmentVariableName), out var uploadConcurrency)
            && uploadConcurrency > 0)
            cfg.RestoreUploadMaxConcurrentUploads = Math.Clamp(uploadConcurrency,
                RGBConfiguration.RestoreUploadMaxConcurrentUploadsMin,
                RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax);
    }

    internal static void ApplyResolvedRgbBaseDir(
        RGBConfiguration cfg,
        string btcPayDataDir,
        Func<string, bool>? directoryExists = null,
        ILogger? log = null)
    {
        if (cfg.RgbBaseDirExplicitlySet) return;

        var candidate = ResolveRgbBaseDir(btcPayDataDir);
        if (string.Equals(candidate, cfg.RgbBaseDir, StringComparison.Ordinal)) return;

        var exists = directoryExists ?? Directory.Exists;
        if (exists(RGBConfiguration.DefaultRgbBaseDir))
        {
            log?.LogWarning(
                "rgb.json does not set rgb_base_dir, so RGB wallet data will be read from the built-in default {DefaultBaseDir}, which exists on this host. The directory this deployment would otherwise have used, {Candidate}, was neither substituted nor migrated to: wallet directories are never moved between parents, and an RGB stock opened from the wrong parent is not recoverable from the chain. Confirm which of the two actually holds this deployment's wallets, then set rgb_base_dir explicitly in rgb.json (or set RGB_BASE_DIR and remove rgb.json) to pin it",
                RGBConfiguration.DefaultRgbBaseDir, candidate);
            return;
        }

        cfg.RgbBaseDir = candidate;
    }

    private static string ResolveRgbBaseDir(string btcPayDataDir)
    {
        var env = Environment.GetEnvironmentVariable("RGB_BASE_DIR");
        if (!string.IsNullOrEmpty(env))
            return env;

        return Directory.GetParent(btcPayDataDir)?.FullName ?? btcPayDataDir;
    }
}
