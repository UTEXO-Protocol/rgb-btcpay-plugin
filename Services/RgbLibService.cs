using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    readonly FieldInfo _onlineField;
    readonly FieldInfo _resultField;
    readonly FieldInfo _innerField;
    
    readonly MethodInfo _blindReceiveMethod;
    readonly MethodInfo _listUnspentsMethod;
    readonly MethodInfo _createUtxosBeginMethod;
    readonly MethodInfo _createUtxosEndMethod;
    readonly MethodInfo _refreshMethod;
    
    bool _disposed;

    public RgbLibService(
        RGBConfiguration config,
        RGBPluginDbContextFactory db,
        ILogger<RgbLibService> log)
    {
        _config = config;
        _db = db;
        _log = log;
        Directory.CreateDirectory(config.RgbDataDir);
        
        var assembly = typeof(RgbLibWallet).Assembly;
        _nativeMethodsType = assembly.GetType("RgbLib.NativeMethods")!;
        _cResultStringType = assembly.GetType("RgbLib.CResultString")!;
        
        _walletField = typeof(RgbLibWallet).GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _onlineField = typeof(RgbLibWallet).GetField("_online", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _resultField = _cResultStringType.GetField("result")!;
        _innerField = _cResultStringType.GetField("inner")!;
        
        _blindReceiveMethod = _nativeMethodsType.GetMethod("rgblib_blind_receive")!;
        _listUnspentsMethod = _nativeMethodsType.GetMethod("rgblib_list_unspents")!;
        _createUtxosBeginMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_begin")!;
        _createUtxosEndMethod = _nativeMethodsType.GetMethod("rgblib_create_utxos_end")!;
        _refreshMethod = _nativeMethodsType.GetMethod("rgblib_refresh")!;
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
                dbWallet.MaxAllocationsPerUtxo)));

        return lazyWallet.Value;
    }

    RgbLibWalletHandle CreateWalletInternal(string walletId, string xpubVanilla, string xpubColored, string masterFingerprint, string walletNetwork, int maxAllocationsPerUtxo)
    {
        _log.LogInformation("Lazy loading wallet {WalletId} on network {Network} with max_allocations={MaxAlloc}", walletId, walletNetwork, maxAllocationsPerUtxo);

        var dataDir = Path.Combine(_config.RgbDataDir, walletId);
        Directory.CreateDirectory(dataDir);
        
        var networkSettings = _config.GetNetworkSettings(walletNetwork);

        var walletConfig = new Dictionary<string, object?>
        {
            ["data_dir"] = dataDir,
            ["bitcoin_network"] = NetworkHelper.MapNetworkToRgbLibFormat(walletNetwork),
            ["database_type"] = "Sqlite",
            ["max_allocations_per_utxo"] = maxAllocationsPerUtxo,
            ["account_xpub_vanilla"] = xpubVanilla,
            ["account_xpub_colored"] = xpubColored,
            ["mnemonic"] = null,
            ["master_fingerprint"] = masterFingerprint,
            ["vanilla_keychain"] = (int?)null,
            ["supported_schemas"] = new[] { "Nia", "Cfa" }
        };
        
        var configJson = JsonSerializer.Serialize(walletConfig);
        var wallet = new RgbLibWallet(configJson);
        wallet.GoOnline(networkSettings.ElectrumUrl, true);

        _log.LogInformation("Wallet {WalletId} connected to {Electrum}", walletId, networkSettings.ElectrumUrl);
        return new RgbLibWalletHandle(wallet, walletId);
    }

    public void UnloadWallet(string walletId)
    {
        if (_wallets.TryRemove(walletId, out var lazy))
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
                _log.LogInformation("Wallet {WalletId} unloaded", walletId);
            }
        }
    }

    public async Task<string> GetAddressAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            return wallet.GetAddress();
        }, ct);
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var balanceJson = wallet.GetBtcBalance(false);
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
                IssuedSupply = a.IssuedSupply
            }).ToList() ?? [];
        }, ct);
    }

    public async Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        var assignment = amount.HasValue 
            ? JsonSerializer.Serialize(new { Fungible = amount.Value })
            : "{\"NonFungible\":null}";
        
        var duration = expiration.HasValue 
            ? (expiration.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString()
            : "3600";
        
        var transportEndpoints = JsonSerializer.Serialize(new[] { _config.ProxyEndpoint });
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var args = new object?[] { walletStruct, assetId, assignment, duration, transportEndpoints, "1" };
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
            var onlineStruct = _onlineField.GetValue(wallet)!;
            
            var args = new object?[] { walletStruct, onlineStruct, false, false };
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

    public async Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default)
    {
        var handle = await GetOrCreateWalletAsync(walletId, ct);
        
        return await handle.ExecuteAsync(wallet =>
        {
            ct.ThrowIfCancellationRequested();
            var walletStruct = _walletField.GetValue(wallet)!;
            var onlineStruct = _onlineField.GetValue(wallet)!;
            
            var args = new object?[] { walletStruct, onlineStruct, true, count.ToString(), size.ToString(), ((int)feeRate).ToString(), false };
            var result = _createUtxosBeginMethod.Invoke(null, args);
            
            _walletField.SetValue(wallet, args[0]);
            _onlineField.SetValue(wallet, args[1]);
            
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
            var onlineStruct = _onlineField.GetValue(wallet)!;
            
            var args = new object?[] { walletStruct, onlineStruct, signedPsbt.Trim('"'), false };
            var result = _createUtxosEndMethod.Invoke(null, args);
            
            _walletField.SetValue(wallet, args[0]);
            _onlineField.SetValue(wallet, args[1]);
            
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
        
        var dbPath = Path.Combine(_config.RgbDataDir, walletId, dbWallet.MasterFingerprint, "rgb_lib_db");
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
            SELECT t.idx, bt.status, t.recipient_id, bt.txid, t.incoming
            FROM transfer t
            JOIN asset_transfer at ON t.asset_transfer_idx = at.idx
            JOIN batch_transfer bt ON at.batch_transfer_idx = bt.idx
            WHERE at.asset_id = @assetId";
        cmd.Parameters.AddWithValue("@assetId", assetId);
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            transfers.Add(new RgbTransfer
            {
                Idx = reader.GetInt32(0),
                Status = reader.GetInt32(1),
                RecipientId = reader.IsDBNull(2) ? null : reader.GetString(2),
                Txid = reader.IsDBNull(3) ? null : reader.GetString(3),
                Kind = reader.GetBoolean(4) ? 2 : 3
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
            var onlineStruct = _onlineField.GetValue(wallet)!;
            
            var args = new object?[] { walletStruct, onlineStruct, null, "[]", false };
            _refreshMethod.Invoke(null, args);
            
            _walletField.SetValue(wallet, args[0]);
            _onlineField.SetValue(wallet, args[1]);
        }, ct);
        
        InvalidateWalletCache(walletId);
    }
    
    void InvalidateWalletCache(string walletId)
    {
        if (_wallets.TryRemove(walletId, out var lazy) && lazy.IsValueCreated)
        {
            lazy.Value.Dispose();
        }
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
        "settled" => 2,
        "failed" => 3,
        _ => int.TryParse(s, out var n) ? n : -1
    };
    
    static int ParseKind(string? s) => s?.ToLowerInvariant() switch
    {
        "issuance" => 0,
        "receiveincoming" or "receive_incoming" => 1,
        "receiveblind" or "receive_blind" => 2,
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
