using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbLibService : IRgbLibService
{
    internal const int MaxTransferListRows = 1_000;
    static readonly ConcurrentDictionary<Lazy<RgbLibWalletHandle>, byte>
        PendingConstructionDisposals = new(ReferenceEqualityComparer.Instance);
    readonly RGBConfiguration _config;
    readonly RGBPluginDbContextFactory _db;
    readonly ILogger<RgbLibService> _log;
    readonly ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> _wallets = new();
    
    readonly Type _nativeMethodsType;
    readonly Type _cResultStringType;
    readonly FieldInfo _walletField;
    readonly FieldInfo _onlineJsonField;
    readonly FieldInfo _resultField;
    readonly FieldInfo _innerField;
    readonly Action<IntPtr> _stringFree;
    readonly Func<IntPtr, string?> _marshal;

    readonly MethodInfo _getAddressMethod;
    readonly MethodInfo _issueAssetNiaMethod;
    readonly MethodInfo _getBtcBalanceMethod;
    readonly MethodInfo _listAssetsMethod;
    readonly MethodInfo _blindReceiveMethod;
    readonly MethodInfo _listUnspentsMethod;
    readonly MethodInfo _createUtxosBeginMethod;
    readonly MethodInfo _createUtxosEndMethod;
    readonly MethodInfo _refreshMethod;
    readonly MethodInfo _listTransactionsMethod;
    readonly MethodInfo _sendBeginMethod;
    readonly MethodInfo _sendEndMethod;
    readonly MethodInfo _goOnlineMethod;
    readonly MethodInfo _generateKeysMethod;
    readonly MethodInfo _restoreKeysMethod;
    readonly MethodInfo _backupMethod;

    bool _disposed;

    public RgbLibService(
        RGBConfiguration config,
        RGBPluginDbContextFactory db,
        ILogger<RgbLibService> log)
        : this(config, db, log,
            typeof(RgbLibWallet).Assembly.GetType("RgbLib.CResultString")!,
            rgblib_string_free,
            Marshal.PtrToStringUTF8)
    {
    }

    internal RgbLibService(
        RGBConfiguration config,
        RGBPluginDbContextFactory db,
        ILogger<RgbLibService> log,
        Type cResultStringType,
        Action<IntPtr> stringFree,
        Func<IntPtr, string?> marshal)
    {
        _config = config;
        _db = db;
        _log = log;
        _stringFree = stringFree;
        _marshal = marshal;

        var assembly = typeof(RgbLibWallet).Assembly;
        _nativeMethodsType = assembly.GetType("RgbLib.NativeMethods")!;
        _cResultStringType = cResultStringType;

        _walletField = typeof(RgbLibWallet).GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onlineJsonField = typeof(RgbLibWallet).GetField("_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _resultField = _cResultStringType.GetField("result")!;
        _innerField = _cResultStringType.GetField("inner")!;
        
        // RgbLib's own typed wrappers marshal a CResultString and never free it — the package binds no
        // string-free at all — so every typed call leaked its payload. These three are the reachable
        // ones, and get_btc_balance runs once per wallet per listener sweep.
        _getAddressMethod = _nativeMethodsType.GetMethod("rgblib_get_address")!;
        _issueAssetNiaMethod = _nativeMethodsType.GetMethod("rgblib_issue_asset_nia")!;
        _getBtcBalanceMethod = _nativeMethodsType.GetMethod("rgblib_get_btc_balance")!;
        _listAssetsMethod = _nativeMethodsType.GetMethod("rgblib_list_assets")!;
        _blindReceiveMethod = _nativeMethodsType.GetMethod("rgblib_blind_receive")!;
        _listUnspentsMethod = _nativeMethodsType.GetMethod("rgblib_list_unspents")!;
        _createUtxosBeginMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_begin")!;
        _createUtxosEndMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_end")!;
        _refreshMethod = _nativeMethodsType.GetMethod("rgblib_refresh")!;
        _listTransactionsMethod = _nativeMethodsType.GetMethod("rgblib_list_transactions")!;
        _sendBeginMethod = _nativeMethodsType.GetMethod("rgblib_send_begin")!;
        _sendEndMethod = _nativeMethodsType.GetMethod("rgblib_send_end")!;
        _goOnlineMethod = _nativeMethodsType.GetMethod("rgblib_go_online")!;
        _generateKeysMethod = _nativeMethodsType.GetMethod("rgblib_generate_keys")!;
        _restoreKeysMethod = _nativeMethodsType.GetMethod("rgblib_restore_keys")!;
        _backupMethod = _nativeMethodsType.GetMethod("rgblib_backup")!;
    }

    public async Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");

        var walletDir = Path.Combine(_config.GetWalletDataDir(walletId, dbWallet.Network),
            dbWallet.MasterFingerprint);
        // Returning an already-built handle touches no native state. Its ExecuteAsync path owns the
        // native-access mutex, so taking that mutex here as well would turn one long healthy call into
        // a spurious construction timeout for every concurrent request on the same wallet.
        if (_wallets.TryGetValue(walletId, out var cachedWallet) && cachedWallet.IsValueCreated)
            return cachedWallet.Value;
        return RgbNativeSendLease.WithProcessGate(walletDir, () =>
        {
            // Recovery is admitted by execution-context lease ownership, never by a public bypass.
            using var walletAccess = RgbNativeSendLease.AcquireWalletConstructionAccess(walletDir);
            if (RgbNativeSendLease.Exists(walletDir)
                && !RgbNativeSendLease.IsOwnedByCurrentContext(walletDir))
                throw new RgbWalletQuarantinedException(
                    "native send helper may still own this wallet — refusing concurrent rgb-lib access");

            var lazyWallet = _wallets.GetOrAdd(walletId, _ =>
                new Lazy<RgbLibWalletHandle>(() => CreateWalletInternal(
                    walletId,
                    dbWallet.XpubVanilla,
                    dbWallet.XpubColored,
                    dbWallet.MasterFingerprint,
                    dbWallet.Network,
                    RGBWalletService.ResolveAllocationsPerUtxo(dbWallet.MaxAllocationsPerUtxo))));

            return lazyWallet.Value;
        });
    }

    RgbLibWalletHandle CreateWalletInternal(string walletId, string xpubVanilla, string xpubColored, string masterFingerprint, string walletNetwork, int maxAllocationsPerUtxo)
    {
        _log.LogInformation("Lazy loading wallet {WalletId} on network {Network} with max_allocations={MaxAlloc}", walletId, walletNetwork, maxAllocationsPerUtxo);

        var dataDir = _config.GetWalletDataDir(walletId, walletNetwork);
        Directory.CreateDirectory(dataDir);
        
        var networkSettings = RGBConfiguration.GetNetworkSettings(walletNetwork);

        var walletConfig = new Dictionary<string, object?>
        {
            ["data_dir"] = dataDir,
            ["bitcoin_network"] = NetworkHelper.MapNetworkToRgbLibFormat(walletNetwork),
            ["database_type"] = "Sqlite",
            ["max_allocations_per_utxo"] = maxAllocationsPerUtxo,
            ["supported_schemas"] = RgbAssetSchemaSupport.TheOnlySchemasThisPluginCanEnumerateAndSpend
        };

        var keysConfig = new Dictionary<string, object?>
        {
            ["account_xpub_vanilla"] = xpubVanilla,
            ["account_xpub_colored"] = xpubColored,
            ["master_fingerprint"] = masterFingerprint,
            ["vanilla_keychain"] = (int?)null,
            ["mnemonic"] = (string?)null
        };

        var configJson = JsonSerializer.Serialize(walletConfig);
        var keysJson = JsonSerializer.Serialize(keysConfig);
        try
        {
            var wallet = new RgbLibWallet(configJson, keysJson);
            return CreateHandleOrDisposeWallet(
                wallet,
                w => GoOnline(w, networkSettings.ElectrumUrl, true),
                w =>
                {
                    _log.LogInformation("Wallet {WalletId} connected to {Electrum}", walletId, networkSettings.ElectrumUrl);
                    return new RgbLibWalletHandle(w, walletId,
                        Path.Combine(dataDir, masterFingerprint), _log);
                },
                w => w.Dispose(),
                disposeError => _log.LogError(disposeError,
                    "Failed to dispose the rgb-lib wallet for {WalletId} after a failed bring-online; its rgb_runtime.lock may still be on disk",
                    walletId));
        }
        catch (Exception ex)
        {
            var detailWithKeyMaterialRemoved = RgbNativeMessageSanitizer.Sanitize(ex.Message);
            _log.LogError(
                "Failed to bring up the rgb-lib wallet. walletId={WalletId} network={Network} failure={FailureType} detail={KeyMaterialSanitizedDetail}",
                walletId, walletNetwork, ex.GetType().Name, detailWithKeyMaterialRemoved);
            throw new RgbWalletConstructionException(WalletBringUpFailureForTheOperator(
                walletId, walletNetwork, ex, detailWithKeyMaterialRemoved));
        }
    }

    internal void GoOnline(RgbLibWallet wallet, string electrumUrl, bool skipConsistencyCheck)
    {
        var walletStruct = _walletField.GetValue(wallet)!;
        var onlineOptionsJson = JsonSerializer.Serialize(new
        {
            indexer_url = electrumUrl,
            skip_consistency_check = skipConsistencyCheck,
            vanilla_sync_lookback = 100u,
        });

        var args = new object?[] { walletStruct, onlineOptionsJson };
        var result = _goOnlineMethod.Invoke(null, args);

        _walletField.SetValue(wallet, args[0]);

        var onlineJson = Require(ReadNativeResult(result), "go_online");
        _onlineJsonField.SetValue(wallet, onlineJson);
    }

    internal const string DotnetRuntimeDetailWithheldBecauseItNamesServerFilesystemPaths =
        "the .NET runtime raised this, not rgb-lib, and the runtime's own text names server "
        + "filesystem locations. The failure type above is the actionable part; the full detail is in "
        + "the server log.";

    internal static bool DetailWasWrittenForAnOperatorRatherThanByTheDotnetRuntime(Exception ex) =>
        ex is RgbLib.RgbLibException
        || Controllers.RgbOperatorFacingFailure.MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime(ex);

    internal static string WalletBringUpFailureForTheOperator(
        string walletId, string walletNetwork, Exception ex, string detailWithKeyMaterialRemoved) =>
        $"rgb-lib could not bring up wallet {walletId} on {walletNetwork} ({ex.GetType().Name}): "
        + (DetailWasWrittenForAnOperatorRatherThanByTheDotnetRuntime(ex)
            ? detailWithKeyMaterialRemoved
            : DotnetRuntimeDetailWithheldBecauseItNamesServerFilesystemPaths);

    internal static THandle CreateHandleOrDisposeWallet<TWallet, THandle>(
        TWallet wallet,
        Action<TWallet> bringOnline,
        Func<TWallet, THandle> buildHandle,
        Action<TWallet> disposeWallet,
        Action<Exception> reportDisposeFailure)
    {
        try
        {
            bringOnline(wallet);
            return buildHandle(wallet);
        }
        catch
        {
            try { disposeWallet(wallet); }
            catch (Exception disposeError) { reportDisposeFailure(disposeError); }
            throw;
        }
    }

    public bool UnloadWallet(string walletId) => UnloadFromCache(_wallets, walletId, _log);

    internal static bool UnloadFromCache(ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> wallets, string walletId, ILogger? log)
    {
        if (!wallets.TryGetValue(walletId, out var lazy))
            return true;

        if (lazy.IsValueCreated)
        {
            return DisposeAndEvict(wallets, walletId, lazy, log);
        }

        if (PendingConstructionDisposals.TryAdd(lazy, 0))
            _ = Task.Run(() =>
            {
                try { DisposeAndEvict(wallets, walletId, lazy, log); }
                finally { PendingConstructionDisposals.TryRemove(lazy, out _); }
            });
        return false;
    }

    static bool DisposeAndEvict(ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> wallets, string walletId, Lazy<RgbLibWalletHandle> lazy, ILogger? log)
    {
        RgbLibWalletHandle handle;
        try
        {
            handle = lazy.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not AppDomainUnloadedException and not BadImageFormatException)
        {
            log?.LogDebug(ex, "Wallet {WalletId} construction had failed; removing cache entry", walletId);
            wallets.TryRemove(new KeyValuePair<string, Lazy<RgbLibWalletHandle>>(walletId, lazy));
            return true;
        }

        try
        {
            handle.Dispose();
        }
        catch (Exception disposeFault)
        {
            log?.LogWarning(disposeFault,
                "Wallet {WalletId} threw while disposing; falling through to the not-freed path so the cache entry is still evicted once the native wallet is released",
                walletId);
        }

        if (handle.NativeWalletFreed)
        {
            wallets.TryRemove(new KeyValuePair<string, Lazy<RgbLibWalletHandle>>(walletId, lazy));
            log?.LogInformation("Wallet {WalletId} unloaded", walletId);
            return true;
        }
        else
        {
            log?.LogWarning(
                "Wallet {WalletId} unload timed out with an operation still running; native wallet will be freed after the operation completes",
                walletId);
            if (handle.TryStartDeferredDispose())
                _ = Task.Run(() => CompleteTimedOutDisposeAndEvictAsync(
                    wallets, walletId, lazy, handle, log));
            return false;
        }
    }

    static async Task CompleteTimedOutDisposeAndEvictAsync(
        ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> wallets,
        string walletId,
        Lazy<RgbLibWalletHandle> lazy,
        RgbLibWalletHandle handle,
        ILogger? log)
    {
        while (!handle.NativeWalletFreed)
        {
            try
            {
                handle.CompleteTimedOutDispose();
            }
            catch (Exception ex) when (ex is IOException or RgbWalletQuarantinedException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not AccessViolationException and not AppDomainUnloadedException and not BadImageFormatException)
            {
                log?.LogWarning(ex, "Wallet {WalletId} deferred unload failed; restart required to reclaim it", walletId);
                return;
            }
        }

        if (handle.NativeWalletFreed)
        {
            wallets.TryRemove(new KeyValuePair<string, Lazy<RgbLibWalletHandle>>(walletId, lazy));
            log?.LogInformation("Wallet {WalletId} deferred unload completed", walletId);
        }
    }

    public string GetWalletDataDir(string walletId, string walletNetwork) =>
        _config.GetWalletDataDir(walletId, walletNetwork);

    public async Task<string> GetAddressAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;

            var args = new object?[] { walletStruct };
            var result = _getAddressMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            return Require(ReadNativeResult(result), "get_address");
        }, ct);
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            // rgb-lib's parameter is skipSync, so it is the INVERSE of this method's `sync`. Passing `sync`
            // straight through — as this line did — silently reversed every caller: the three that ask for a
            // sync got none, and the page loads that take the `sync: false` default were the only ones
            // syncing, on the request path. The reflected argument array cannot name its parameters, so
            // the negation at the skipSync position is the whole of what keeps the flip right.
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, !sync };
            var result = _getBtcBalanceMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            var balanceJson = Require(ReadNativeResult(result), "get_btc_balance");
            var balance = JsonSerializer.Deserialize<BtcBalanceResponse>(balanceJson);

            return new BtcBalance(
                new BalanceInfo { Settled = balance?.Vanilla?.Settled ?? 0, Future = balance?.Vanilla?.Future ?? 0, Spendable = balance?.Vanilla?.Spendable ?? 0 },
                new BalanceInfo { Settled = balance?.Colored?.Settled ?? 0, Future = balance?.Colored?.Future ?? 0, Spendable = balance?.Colored?.Spendable ?? 0 }
            );
        }, ct);
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;

            var args = new object?[] { walletStruct, "[]" };
            var result = _listAssetsMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            var assetsJson = Require(ReadNativeResult(result), "list_assets");
            return InterpretListAssets(assetsJson);
        }, ct);
    }

    internal static List<RgbAsset> InterpretListAssets(string assetsJson)
    {
        var assets = JsonSerializer.Deserialize<ListAssetsResponse>(assetsJson);

        return assets?.Nia?.Select(a => new RgbAsset
        {
            AssetId = a.AssetId ?? "",
            Ticker = a.Ticker ?? "",
            Name = a.Name ?? "",
            Precision = a.Precision,
            IssuedSupply = a.IssuedSupply,
            Balance = a.Balance?.Settled ?? 0,
            FutureBalance = a.Balance?.Future ?? 0,
            SpendableBalance = a.Balance?.Spendable ?? 0
        }).ToList() ?? [];
    }

    public async Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, int minConfirmations = 1, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        var networkSettings = RGBConfiguration.GetNetworkSettings(dbWallet.Network);

        var assignment = amount.HasValue
            ? JsonSerializer.Serialize(new { Fungible = amount.Value })
            : "{\"Any\":null}";

        var expirationTs = expiration.HasValue
            ? expiration.Value.ToString()
            : DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds().ToString();

        var transportEndpoints = JsonSerializer.Serialize(new[] { networkSettings.ProxyEndpoint });
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var args = new object?[] { walletStruct, assetId, assignment, expirationTs, transportEndpoints, minConfirmations.ToString() };
            var result = _blindReceiveMethod.Invoke(null, args);
            
            var invoiceJson = Require(ReadNativeResult(result), "blind_receive");

            var invoice = JsonSerializer.Deserialize<BlindReceiveResponse>(invoiceJson);
            return new InvoiceResponse
            {
                Invoice = invoice?.Invoice ?? "",
                RecipientId = invoice?.RecipientId ?? "",
                ExpirationTimestamp = invoice?.ExpirationTimestamp,
                BatchTransferIdx = invoice?.BatchTransferIdx
            };
        }, ct);
    }

    public async Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, false, false };
            var result = _listUnspentsMethod.Invoke(null, args);

            return InterpretListUnspents(ReadNativeResult(result));
        }, ct);
    }

    public async Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, false };
            var result = _listTransactionsMethod.Invoke(null, args);

            return InterpretListBtcTransactions(ReadNativeResult(result));
        }, ct);
    }

    public async Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, false, count.ToString(), size.ToString(), ((int)feeRate).ToString(), false, true };
            var result = _createUtxosBeginMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            return InterpretCreateUtxosBegin(ReadNativeResult(result));
        }, ct);
    }

    public async Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, signedPsbt.Trim('"') };
            var result = _createUtxosEndMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            return Require(ReadNativeResult(result), "create_utxos_end");
        }, ct);
    }

    public async Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        
        var dbPath = Path.Combine(_config.GetWalletDataDir(walletId, dbWallet.Network), dbWallet.MasterFingerprint, "rgb_lib_db");
        if (!File.Exists(dbPath))
        {
            _log.LogWarning("RGB sqlite db not found at {Path}", dbPath);
            return [];
        }

        var transfers = await QueryRecentTransfersAsync(dbPath, assetId, ct);
        _log.LogInformation("ListTransfersAsync: Found {Count} transfers{AssetFilter}",
            transfers.Count, assetId == null ? "" : " for the selected asset");
        return transfers;
    }

    internal static async Task<List<RgbTransfer>> QueryRecentTransfersAsync(
        string dbPath, string? assetId = null, CancellationToken ct = default)
    {
        if (assetId?.Length > 1024)
            throw new InvalidDataException("RGB asset identifier exceeds its size bound");
        if (!File.Exists(dbPath)) return [];

        var transfers = new List<RgbTransfer>();
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.idx, bt.status, t.recipient_id, bt.txid, t.incoming,
                   CASE
                       WHEN t.incoming = 0 THEN
                           CASE WHEN json_valid(t.requested_assignment)
                                THEN json_array(json(t.requested_assignment)) END
                       ELSE
                           (SELECT json_group_array(json(assignment))
                            FROM (SELECT DISTINCT c.txo_idx AS txo_idx, c.assignment AS assignment
                                  FROM coloring c
                                  WHERE c.asset_transfer_idx = at.idx
                                    AND c.type IN (1, 2)
                                    AND json_valid(c.assignment)
                                  ORDER BY txo_idx, assignment
                                  LIMIT @assignmentLimit))
                   END,
                   t.recipient_type, at.asset_id, COALESCE(a.ticker, '')
            FROM transfer t
            JOIN asset_transfer at ON t.asset_transfer_idx = at.idx
            JOIN batch_transfer bt ON at.batch_transfer_idx = bt.idx
            -- beta.30 maps Asset::Id to asset.id and AssetTransfer::AssetId to
            -- asset_transfer.asset_id; the foreign key joins those differently named columns.
            JOIN asset a ON a.id = at.asset_id
            WHERE (@assetId IS NULL OR at.asset_id = @assetId)
            ORDER BY t.idx DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@assetId", (object?)assetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", MaxTransferListRows);
        cmd.Parameters.AddWithValue("@assignmentLimit", MaxCreditedAssignmentsPerAssetTransfer);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var incoming = reader.GetBoolean(4);
            var recipientType = reader.IsDBNull(6) ? null : reader.GetString(6);
            int kind;
            if (!incoming)
                kind = 3;
            else if (recipientType == null)
                kind = 0;
            else if (recipientType.Contains("\"Blind\""))
                kind = 1;
            else
                kind = 2;

            transfers.Add(new RgbTransfer
            {
                Idx = reader.GetInt32(0),
                Status = reader.GetInt32(1),
                RecipientId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Txid = reader.IsDBNull(3) ? null : reader.GetString(3),
                Kind = kind,
                Amount = RgbAssignmentJson.ToSignedByUnderReportingNeverOverReporting(
                    RgbAssignmentJson.SumFungibleSaturatingRatherThanWrapping(
                        reader.IsDBNull(5) ? null : reader.GetString(5))),
                AssetId = reader.GetString(7),
                AssetTicker = reader.GetString(8)
            });
        }
        return transfers;
    }

    public async Task<List<RgbMatchedTransfer>> ListIncomingTransfersForRecipientsAsync(
        string walletId, IReadOnlyCollection<string> recipientIds, string? assetId = null,
        CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        var dbPath = Path.Combine(
            _config.GetWalletDataDir(walletId, dbWallet.Network),
            dbWallet.MasterFingerprint, "rgb_lib_db");
        return await QueryIncomingTransfersForRecipientsAsync(
            dbPath, recipientIds, assetId, ct);
    }

    internal const int MaxCreditedAssignmentsPerAssetTransfer = 1024;

    internal static async Task<List<RgbMatchedTransfer>> QueryIncomingTransfersForRecipientsAsync(
        string dbPath, IReadOnlyCollection<string> recipientIds, string? assetId = null,
        CancellationToken ct = default)
    {
        const int maxRecipients = RGBInvoiceListener.DurableInvoicePageSize;
        if (recipientIds.Count > maxRecipients)
            throw new InvalidOperationException("RGB transfer recipient query exceeds its work bound");
        var recipients = recipientIds
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (recipients.Count == 0) return [];
        if (recipients.Any(r => r.Length > TransportEndpointValidator.MaxRgbInvoiceLength))
            throw new InvalidDataException("RGB transfer recipient identifier exceeds its size bound");
        if (!File.Exists(dbPath)) return [];

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var recipientParameters = new List<string>(recipients.Count);
        for (var i = 0; i < recipients.Count; i++)
        {
            var name = $"@recipient{i}";
            recipientParameters.Add(name);
            cmd.Parameters.AddWithValue(name, recipients[i]);
        }
        cmd.Parameters.AddWithValue("@assetId", (object?)assetId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", maxRecipients);
        cmd.Parameters.AddWithValue("@assignmentLimit", MaxCreditedAssignmentsPerAssetTransfer);
        cmd.CommandText = $$"""
            WITH candidate AS (
                SELECT t.idx, bt.status, t.recipient_id, bt.txid, t.incoming,
                       (SELECT json_group_array(json(assignment))
                        FROM (SELECT DISTINCT c.txo_idx AS txo_idx, c.assignment AS assignment
                              FROM coloring c
                              WHERE c.asset_transfer_idx = atx.idx
                                AND c.type IN (1, 2)
                                AND json_valid(c.assignment)
                              ORDER BY txo_idx, assignment
                              LIMIT @assignmentLimit)) AS credited_assignments,
                       t.recipient_type,
                       atx.asset_id AS asset_id,
                       COALESCE(a.ticker, '') AS ticker,
                       a.name AS name,
                       a.precision AS precision,
                       a.initial_supply AS issued_supply,
                       ROW_NUMBER() OVER (
                           PARTITION BY t.recipient_id ORDER BY t.idx) AS recipient_rank
                FROM transfer t
                INNER JOIN asset_transfer atx ON t.asset_transfer_idx = atx.idx
                INNER JOIN batch_transfer bt ON atx.batch_transfer_idx = bt.idx
                -- beta.30 maps Asset::Id to asset.id and AssetTransfer::AssetId to
                -- asset_transfer.asset_id; the foreign key joins those differently named columns.
                INNER JOIN asset a ON a.id = atx.asset_id
                WHERE t.incoming = 1
                  AND bt.status IN (1, 2, 3, 4)
                  AND t.recipient_id IN ({{string.Join(", ", recipientParameters)}})
                  AND (@assetId IS NULL OR atx.asset_id = @assetId)
            )
            SELECT idx, status, recipient_id, txid, incoming, credited_assignments, recipient_type,
                   asset_id, ticker, name, precision, issued_supply
            FROM candidate
            WHERE recipient_rank = 1
            ORDER BY idx
            LIMIT @limit
            """;

        var results = new List<RgbMatchedTransfer>(recipients.Count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var recipientType = reader.IsDBNull(6) ? null : reader.GetString(6);
            var kind = recipientType == null ? 0
                : recipientType.Contains("\"Blind\"", StringComparison.Ordinal) ? 1 : 2;
            var issuedSupply = reader.IsDBNull(11)
                ? 0
                : ulong.TryParse(Convert.ToString(reader.GetValue(11)), out var parsedSupply)
                    ? parsedSupply : 0;
            var matchedAsset = new RgbAsset
            {
                AssetId = reader.GetString(7),
                Ticker = reader.GetString(8),
                Name = reader.GetString(9),
                Precision = reader.GetInt32(10),
                IssuedSupply = issuedSupply
            };
            results.Add(new RgbMatchedTransfer(matchedAsset.AssetId, matchedAsset,
                new RgbTransfer
                {
                    Idx = reader.GetInt32(0),
                    Status = reader.GetInt32(1),
                    RecipientId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Txid = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Kind = kind,
                    Amount = RgbAssignmentJson.ToSignedByUnderReportingNeverOverReporting(
                        RgbAssignmentJson.SumFungibleSaturatingRatherThanWrapping(
                            reader.IsDBNull(5) ? null : reader.GetString(5)))
                }));
        }
        return results;
    }

    public async Task RefreshAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, null, "[]", false };
            var result = _refreshMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            // WHY: fail-closed — anything other than a confirmed Ok-with-payload (null result,
            // Err, or Ok with a null pointer) must throw so the write-ahead path quarantines.
            Require(ReadNativeResult(result), "refresh");
        }, ct);
    }

    public async Task<string> SnapshotStockAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        var stockDir = RgbStockDurability.ResolveStockDir(
            _config.GetWalletDataDir(walletId, dbWallet.Network), dbWallet.MasterFingerprint);

        return await handle.ExecuteAsync(_ =>
        {
            ct.ThrowIfCancellationRequested();
            return RgbStockDurability.SnapshotStock(stockDir);
        }, ct);
    }

    public async Task<RgbVerificationSnapshot> SnapshotVerificationStateAsync(
        string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        var walletDataDir = _config.GetWalletDataDir(walletId, dbWallet.Network);
        var walletDir = Path.Combine(walletDataDir, dbWallet.MasterFingerprint);
        var stockDir = RgbStockDurability.ResolveStockDir(walletDataDir, dbWallet.MasterFingerprint);

        return await handle.ExecuteAsync(_ =>
        {
            ct.ThrowIfCancellationRequested();
            return RgbStockDurability.SnapshotVerificationState(stockDir, walletDir);
        }, ct);
    }
    
    public async Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var amountsJson = JsonSerializer.Serialize(amounts.Select(a => a.ToString()).ToArray());
            var walletStruct = _walletField.GetValue(wallet)!;

            var args = new object?[] { walletStruct, ticker, name, precision.ToString(), amountsJson };
            var result = _issueAssetNiaMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            var assetJson = Require(ReadNativeResult(result), "issue_asset_nia");
            var asset = JsonSerializer.Deserialize<IssueAssetResponse>(assetJson);
            
            return new RgbAsset
            {
                AssetId = asset?.AssetId ?? "",
                Ticker = asset?.Ticker ?? ticker,
                Name = asset?.Name ?? name,
                Precision = asset?.Precision ?? precision,
                IssuedSupply = asset?.IssuedSupply ?? checked((ulong)amounts.Sum())
            };
        }, ct);
    }

    public async Task<string> SendBeginAsync(string walletId, string recipientMapJson, float feeRate, int minConfirmations = 1, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, recipientMapJson, false, ((int)Math.Round(feeRate)).ToString(), minConfirmations.ToString(), null, false };
            var result = _sendBeginMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            return Require(ReadNativeResult(result), "send_begin");
        }, ct);
    }

    public async Task<string> SendEndAsync(string walletId, string signedPsbt, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));

            var args = new object?[] { walletStruct, onlineJson, signedPsbt.Trim('"') };
            var result = _sendEndMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            var json = Require(ReadNativeResult(result), "send_end");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("txid", out var txidProp))
                return txidProp.GetString() ?? json;

            return json;
        }, ct);
    }

    static readonly SemaphoreSlim _backupGate = new(1, 1);
    static readonly ConcurrentDictionary<string, RestoreCooldownGate> _backupCooldowns = new();
    static long _backupGateHolderSinceMonotonicTimestamp;
    static string? _backupGateHolderWalletId;

    internal static RestoreCooldownGate GetOrCreateBackupCooldown(string walletId, Func<RestoreCooldownGate> create) =>
        _backupCooldowns.GetOrAdd(walletId, _ => create());

    internal static string DescribeElapsedWithoutOverstatingIt(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} minute{((int)elapsed.TotalMinutes == 1 ? "" : "s")}"
            : $"{(int)elapsed.TotalSeconds} second{((int)elapsed.TotalSeconds == 1 ? "" : "s")}";

    internal static TimeSpan ResolveBackupCooldown(RGBConfiguration cfg) =>
        TimeSpan.FromSeconds(Math.Clamp(cfg.BackupCooldownSeconds,
            RGBConfiguration.BackupCooldownSecondsMin, RGBConfiguration.BackupCooldownSecondsMax));

    internal static TimeSpan ResolveBackupStartWaitTimeout(RGBConfiguration cfg) =>
        TimeSpan.FromSeconds(Math.Clamp(cfg.BackupStartWaitTimeoutSeconds,
            RGBConfiguration.BackupStartWaitTimeoutSecondsMin,
            RGBConfiguration.BackupStartWaitTimeoutSecondsMax));

    internal static TimeSpan ResolveBackupStuckThreshold(RGBConfiguration cfg) =>
        TimeSpan.FromSeconds(Math.Clamp(cfg.BackupStuckThresholdSeconds,
            RGBConfiguration.BackupStuckThresholdSecondsMin,
            RGBConfiguration.BackupStuckThresholdSecondsMax));

    internal static string DescribeRetryDelayWithoutUnderstatingIt(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 60)
            return $"{seconds} second{(seconds == 1 ? "" : "s")}";
        var minutes = (int)Math.Ceiling(seconds / 60.0);
        return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
    }

    internal static string DescribeBackupCooldownRefusal(TimeSpan remaining) =>
        "A wallet backup was attempted recently. Try again in "
        + $"{DescribeRetryDelayWithoutUnderstatingIt(remaining)}.";

    internal static string DescribeBackupGateRefusal(TimeSpan heldFor, TimeSpan stuckThreshold) =>
        heldFor > stuckThreshold
            ? "Another wallet backup has been holding this lock for at least "
              + $"{DescribeElapsedWithoutOverstatingIt(heldFor)}. If it does not clear on its own, "
              + "restart BTCPay to release it."
            : "Another wallet backup is currently in progress. Try again shortly.";

    public async Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
    {
        if (RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(password))
            throw new InvalidOperationException(
                RestoreProcessRunner.BackupPasswordLineBreakRefusal);

        var handle = await GetOrCreateWalletAsync(walletId, ct);

        var cooldown = GetOrCreateBackupCooldown(walletId,
            () => new RestoreCooldownGate(ResolveBackupCooldown(_config)));
        var nowUtc = DateTimeOffset.UtcNow;
        if (cooldown.IsCoolingDown(nowUtc))
            throw new InvalidOperationException(DescribeBackupCooldownRefusal(cooldown.Remaining(nowUtc)));

        var tempPath = Path.Combine(Path.GetTempPath(), $"rgb-backup-{walletId}-{Guid.NewGuid():N}.rgb");

        var entered = await _backupGate.WaitAsync(TimeSpan.Zero, ct);
        if (!entered)
        {
            var holderSinceTimestamp = Interlocked.Read(ref _backupGateHolderSinceMonotonicTimestamp);
            var heldFor = holderSinceTimestamp == 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(holderSinceTimestamp);
            var stuckThreshold = ResolveBackupStuckThreshold(_config);
            if (heldFor > stuckThreshold)
                _log.LogWarning(
                    "Wallet backup gate has been held by wallet {HolderWalletId} for {HeldForSeconds:N0}s, past the {ThresholdSeconds}s stuck threshold",
                    Volatile.Read(ref _backupGateHolderWalletId), heldFor.TotalSeconds, stuckThreshold.TotalSeconds);
            throw new InvalidOperationException(DescribeBackupGateRefusal(heldFor, stuckThreshold));
        }

        try
        {
            var afterAcquiringTheGateUtc = DateTimeOffset.UtcNow;
            if (cooldown.IsCoolingDown(afterAcquiringTheGateUtc))
                throw new InvalidOperationException(
                    DescribeBackupCooldownRefusal(cooldown.Remaining(afterAcquiringTheGateUtc)));

            Interlocked.Exchange(ref _backupGateHolderSinceMonotonicTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _backupGateHolderWalletId, walletId);
            using var startWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startWait.CancelAfter(ResolveBackupStartWaitTimeout(_config));

            try
            {
                await handle.ExecuteAsync(wallet =>
                {
                    startWait.Token.ThrowIfCancellationRequested();
                    Backup(wallet, tempPath, password);
                }, startWait.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Timed out waiting to start the wallet backup. No backup was written; try again. "
                    + "If it keeps timing out, restart BTCPay — that releases any wallet operation "
                    + "still holding this wallet.");
            }
            finally
            {
                cooldown.RecordAttempt(DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            Volatile.Write(ref _backupGateHolderWalletId, null);
            Interlocked.Exchange(ref _backupGateHolderSinceMonotonicTimestamp, 0);
            _backupGate.Release();
        }

        return tempPath;
    }

    internal void Backup(RgbLibWallet wallet, string backupPath, string password)
    {
        var walletStruct = _walletField.GetValue(wallet)!;
        var args = new object?[] { walletStruct, backupPath, password };
        var result = (CResult)_backupMethod.Invoke(null, args)!;

        _walletField.SetValue(wallet, args[0]);

        if (result.result != CResultValue.Ok)
        {
            FreeCResultErrorString(result);
            throw new RgbLibException("Failed to backup");
        }
    }

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResult rgblib_invoice_new([MarshalAs(UnmanagedType.LPUTF8Str)] string invoiceString);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgblib_invoice_data(ref COpaqueStruct invoice);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void free_invoice(COpaqueStruct invoice);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgblib_create_consignments(
        ref COpaqueStruct wallet,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string psbt);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgblib_fail_transfers(
        ref COpaqueStruct wallet,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string online,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? batchTransferIdxOpt,
        [MarshalAs(UnmanagedType.I1)] bool noAssetOnly,
        [MarshalAs(UnmanagedType.I1)] bool skipSync);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgblib_string_free(IntPtr ptr);

    internal string ReadRgbLibString(CResultString result, string call)
    {
        try
        {
            var payload = result.inner != IntPtr.Zero ? Marshal.PtrToStringUTF8(result.inner) : null;
            if (result.result != CResultValue.Ok)
                throw new RgbLibException($"{call} failed: {payload ?? "no detail"}");
            return payload ?? throw new RgbLibException($"{call} returned a null payload");
        }
        finally
        {
            if (result.inner != IntPtr.Zero)
                _stringFree(result.inner);
        }
    }

    public async Task<string> CreateConsignmentsAsync(string walletId, string psbt, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = (COpaqueStruct)_walletField.GetValue(wallet)!;
            var result = rgblib_create_consignments(ref walletStruct, psbt.Trim('"'));
            return ReadRgbLibString(result, "create_consignments");
        }, ct);
    }

    public async Task FailTransfersAsync(string walletId, int batchTransferIdx, bool noAssetOnly, bool skipSync, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = (COpaqueStruct)_walletField.GetValue(wallet)!;
            var onlineJson = (string)(_onlineJsonField.GetValue(wallet) ?? throw new RgbLibException("Wallet is offline"));
            var result = rgblib_fail_transfers(ref walletStruct, onlineJson, batchTransferIdx.ToString(), noAssetOnly, skipSync);
            ReadRgbLibString(result, "fail_transfers");
        }, ct);
    }

    internal const ulong RawUtf8StringOpaqueType = 0;

    internal void FreeCResultErrorString(CResult result)
    {
        if (result.result == CResultValue.Ok)
            return;
        if (result.inner.ty != RawUtf8StringOpaqueType)
            return;
        if (result.inner.ptr == IntPtr.Zero)
            return;
        _stringFree(result.inner.ptr);
    }

    public RgbInvoiceData DecodeInvoice(string invoiceString)
    {
        var newResult = rgblib_invoice_new(invoiceString);
        if (newResult.result != CResultValue.Ok)
        {
            FreeCResultErrorString(newResult);
            throw new RgbLibException("Invalid RGB invoice");
        }

        var invoiceStruct = newResult.inner;
        try
        {
            var dataResult = rgblib_invoice_data(ref invoiceStruct);
            var json = ReadRgbLibString(dataResult, "invoice_data");

            _log.LogDebug("Decoded invoice for recipient {RecipientId}",
                json.Length > 200 ? "(large payload)" : "(ok)");
            return JsonSerializer.Deserialize<RgbInvoiceData>(json)
                   ?? throw new RgbLibException("Failed to parse invoice data JSON");
        }
        finally
        {
            free_invoice(invoiceStruct);
        }
    }

    public RgbKeys GenerateKeys(string network)
    {
        var args = new object?[] { NetworkHelper.MapNetworkToRgbLibFormat(network), "Taproot" };
        var result = _generateKeysMethod.Invoke(null, args);
        var keysJson = Require(ReadNativeResult(result), "generate_keys");
        var keys = JsonSerializer.Deserialize<GenerateKeysResponse>(keysJson);

        return new RgbKeys
        {
            Mnemonic = keys?.Mnemonic ?? "",
            Xpub = keys?.Xpub ?? "",
            AccountXpubVanilla = keys?.AccountXpubVanilla ?? "",
            AccountXpubColored = keys?.AccountXpubColored ?? "",
            MasterFingerprint = keys?.MasterFingerprint ?? ""
        };
    }

    public RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network)
    {
        var args = new object?[] { NetworkHelper.MapNetworkToRgbLibFormat(network), mnemonic, "Taproot" };
        var result = _restoreKeysMethod.Invoke(null, args);
        var keysJson = Require(ReadNativeResult(result), "restore_keys");
        var keys = JsonSerializer.Deserialize<GenerateKeysResponse>(keysJson);

        return new RgbKeys
        {
            Mnemonic = mnemonic,
            Xpub = keys?.Xpub ?? "",
            AccountXpubVanilla = keys?.AccountXpubVanilla ?? "",
            AccountXpubColored = keys?.AccountXpubColored ?? "",
            MasterFingerprint = keys?.MasterFingerprint ?? ""
        };
    }

    // Exactly one of Payload / Error is non-null for a well-formed result. Both null means the
    // native side returned something this binding cannot interpret, which is a failure — never an
    // empty success.
    internal readonly record struct NativeCallResult(string? Payload, string? Error)
    {
        internal bool IsOk => Payload != null;
    }

    internal NativeCallResult ReadNativeResult(object? result)
    {
        if (result == null) return default;

        // Defence in depth, not the mechanism: GetValue would already throw on a foreign type.
        // Making the refusal explicit means an unrecognised shape leaks rather than risking a
        // foreign free.
        if (result.GetType() != _cResultStringType) return default;

        var isOk = _resultField.GetValue(result)?.ToString() == "Ok";
        var ptr = (IntPtr)_innerField.GetValue(result)!;
        if (ptr == IntPtr.Zero) return default;

        // Zero the box BEFORE freeing: the pointer becomes unreachable through this result, so a
        // second read frees nothing instead of corrupting the heap.
        _innerField.SetValue(result, IntPtr.Zero);
        try
        {
            var payload = _marshal(ptr);
            return isOk ? new NativeCallResult(payload, null) : new NativeCallResult(null, payload);
        }
        finally
        {
            _stringFree(ptr);
        }
    }

    internal static string Require(NativeCallResult r, string call)
        => r.Payload ?? throw new RgbLibException(r.Error ?? $"{call} failed");

    internal static string InterpretCreateUtxosBegin(NativeCallResult r)
    {
        if (r.IsOk) return r.Payload!;
        // WHY the error text: rgb-lib reports "already has enough UTXOs" as a failure, and the
        // caller treats that as success-with-nothing-to-do.
        if (r.Error?.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase) == true) return "";
        throw new RgbLibException(r.Error ?? "create_utxos_begin failed");
    }

    internal static List<BtcTransaction> InterpretListBtcTransactions(NativeCallResult r)
        => JsonSerializer.Deserialize<List<BtcTransaction>>(Require(r, "list_transactions")) ?? [];

    internal static List<UnspentOutput> InterpretListUnspents(NativeCallResult r)
    {
        var unspentsJson = r.Payload;
        // WHY throw rather than return an empty list: a genuinely empty wallet yields Ok with "[]", so a
        // null payload means the native call FAILED. Returning empty made a failure indistinguishable
        // from "this wallet has no UTXOs", and the replenishment sweep then read zero colorable UTXOs,
        // computed zero free slots, and signed a UTXO-creation transaction because of an error — the
        // false-ACCEPT its own invariant forbids. Observed live on 2026-08-04 against a wallet that had
        // 23 UTXOs at the time. Seven sibling calls in this file already throw on a null payload;
        // ListBtcTransactionsAsync was the last one that did not, and was closed under finding G.
        if (unspentsJson == null)
            throw new RgbLibException(r.Error ?? "list_unspents failed");

        var unspents = JsonSerializer.Deserialize<List<UnspentOutputResponse>>(unspentsJson);
        return unspents?.Select(u => new UnspentOutput(
            new UtxoInfo
            {
                Outpoint = new Outpoint(u.Utxo?.Outpoint?.Txid ?? "", (int)(u.Utxo?.Outpoint?.Vout ?? 0)),
                BtcAmount = u.Utxo?.BtcAmount ?? 0,
                Colorable = u.Utxo?.Colorable ?? false
            },
            u.RgbAllocations?.Select(a => new RgbAllocation
            {
                AssetId = a.AssetId ?? "",
                Amount = RgbAssignmentJson.FungibleValueOrZeroForEveryOtherVariant(a.Assignment),
                Settled = a.Settled
            }).ToList() ?? []
        )).ToList() ?? [];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var lazyWallet in _wallets.Values)
        {
            if (lazyWallet.IsValueCreated)
            {
                try { lazyWallet.Value.Dispose(); }
                catch (Exception ex) { _log.LogWarning(ex, "Error disposing wallet"); }
            }
        }
        _wallets.Clear();
        
        GC.SuppressFinalize(this);
    }
}

