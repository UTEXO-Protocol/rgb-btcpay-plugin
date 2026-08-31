using System.Text.Json.Serialization;
using BTCPayServer.Plugins.RgbUtexo.Services;

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
            ElectrumUrl = "https://esplora-api.utexo.com",
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
        network.Equals("regtest", StringComparison.OrdinalIgnoreCase);
}

public class RGBConfiguration
{
    internal const string DefaultRgbBaseDir = "/data";

    string _rgbBaseDir = DefaultRgbBaseDir;

    internal bool RgbBaseDirExplicitlySet { get; private set; }

    [JsonPropertyName("rgb_base_dir")]
    public string RgbBaseDir
    {
        get => _rgbBaseDir;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _rgbBaseDir = value;
            RgbBaseDirExplicitlySet = true;
        }
    }

    [JsonPropertyName("max_allocations_per_utxo")]
    public int MaxAllocationsPerUtxo { get; set; } = 10;

    [JsonPropertyName("allow_private_transport_endpoints")]
    public bool AllowPrivateTransportEndpoints { get; set; }

    [JsonPropertyName("restore_timeout_seconds")]
    public int RestoreTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("restore_disk_cap_bytes")]
    public long RestoreDiskCapBytes { get; set; } = 536_870_912;

    [JsonPropertyName("restore_upload_max_bytes")]
    public long RestoreUploadMaxBytes { get; set; } = RestoreUploadBoundMinBytes;

    [JsonPropertyName("restore_upload_max_concurrent_uploads")]
    public int RestoreUploadMaxConcurrentUploads { get; set; } = RestoreUploadMaxConcurrentUploadsDefault;

    [JsonPropertyName("restore_ram_cap_bytes")]
    public long RestoreRamCapBytes { get; set; } = RestoreRamMinBytes;

    [JsonPropertyName("restore_cpu_limit_seconds")]
    public int RestoreCpuLimitSeconds { get; set; } = 30;

    [JsonPropertyName("restore_poll_ms")]
    public int RestorePollMs { get; set; } = 500;

    [JsonPropertyName("restore_reap_grace_seconds")]
    public int RestoreReapGraceSeconds { get; set; } = 5;

    // See RgbBackupScryptGuard: the KDF cost lives in the uploaded file, so it is bounded before any
    // child is spawned. Configurable because this bound can only false-REJECT, and a legitimate
    // backup written by a future rgb-lib with a higher log_n must remain restorable.
    [JsonPropertyName("restore_scrypt_memory_cap_bytes")]
    public long RestoreScryptMemoryCapBytes { get; set; } = RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes;

    // Bounds the WATCHDOG's own work, not the child's. Each poll walked the whole staging tree with a
    // stat per file; a hostile inner archive that inflates to very many small files kept the byte
    // total under RestoreDiskCapBytes while making that walk slow, which both burned parent CPU and
    // delayed the kill. Exceeding this count is itself a kill reason, because an honest rgb-lib
    // wallet directory is a handful of files.
    [JsonPropertyName("restore_max_staging_entries")]
    public int RestoreMaxStagingEntries { get; set; } = 20_000;

    // Applied after every native restore attempt. Exit status and wall-clock duration cannot identify
    // cheap work: accepted log_n=18 parameters plus a wrong password measured 290 MiB peak RSS and
    // completed in 399 ms, so a threshold left meaningful resource use immediately retryable forever.
    [JsonPropertyName("restore_kill_cooldown_seconds")]
    public int RestoreKillCooldownSeconds { get; set; } = 60;

    [JsonPropertyName("backup_cooldown_seconds")]
    public int BackupCooldownSeconds { get; set; } = 60;

    [JsonPropertyName("backup_start_wait_timeout_seconds")]
    public int BackupStartWaitTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("backup_stuck_threshold_seconds")]
    public int BackupStuckThresholdSeconds { get; set; } = 300;

    [JsonPropertyName("native_send_timeout_seconds")]
    public int NativeSendTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("native_send_ram_cap_bytes")]
    public long NativeSendRamCapBytes { get; set; } = 536_870_912;

    [JsonPropertyName("native_send_cpu_limit_seconds")]
    public int NativeSendCpuLimitSeconds { get; set; } = 30;

    [JsonPropertyName("native_send_poll_ms")]
    public int NativeSendPollMs { get; set; } = 100;

    [JsonPropertyName("native_send_reap_grace_seconds")]
    public int NativeSendReapGraceSeconds { get; set; } = 5;

    internal const long NativeSendRamMinBytes = 64L * 1024 * 1024;
    internal const long NativeSendRamMaxBytes = 2L * 1024 * 1024 * 1024;

    internal const int NativeSendSecondsMin = 1;
    internal const int NativeSendSecondsMax = 600;

    internal const int RestoreSecondsMin = 1;
    internal const int RestoreSecondsMax = 3_600;

    // Floors on the restore side are the false-REJECT direction, and restore is the recovery path:
    // a bound below what an honest backup demonstrably needs refuses every restore, which is fund
    // loss, while a bound above the shipped default only widens a DoS window the other caps still
    // close. RgbBackupValidator admits up to MaxTotalUncompressedBytes (50 MiB) of OUTER-archive
    // content, and that content is the zstd-compressed, encrypted wallet zip, so the wallet directory
    // rgb-lib expands into the staging dir is always larger by the compression ratio: this floor is the
    // NECESSARY minimum for a staging byte cap and never a sufficient one; and an rgb-lib wallet
    // directory is a few dozen files, so 1 000 entries is far above legitimate while still letting
    // an operator tighten the 20 000 default.
    internal const long RestoreDiskCapMinBytes = RgbBackupValidator.MaxTotalUncompressedBytes;
    internal const long RestoreDiskCapMaxBytes = 4L * 1024 * 1024 * 1024;

    internal const long RestoreUploadZipFramingHeadroomBytes = 1024L * 1024;
    internal const long RestoreUploadBoundMinBytes =
        RgbBackupValidator.MaxTotalUncompressedBytes + RestoreUploadZipFramingHeadroomBytes;
    internal const long RestoreUploadBoundMaxBytes = 100L * 1024 * 1024;
    internal const long MultipartFormBodyLengthCeilingBytes = 128L * 1024 * 1024;

    internal const int RestoreUploadMaxConcurrentUploadsDefault = 4;
    internal const int RestoreUploadMaxConcurrentUploadsMin = 1;
    internal const int RestoreUploadMaxConcurrentUploadsMax = 32;

    internal const long RestoreHelperResidentSetOutsideTheScryptArenaMeasuredBytes = 34L * 1024 * 1024;
    internal const long RestoreRamHeadroomTheScryptArenaCeilingIsNotMeasuredWithBytes =
        RestoreHelperResidentSetOutsideTheScryptArenaMeasuredBytes
        + RgbBackupValidator.MaxTotalUncompressedBytes;
    internal const long RestoreRamMinBytes =
        RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes
        + RestoreRamHeadroomTheScryptArenaCeilingIsNotMeasuredWithBytes;
    internal const long RestoreRamMaxBytes = 4L * 1024 * 1024 * 1024;

    internal const int RestoreMinStagingEntries = 1_000;

    internal const int RestorePollMsMin = 10;
    internal const int RestorePollMsMax = 1_000;

    internal const int RestoreReapGraceSecondsMin = 1;
    internal const int RestoreReapGraceSecondsMax = 30;

    internal const int BackupCooldownSecondsMin = 0;
    internal const int BackupCooldownSecondsMax = 3_600;
    internal const int BackupStartWaitTimeoutSecondsMin = 1;
    internal const int BackupStartWaitTimeoutSecondsMax = 3_600;
    internal const int BackupStuckThresholdSecondsMin = 1;
    internal const int BackupStuckThresholdSecondsMax = 86_400;

    // MUST exceed RGBInvoiceListener.UtxoCheckMinutes (10). At 10 the cooldown was inert: the sweep stamps
    // its own clock AFTER the sweep returns, so sweep N+1 begins later than end_N + 10 min, while a wallet
    // that settled at T <= end_N became eligible at T + 10 min — always already past. SkipCooldown could
    // never fire on a settle path, so audit clause 3 shipped its cap and not its rate limit. Because
    // EvaluateReplenishDemand requests exactly the shortfall — create_utxos_begin is called with
    // up_to = false, so the request is a count of NEW outputs, not a target total — one successful
    // creation reaches the goal unless the cap itself is the binding constraint, so a longer gap costs
    // almost no liveness. Pinned by CooldownMustOutlastTheSweepPeriod.
    const int DefaultAutoUtxoCooldownMinutes = 30;
    const int DefaultAutoUtxoMaxBackoffMinutes = 160;

    int _autoUtxoCooldownMinutes = DefaultAutoUtxoCooldownMinutes;
    int _autoUtxoMaxBackoffMinutes = DefaultAutoUtxoMaxBackoffMinutes;

    const int DefaultCheckoutInvoiceHotScanWindowHours = 72;

    internal const int MinCheckoutInvoiceHotScanWindowHours = 48;
    internal const int MaxCheckoutInvoiceHotScanWindowHours = 24 * 365 * 10;
    internal const int CheckoutInvoiceMonitoringSafetyMarginHours = 48;

    int _checkoutInvoiceHotScanWindowHours = DefaultCheckoutInvoiceHotScanWindowHours;

    [JsonPropertyName("checkout_invoice_hot_scan_window_hours")]
    public int CheckoutInvoiceHotScanWindowHours
    {
        get => _checkoutInvoiceHotScanWindowHours;
        set => _checkoutInvoiceHotScanWindowHours = Math.Clamp(
            value > 0 ? value : DefaultCheckoutInvoiceHotScanWindowHours,
            MinCheckoutInvoiceHotScanWindowHours,
            MaxCheckoutInvoiceHotScanWindowHours);
    }

    const int DefaultMaxAutoColorableUtxos = 50;

    int _maxAutoColorableUtxos = DefaultMaxAutoColorableUtxos;

    [JsonPropertyName("max_auto_colorable_utxos")]
    public int MaxAutoColorableUtxos
    {
        get => _maxAutoColorableUtxos;
        set => _maxAutoColorableUtxos = Math.Max(0, value);
    }

    const int DefaultMaxManualColorableUtxos = 250;

    int _maxManualColorableUtxos = DefaultMaxManualColorableUtxos;

    [JsonPropertyName("max_manual_colorable_utxos")]
    public int MaxManualColorableUtxos
    {
        get => _maxManualColorableUtxos;
        set => _maxManualColorableUtxos =
            value > 0 ? value : DefaultMaxManualColorableUtxos;
    }

    // WHY the clamping lives in the accessors: the listener's tracker construction is pinned to the literal
    // expression TimeSpan.FromMinutes(_cfg.AutoUtxoCooldownMinutes), so the value has to be safe by the time
    // it is read. A non-positive cooldown would mean "always eligible", i.e. more automatic signing.
    //
    // WHY the floor is TWICE the sweep period rather than one minute more: the gate compares against an
    // instant stamped mid-sweep, so the usable margin is the cooldown minus the sweep period minus however
    // long the rest of the sweep takes. `UtxoCheckMinutes + 1` leaves one minute of that, which a
    // multi-wallet sweep or a single sign-and-broadcast eats, reproducing the inert cooldown for anyone who
    // sets 11. A sweep lasting longer than UtxoCheckMinutes would already saturate the sweep timer itself,
    // so one whole period is the natural margin. A cooldown at or below the sweep period can never fire. The
    // listener stamps _lastUtxoCheck AFTER the sweep, so the next sweep starts later than end + period, by
    // which time a wallet that settled during the sweep is already eligible. Raising the DEFAULT to 30 fixed
    // the shipped case and left the trap armed for anyone who sets the knob — and `10` is exactly what an
    // operator upgrading from an earlier build would pin to keep the old cadence. Clamping up is the
    // false-REJECT direction (a longer wait, never a shorter one), which is the permitted one.
    [JsonPropertyName("auto_utxo_cooldown_minutes")]
    public int AutoUtxoCooldownMinutes
    {
        get => _autoUtxoCooldownMinutes;
        set => _autoUtxoCooldownMinutes = Math.Max(
            value > 0 ? value : DefaultAutoUtxoCooldownMinutes,
            RGBInvoiceListener.UtxoCheckMinutes * 2);
    }

    [JsonPropertyName("auto_utxo_max_backoff_minutes")]
    public int AutoUtxoMaxBackoffMinutes
    {
        get => Math.Max(_autoUtxoMaxBackoffMinutes, AutoUtxoCooldownMinutes);
        set => _autoUtxoMaxBackoffMinutes = value > 0 ? value : DefaultAutoUtxoMaxBackoffMinutes;
    }

    // Clamped HERE, not only in ApplyEnvironmentOverrides: rgb.json reaches these properties without
    // passing through that method, so "restore_cpu_limit_seconds": 0 used to arrive at prlimit --cpu=0
    // and refuse every restore. ToNativeSendLimits has always clamped at this read site; the restore
    // twin clamped nothing, which made the file path bypass exactly the bound the env path enforces.
    public RestoreLimits ToRestoreLimits() => new(
        Timeout: TimeSpan.FromSeconds(
            Math.Clamp(RestoreTimeoutSeconds, RestoreSecondsMin, RestoreSecondsMax)),
        DiskCapBytes: Math.Clamp(RestoreDiskCapBytes, RestoreDiskCapMinBytes, RestoreDiskCapMaxBytes),
        RamCapBytes: Math.Clamp(RestoreRamCapBytes, RestoreRamMinBytes, RestoreRamMaxBytes),
        CpuLimit: TimeSpan.FromSeconds(
            Math.Clamp(RestoreCpuLimitSeconds, RestoreSecondsMin, RestoreSecondsMax)),
        Poll: TimeSpan.FromMilliseconds(
            Math.Clamp(RestorePollMs, RestorePollMsMin, RestorePollMsMax)),
        ReapGrace: TimeSpan.FromSeconds(
            Math.Clamp(RestoreReapGraceSeconds, RestoreReapGraceSecondsMin, RestoreReapGraceSecondsMax)),
        MaxStagingEntries: Math.Max(RestoreMaxStagingEntries, RestoreMinStagingEntries));

    public NativeSendLimits ToNativeSendLimits() => new(
        Timeout: TimeSpan.FromSeconds(
            Math.Clamp(NativeSendTimeoutSeconds, NativeSendSecondsMin, NativeSendSecondsMax)),
        RamCapBytes: Math.Clamp(NativeSendRamCapBytes,
            NativeSendRamMinBytes, NativeSendRamMaxBytes),
        CpuLimit: TimeSpan.FromSeconds(
            Math.Clamp(NativeSendCpuLimitSeconds, NativeSendSecondsMin, NativeSendSecondsMax)),
        Poll: TimeSpan.FromMilliseconds(Math.Clamp(NativeSendPollMs, 10, 1_000)),
        ReapGrace: TimeSpan.FromSeconds(Math.Clamp(NativeSendReapGraceSeconds, 1, 30)));

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
