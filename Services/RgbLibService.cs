using System.Collections.Concurrent;
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
    
    readonly MethodInfo _blindReceiveMethod;
    readonly MethodInfo _listUnspentsMethod;
    readonly MethodInfo _createUtxosBeginMethod;
    readonly MethodInfo _createUtxosEndMethod;
    readonly MethodInfo _refreshMethod;
    readonly MethodInfo _listTransactionsMethod;
    readonly MethodInfo _restoreBackupMethod;
    readonly MethodInfo _sendBeginMethod;
    readonly MethodInfo _sendEndMethod;

    bool _disposed;

    public RgbLibService(
        RGBConfiguration config,
        RGBPluginDbContextFactory db,
        ILogger<RgbLibService> log)
    {
        _config = config;
        _db = db;
        _log = log;
        
        var assembly = typeof(RgbLibWallet).Assembly;
        _nativeMethodsType = assembly.GetType("RgbLib.NativeMethods")!;
        _cResultStringType = assembly.GetType("RgbLib.CResultString")!;
        
        _walletField = typeof(RgbLibWallet).GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onlineJsonField = typeof(RgbLibWallet).GetField("_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _resultField = _cResultStringType.GetField("result")!;
        _innerField = _cResultStringType.GetField("inner")!;
        
        _blindReceiveMethod = _nativeMethodsType.GetMethod("rgblib_blind_receive")!;
        _listUnspentsMethod = _nativeMethodsType.GetMethod("rgblib_list_unspents")!;
        _createUtxosBeginMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_begin")!;
        _createUtxosEndMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_end")!;
        _refreshMethod = _nativeMethodsType.GetMethod("rgblib_refresh")!;
        _listTransactionsMethod = _nativeMethodsType.GetMethod("rgblib_list_transactions")!;
        _restoreBackupMethod = _nativeMethodsType.GetMethod("rgblib_restore_backup")!;
        _sendBeginMethod = _nativeMethodsType.GetMethod("rgblib_send_begin")!;
        _sendEndMethod = _nativeMethodsType.GetMethod("rgblib_send_end")!;
    }

    public async Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");

        var lazyWallet = _wallets.GetOrAdd(walletId, _ =>
            new Lazy<RgbLibWalletHandle>(() => CreateWalletInternal(
                walletId, 
                dbWallet.XpubVanilla, 
                dbWallet.XpubColored, 
                dbWallet.MasterFingerprint,
                dbWallet.Network,
                RGBWalletService.ResolveAllocationsPerUtxo(dbWallet.MaxAllocationsPerUtxo))));

        return lazyWallet.Value;
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
            ["supported_schemas"] = new[] { "Nia", "Cfa" }
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
        RgbLibWallet wallet;
        try
        {
            wallet = new RgbLibWallet(configJson, keysJson);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RgbLibWallet ctor failed. walletId={WalletId} dataDir={DataDir} fingerprint={Fingerprint} config={Config} keys={Keys}",
                walletId, dataDir, masterFingerprint, configJson, keysJson);
            throw;
        }
        wallet.GoOnline(networkSettings.ElectrumUrl, true);

        _log.LogInformation("Wallet {WalletId} connected to {Electrum}", walletId, networkSettings.ElectrumUrl);
        return new RgbLibWalletHandle(wallet, walletId, _log);
    }

    public void UnloadWallet(string walletId) => UnloadFromCache(_wallets, walletId, _log);

    internal static void UnloadFromCache(ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> wallets, string walletId, ILogger? log)
    {
        if (!wallets.TryGetValue(walletId, out var lazy))
            return;

        if (lazy.IsValueCreated)
        {
            DisposeAndEvict(wallets, walletId, lazy, log);
            return;
        }

        Task.Run(() => DisposeAndEvict(wallets, walletId, lazy, log));
    }

    static void DisposeAndEvict(ConcurrentDictionary<string, Lazy<RgbLibWalletHandle>> wallets, string walletId, Lazy<RgbLibWalletHandle> lazy, ILogger? log)
    {
        RgbLibWalletHandle handle;
        try
        {
            handle = lazy.Value;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "Wallet {WalletId} construction had failed; removing cache entry", walletId);
            wallets.TryRemove(new KeyValuePair<string, Lazy<RgbLibWalletHandle>>(walletId, lazy));
            return;
        }

        handle.Dispose();

        if (handle.NativeWalletFreed)
        {
            wallets.TryRemove(new KeyValuePair<string, Lazy<RgbLibWalletHandle>>(walletId, lazy));
            log?.LogInformation("Wallet {WalletId} unloaded", walletId);
        }
        else
        {
            log?.LogWarning(
                "Wallet {WalletId} unload timed out with an operation still running; native wallet leaked and the handle is kept cached to prevent a second instance on the same data dir. Restart required to reclaim it.",
                walletId);
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
            return wallet.GetAddress();
        }, ct);
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);

        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var balanceJson = wallet.GetBtcBalance(sync);
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
            var assetsJson = wallet.ListAssets("[]");
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
        }, ct);
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
            
            var invoiceJson = GetNativeResult(result);
            if (invoiceJson == null)
            {
                throw new RgbLibException(GetNativeError(result) ?? "blind_receive failed");
            }
            
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
            
            var unspentsJson = GetNativeResult(result);
            if (unspentsJson == null)
            {
                return new List<UnspentOutput>();
            }
            
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
                    Amount = a.Amount,
                    Settled = a.Settled
                }).ToList() ?? []
            )).ToList() ?? [];
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

            var json = GetNativeResult(result);
            if (json == null)
            {
                return new List<BtcTransaction>();
            }

            return JsonSerializer.Deserialize<List<BtcTransaction>>(json) ?? [];
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

            var args = new object?[] { walletStruct, onlineJson, true, count.ToString(), size.ToString(), ((int)feeRate).ToString(), false, false };
            var result = _createUtxosBeginMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);

            var psbt = GetNativeResult(result);
            if (psbt == null)
            {
                var error = GetNativeError(result);
                if (error?.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return "";
                }
                throw new RgbLibException(error ?? "create_utxos_begin failed");
            }
            
            return psbt;
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

            var resultJson = GetNativeResult(result);
            if (resultJson == null)
            {
                throw new RgbLibException(GetNativeError(result) ?? "create_utxos_end failed");
            }
            
            return resultJson;
        }, ct);
    }

    public async Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(assetId))
        {
            return [];
        }

        await using var ctx = _db.CreateContext();
        var dbWallet = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"Wallet {walletId} not found");
        
        var dbPath = Path.Combine(_config.GetWalletDataDir(walletId, dbWallet.Network), dbWallet.MasterFingerprint, "rgb_lib_db");
        if (!File.Exists(dbPath))
        {
            _log.LogWarning("RGB sqlite db not found at {Path}", dbPath);
            return [];
        }
        
        var transfers = new List<RgbTransfer>();
        var connStr = $"Data Source={dbPath};Mode=ReadOnly";
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT t.idx, bt.status, t.recipient_id, bt.txid, t.incoming,
                   CASE
                       WHEN t.incoming = 0 THEN
                           json_extract(t.requested_assignment, '$.Fungible')
                       ELSE
                           COALESCE(
                               (SELECT json_extract(c.assignment, '$.Fungible')
                                FROM coloring c WHERE c.asset_transfer_idx = at.idx AND c.type IN (1, 2) LIMIT 1),
                               (SELECT json_extract(c.assignment, '$.Fungible')
                                FROM coloring c WHERE c.asset_transfer_idx = at.idx AND c.type != 3 LIMIT 1),
                               (SELECT json_extract(c.assignment, '$.Fungible')
                                FROM coloring c WHERE c.asset_transfer_idx = at.idx LIMIT 1)
                           )
                   END,
                   t.recipient_type
            FROM transfer t
            JOIN asset_transfer at ON t.asset_transfer_idx = at.idx
            JOIN batch_transfer bt ON at.batch_transfer_idx = bt.idx
            WHERE at.asset_id = @assetId";
        cmd.Parameters.AddWithValue("@assetId", assetId);

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
                Amount = reader.IsDBNull(5) ? 0 : reader.GetInt64(5)
            });
        }
        
        _log.LogInformation("ListTransfersAsync: Found {Count} transfers for asset {AssetId}", transfers.Count, assetId[..Math.Min(30, assetId.Length)]);
        return transfers;
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
            _refreshMethod.Invoke(null, args);

            _walletField.SetValue(wallet, args[0]);
        }, ct);
    }
    
    public async Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var amountsJson = JsonSerializer.Serialize(amounts.Select(a => a.ToString()).ToArray());
            var assetJson = wallet.IssueAssetNia(ticker, name, precision.ToString(), amountsJson);
            var asset = JsonSerializer.Deserialize<IssueAssetResponse>(assetJson);
            
            return new RgbAsset
            {
                AssetId = asset?.AssetId ?? "",
                Ticker = asset?.Ticker ?? ticker,
                Name = asset?.Name ?? name,
                Precision = asset?.Precision ?? precision,
                IssuedSupply = asset?.IssuedSupply ?? amounts.Sum()
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

            var psbt = GetNativeResult(result);
            if (psbt == null)
                throw new RgbLibException(GetNativeError(result) ?? "send_begin failed");

            return psbt;
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

            var json = GetNativeResult(result);
            if (json == null)
                throw new RgbLibException(GetNativeError(result) ?? "send_end failed");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("txid", out var txidProp))
                return txidProp.GetString() ?? json;

            return json;
        }, ct);
    }

    public async Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        var tempPath = Path.Combine(Path.GetTempPath(), $"rgb-backup-{walletId}-{Guid.NewGuid():N}.rgb");

        await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            wallet.Backup(tempPath, password);
        }, ct);

        return tempPath;
    }

    public void RestoreBackup(string backupPath, string password, string targetDir)
    {
        var args = new object?[] { backupPath, password, targetDir };
        var result = _restoreBackupMethod.Invoke(null, args);

        if (result == null)
            throw new RgbLibException("restore_backup returned null");

        var cResultType = result.GetType();
        var isSuccessProp = cResultType.GetProperty("IsSuccess");
        if (isSuccessProp == null)
            throw new RgbLibException("restore_backup: cannot read result type");

        var isSuccess = (bool)(isSuccessProp.GetValue(result) ?? false);
        if (!isSuccess)
        {
            var errorMsg = "restore_backup failed";
            try
            {
                var getError = cResultType.GetMethod("GetError");
                if (getError != null)
                    errorMsg = getError.Invoke(result, null)?.ToString() ?? errorMsg;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not extract error from CResult");
            }
            throw new RgbLibException(errorMsg);
        }
    }

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResult rgblib_invoice_new([MarshalAs(UnmanagedType.LPUTF8Str)] string invoiceString);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgblib_invoice_data(ref COpaqueStruct invoice);

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void free_invoice(COpaqueStruct invoice);

    public RgbInvoiceData DecodeInvoice(string invoiceString)
    {
        var newResult = rgblib_invoice_new(invoiceString);
        if (newResult.result != CResultValue.Ok)
        {
            throw new RgbLibException("Invalid RGB invoice");
        }

        var invoiceStruct = newResult.inner;
        try
        {
            var dataResult = rgblib_invoice_data(ref invoiceStruct);
            var json = (string?)null;
            if (dataResult.result == CResultValue.Ok && dataResult.inner != IntPtr.Zero)
                json = Marshal.PtrToStringUTF8(dataResult.inner);

            if (json == null)
                throw new RgbLibException("Failed to decode invoice data");

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
        var keysJson = RgbLibWallet.GenerateKeys(NetworkHelper.MapNetworkToRgbLibFormat(network));
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
        var keysJson = RgbLibWallet.RestoreKeys(NetworkHelper.MapNetworkToRgbLibFormat(network), mnemonic);
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

    string? GetNativeResult(object? result)
    {
        if (result == null) return null;
        var resultValue = _resultField.GetValue(result);
        var innerPtr = (IntPtr)_innerField.GetValue(result)!;
        if (resultValue?.ToString() == "Ok" && innerPtr != IntPtr.Zero)
            return Marshal.PtrToStringUTF8(innerPtr);
        return null;
    }

    string? GetNativeError(object? result)
    {
        if (result == null) return null;
        var resultValue = _resultField.GetValue(result);
        var innerPtr = (IntPtr)_innerField.GetValue(result)!;
        if (resultValue?.ToString() == "Err" && innerPtr != IntPtr.Zero)
            return Marshal.PtrToStringUTF8(innerPtr);
        return null;
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
    [JsonPropertyName("issued_supply")] public long IssuedSupply { get; set; }
    [JsonPropertyName("balance")] public AssetBalanceResponse? Balance { get; set; }
}

class AssetBalanceResponse
{
    [JsonPropertyName("settled")] public long Settled { get; set; }
    [JsonPropertyName("future")] public long Future { get; set; }
    [JsonPropertyName("spendable")] public long Spendable { get; set; }
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
    [JsonPropertyName("amount")] public long Amount { get; set; }
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
        "waitingcounterparty" => 0,
        "waitingconfirmations" => 1,
        "settled" => 3,
        "failed" => 4,
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
    [JsonPropertyName("issued_supply")] public long IssuedSupply { get; set; }
}

class GenerateKeysResponse
{
    [JsonPropertyName("mnemonic")] public string? Mnemonic { get; set; }
    [JsonPropertyName("xpub")] public string? Xpub { get; set; }
    [JsonPropertyName("account_xpub_vanilla")] public string? AccountXpubVanilla { get; set; }
    [JsonPropertyName("account_xpub_colored")] public string? AccountXpubColored { get; set; }
    [JsonPropertyName("master_fingerprint")] public string? MasterFingerprint { get; set; }
}