class BtcBalanceResponse
{
    [JsonPropertyName("vanilla")] public BalanceInfoResponse? Vanilla { get; set; }
    [JsonPropertyName("colored")] public BalanceInfoResponse? Colored { get; set; }
}

class BalanceInfoResponse
{
    [JsonPropertyName("settled")] public long Settled { get; set; }
    [JsonPropertyName("future")] public long Future { get; set; }
    [JsonPropertyName("spendable")] public long Spendable { get; set; }
}

class ListAssetsResponse
{
    [JsonPropertyName("nia")] public List<AssetNiaResponse>? Nia { get; set; }
}

class AssetNiaResponse
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("issued_supply")] public ulong IssuedSupply { get; set; }
    [JsonPropertyName("balance")] public AssetBalanceResponse? Balance { get; set; }
}

class AssetBalanceResponse
{
    [JsonPropertyName("settled")] public ulong Settled { get; set; }
    [JsonPropertyName("future")] public ulong Future { get; set; }
    [JsonPropertyName("spendable")] public ulong Spendable { get; set; }
}

class BlindReceiveResponse
{
    [JsonPropertyName("invoice")] public string? Invoice { get; set; }
    [JsonPropertyName("recipient_id")] public string? RecipientId { get; set; }
    [JsonPropertyName("expiration_timestamp")] public long? ExpirationTimestamp { get; set; }
    [JsonPropertyName("batch_transfer_idx")] public int? BatchTransferIdx { get; set; }
}

class UnspentOutputResponse
{
    [JsonPropertyName("utxo")] public UtxoResponse? Utxo { get; set; }
    [JsonPropertyName("rgb_allocations")] public List<RgbAllocationResponse>? RgbAllocations { get; set; }
}

class UtxoResponse
{
    [JsonPropertyName("outpoint")] public OutpointResponse? Outpoint { get; set; }
    [JsonPropertyName("btc_amount")] public long BtcAmount { get; set; }
    [JsonPropertyName("colorable")] public bool Colorable { get; set; }
}

class OutpointResponse
{
    [JsonPropertyName("txid")] public string? Txid { get; set; }
    [JsonPropertyName("vout")] public uint? Vout { get; set; }
}

class RgbAllocationResponse
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("assignment")] public JsonElement Assignment { get; set; }
    [JsonPropertyName("settled")] public bool Settled { get; set; }
}

class TransferResponse
{
    [JsonPropertyName("idx")] public int Idx { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("status")] public JsonElement Status { get; set; }
    [JsonPropertyName("amount")][JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Amount { get; set; }
    [JsonPropertyName("kind")] public JsonElement Kind { get; set; }
    [JsonPropertyName("txid")] public string? Txid { get; set; }
    [JsonPropertyName("recipient_id")] public string? RecipientId { get; set; }
    [JsonPropertyName("receive_utxo")] public OutpointResponse? ReceiveUtxo { get; set; }
    
    public int GetStatusInt() => Status.ValueKind == JsonValueKind.Number ? Status.GetInt32() : ParseStatus(Status.GetString());
    public int GetKindInt() => Kind.ValueKind == JsonValueKind.Number ? Kind.GetInt32() : ParseKind(Kind.GetString());
    
    static int ParseStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "waitingcounterparty" => 1,
        "waitingconfirmations" => 2,
        "settled" => 3,
        "failed" => 4,
        "initiated" => 5,
        "waitingsafeheight" => 6,
        _ => int.TryParse(s, out var n) ? n : -1
    };
    
    static int ParseKind(string? s) => s?.ToLowerInvariant() switch
    {
        "issuance" => 0,
        "receiveblind" or "receive_blind" => 1,
        "receivewitness" or "receive_witness" => 2,
        "send" => 3,
        _ => int.TryParse(s, out var n) ? n : -1
    };
}

class IssueAssetResponse
{
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("ticker")] public string? Ticker { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("issued_supply")] public ulong IssuedSupply { get; set; }
}

class GenerateKeysResponse
{
    [JsonPropertyName("mnemonic")] public string? Mnemonic { get; set; }
    [JsonPropertyName("xpub")] public string? Xpub { get; set; }
    [JsonPropertyName("account_xpub_vanilla")] public string? AccountXpubVanilla { get; set; }
    [JsonPropertyName("account_xpub_colored")] public string? AccountXpubColored { get; set; }
    [JsonPropertyName("master_fingerprint")] public string? MasterFingerprint { get; set; }
}
