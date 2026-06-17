using System.Collections.Concurrent;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RGBWalletService : IRGBWalletService
{
    readonly IRgbLibService _rgbLib;
    readonly RGBPluginDbContextFactory _db;
    readonly RGBConfiguration _cfg;
    readonly MnemonicProtectionService _mnemonicProtection;
    readonly RgbWalletSignerProvider _signerProvider;
    readonly ILogger<RGBWalletService> _log;
    readonly CurrencyNameTable _currencyNameTable;
    readonly EventAggregator _events;
    readonly ConcurrentDictionary<string, string> _addressCache = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();

    public RGBWalletService(
        IRgbLibService rgbLib,
        RGBPluginDbContextFactory db,
        RGBConfiguration cfg,
        MnemonicProtectionService mnemonicProtection,
        RgbWalletSignerProvider signerProvider,
        CurrencyNameTable currencyNameTable,
        EventAggregator events,
        ILogger<RGBWalletService> log)
    {
        _rgbLib = rgbLib;
        _db = db;
        _cfg = cfg;
        _mnemonicProtection = mnemonicProtection;
        _signerProvider = signerProvider;
        _currencyNameTable = currencyNameTable;
        _events = events;
        _log = log;
    }

    public const int MinAllocationsPerUtxo = 1;
    public const int MaxAllocationsPerUtxoLimit = 50;
    public const int DefaultAllocationsPerUtxo = 10;

    public static int ResolveAllocationsPerUtxo(int? requested) =>
        requested is > 0
            ? Math.Clamp(requested.Value, MinAllocationsPerUtxo, MaxAllocationsPerUtxoLimit)
            : DefaultAllocationsPerUtxo;

    public async Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork;
        var keys = _rgbLib.GenerateKeys(walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(keys.Mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo)
        };

        await using (var ctx = _db.CreateContext())
        {
            ctx.RGBWallets.Add(wallet);
            try { await ctx.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_RGB_Wallets_StoreId", StringComparison.OrdinalIgnoreCase) == true
                || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException("A wallet already exists for this store.");
            }
        }

        _signerProvider.RegisterSigner(wallet.Id, keys.Mnemonic, network);

        _log.LogInformation("created wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork;
        var keys = _rgbLib.RestoreKeysFromMnemonic(mnemonic, walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo)
        };

        await using (var ctx = _db.CreateContext())
        {
            ctx.RGBWallets.Add(wallet);
            try { await ctx.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_RGB_Wallets_StoreId", StringComparison.OrdinalIgnoreCase) == true
                || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException("A wallet already exists for this store.");
            }
        }

        _signerProvider.RegisterSigner(wallet.Id, mnemonic, network);

        try
        {
            await _rgbLib.RefreshAsync(wallet.Id, ct);
            await _rgbLib.GetBtcBalanceAsync(wallet.Id, ct, sync: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post-restore sync failed for wallet {Id}", wallet.Id);
        }

        _log.LogInformation("restored wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet?> GetWalletAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FindAsync([id], ct);
    }

    public async Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FirstOrDefaultAsync(w => w.StoreId == storeId, ct);
    }

    public async Task<string> GetAddressAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        if (_addressCache.TryGetValue(walletId, out var cached))
            return cached;
        var address = await _rgbLib.GetAddressAsync(walletId, ct);
        _addressCache.TryAdd(walletId, address);
        return address;
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: sync);
    }

    public async Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
        return await CreateColorableUtxosInternalAsync(walletId, count, size, ct);
        }
        finally { sendLock.Release(); }
    }

    async Task<int> CreateColorableUtxosInternalAsync(string walletId, int count, int size, CancellationToken ct)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);

        try
        {
            var result = await _rgbLib.CreateUtxosBeginAsync(walletId, count, size, 2.0f, ct);
            if (string.IsNullOrEmpty(result)) return 0;

            var ownAddr = BitcoinAddress.Create(await _rgbLib.GetAddressAsync(walletId, ct), network);
            var psbt = ExtractPsbt(result);
            var signed = await SignPsbtLocallyAsync(walletId, psbt, network,
                new SigningPolicy
                {
                    MaxUnknownOutputSats = 0,
                    MaxFeeSats = EstimateTaprootFee(count, count + 1, 2.0f) * 3,
                    AllowedScripts = new HashSet<Script> { ownAddr.ScriptPubKey },
                    MaxOutputCount = count + 1
                }, ct);
            await _rgbLib.CreateUtxosEndAsync(walletId, signed, ct);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(ex, "UTXOs already available for wallet {WalletId}", walletId);
            return 0;
        }
    }

    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network, SigningPolicy policy, CancellationToken ct = default)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId, ct);
        if (signer == null)
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");

        _log.LogDebug("Signing PSBT locally for wallet {WalletId}", walletId);
        try
        {
            return await signer.SignPsbtAsync(psbt, network, policy, ct);
        }
        catch (ObjectDisposedException)
        {
            throw new InvalidOperationException($"Signer for wallet {walletId} was disposed (wallet may have been deleted). Retry the operation.");
        }
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var assets = await _rgbLib.ListAssetsAsync(walletId, ct);
        await SyncAssetsToDbAsync(walletId, assets, ct);
        return assets;
    }

    public async Task<List<RgbAsset>> ListAssetsRawAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListAssetsAsync(walletId, ct);
    }

    async Task SyncAssetsToDbAsync(string walletId, List<RgbAsset> assets, CancellationToken ct)
    {
        if (assets.Count == 0) return;
        try
        {
            await using var ctx = _db.CreateContext();
            var assetIds = assets.Select(a => a.AssetId).ToList();
            var knownIds = await ctx.RGBAssets
                .Where(a => a.WalletId == walletId && assetIds.Contains(a.AssetId))
                .Select(a => a.AssetId)
                .ToListAsync(ct);
            var newAssets = assets.Where(a => !knownIds.Contains(a.AssetId)).ToList();
            if (newAssets.Count == 0) return;

            foreach (var a in newAssets)
            {
                var (t, n) = NormalizeAssetMetadata(a.Ticker, a.Name);
                ctx.RGBAssets.Add(new RGBAsset
                {
                    AssetId = a.AssetId,
                    WalletId = walletId,
                    Ticker = t,
                    Name = n,
                    Precision = a.Precision,
                    IssuedSupply = a.IssuedSupply,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            await ctx.SaveChangesAsync(ct);
            await _currencyNameTable.ReloadCurrencyData(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to sync assets to DB for wallet {WalletId}", walletId);
        }
    }

    public async Task<bool> RegisterSingleAssetIfNewAsync(string walletId, RgbAsset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(asset.AssetId)) return false;
        var (t, n) = NormalizeAssetMetadata(asset.Ticker, asset.Name);

        await using var ctx = _db.CreateContext();
        var existing = await ctx.RGBAssets.FindAsync([walletId, asset.AssetId], ct);
        if (existing != null) return false;

        ctx.RGBAssets.Add(new RGBAsset
        {
            AssetId = asset.AssetId,
            WalletId = walletId,
            Ticker = t,
            Name = n,
            Precision = asset.Precision,
            IssuedSupply = asset.IssuedSupply,
            CreatedAt = DateTimeOffset.UtcNow
        });
        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return false;
        }

        try { await _currencyNameTable.ReloadCurrencyData(ct); }
        catch (Exception ex) { _log.LogWarning(ex, "ReloadCurrencyData failed after registering asset {AssetId}", asset.AssetId); }

        _events.Publish(new RgbAssetDiscoveredEvent(walletId, asset.AssetId, t, n));
        _log.LogInformation("Auto-registered new asset {AssetId} ({Ticker}) on wallet {WalletId} via blind-receive", asset.AssetId, t, walletId);
        return true;
    }

    static bool IsDuplicateKey(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505", StringComparison.Ordinal)
            || msg.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListUnspentsAsync(walletId, ct);
    }

    public async Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListBtcTransactionsAsync(walletId, ct);
    }

    public async Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var asset = await _rgbLib.IssueAssetNiaAsync(walletId, ticker, name, [amt], precision, ct);

        await using var ctx = _db.CreateContext();
        var existing = await ctx.RGBAssets.FindAsync([walletId, asset.AssetId], ct);
        if (existing == null)
        {
            var (t, n) = NormalizeAssetMetadata(asset.Ticker, asset.Name);
            ctx.RGBAssets.Add(new RGBAsset
            {
                AssetId = asset.AssetId,
                WalletId = walletId,
                Ticker = t,
                Name = n,
                Precision = asset.Precision,
                IssuedSupply = asset.IssuedSupply,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(ct);
            await _currencyNameTable.ReloadCurrencyData(ct);
        }

        return asset;
    }

    public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);

        long? expTs = expiration.HasValue ? DateTimeOffset.UtcNow.Add(expiration.Value).ToUnixTimeSeconds() : null;
        var resp = await _rgbLib.BlindReceiveAsync(walletId, assetId, amount, expTs, minConfirmations, ct);

        var inv = new RGBInvoice
        {
            Id = Guid.NewGuid().ToString(),
            WalletId = walletId,
            BtcPayInvoiceId = btcPayInvoiceId,
            Invoice = resp.Invoice,
            RecipientId = resp.RecipientId,
            AssetId = assetId,
            Amount = amount,
            ExpirationTimestamp = resp.ExpirationTimestamp,
            BatchTransferIdx = resp.BatchTransferIdx,
            Status = RGBInvoiceStatus.Pending,
            IsBlind = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var ctx = _db.CreateContext();
        ctx.RGBInvoices.Add(inv);
        await ctx.SaveChangesAsync(ct);
        return inv;
    }

    public async Task RefreshWalletAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        await _rgbLib.RefreshAsync(walletId, ct);
        await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: true);
    }

    public async Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListTransfersAsync(walletId, assetId, ct);
    }

    const int RgbLibTransferStatusWaitingConfirmations = 1;
    const int RgbLibTransferStatusFailed = 4;

    public async Task<int> CleanupExpiredTransfersAsync(string walletId, string walletNetwork, string masterFingerprint, CancellationToken ct = default)
    {
        var walletDataDir = _rgbLib.GetWalletDataDir(walletId, walletNetwork);
        var dbPath = Path.Combine(walletDataDir, masterFingerprint, "rgb_lib_db");
        if (!File.Exists(dbPath)) return 0;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var connStr = $"Data Source={dbPath}";
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE batch_transfer SET status = {RgbLibTransferStatusFailed} WHERE status = {RgbLibTransferStatusWaitingConfirmations} AND expiration IS NOT NULL AND expiration < @now";
        cmd.Parameters.AddWithValue("@now", now);
        var count = await cmd.ExecuteNonQueryAsync(ct);
        if (count > 0)
        {
            _log.LogInformation("Cleaned up {Count} expired blind receive transfers for wallet {WalletId}", count, walletId);
            try { await _rgbLib.RefreshAsync(walletId, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Post-cleanup refresh failed for wallet {WalletId}", walletId); }
        }
        return count;
    }

    public async Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.BackupWalletAsync(walletId, password, ct);
    }

    internal const string RestoreStagingPrefix = ".restore-staging-";

    public async Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default)
    {
        var walletNetwork = selectedNetwork;
        var keys = _rgbLib.RestoreKeysFromMnemonic(mnemonic, walletNetwork);
        var network = NetworkHelper.GetNetwork(walletNetwork);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(mnemonic),
            Network = walletNetwork,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo)
        };

        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, walletNetwork);
        var parentDir = Path.GetDirectoryName(walletDataDir)!;
        Directory.CreateDirectory(parentDir);

        var stagingDir = Path.Combine(parentDir, $"{RestoreStagingPrefix}{wallet.Id}-{Guid.NewGuid():N}");

        // SECURITY: Backup file is validated before reaching native code:
        // - 5MB upload limit (controller [RequestSizeLimit])
        // - ZIP structure + entry validation (controller ValidateBackupFileHeader)
        // - Post-extraction 50MB size cap (below)
        // - Staging directory isolation: native restore writes to a staging dir,
        //   only moved to final location on success. On timeout, the staging dir
        //   is left for deferred cleanup at next startup — never deleted while
        //   native code may still be writing.
        // Remaining risk: malformed ZIP contents could exploit rgb-lib parser bugs.
        // This requires admin authentication and is accepted risk — fuzzing the
        // native decoder is upstream work (rgb-lib-c-sharp).
        var restoreTask = Task.Run(() => _rgbLib.RestoreBackup(backupPath, password, stagingDir));
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), ct);
        var completed = await Task.WhenAny(restoreTask, timeoutTask);
        if (completed == timeoutTask)
        {
            _log.LogWarning("Backup restore timed out after 30 seconds — staging dir {Dir} left for deferred cleanup", stagingDir);
            throw new InvalidOperationException("Backup restore timed out after 30 seconds");
        }
        await restoreTask;

        var dirSize = new DirectoryInfo(stagingDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
        if (dirSize > 50 * 1024 * 1024)
        {
            try { Directory.Delete(stagingDir, true); }
            catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up oversized staging dir {Dir}", stagingDir); }
            throw new InvalidOperationException(
                $"Restored wallet data exceeds size limit ({dirSize / 1024 / 1024}MB > 50MB)");
        }

        var expectedFingerprint = wallet.MasterFingerprint?.ToLowerInvariant();
        var stagingFingerprintDirs = Directory.GetDirectories(stagingDir)
            .Select(d => Path.GetFileName(d).ToLowerInvariant())
            .Where(name => name.Length == 8 && name.All(c => "0123456789abcdef".Contains(c)))
            .ToList();

        if (stagingFingerprintDirs.Count > 0 && !string.IsNullOrEmpty(expectedFingerprint)
            && !stagingFingerprintDirs.Contains(expectedFingerprint))
        {
            _log.LogError("Mnemonic/backup mismatch: user mnemonic derives fingerprint {Expected} but backup contains {Found}",
                expectedFingerprint, string.Join(",", stagingFingerprintDirs));
            try { Directory.Delete(stagingDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up staging dir after fingerprint mismatch"); }
            throw new InvalidOperationException(
                "Backup could not be loaded with the supplied mnemonic. The mnemonic does not match the keys in this backup.");
        }

        try
        {
            Directory.Move(stagingDir, walletDataDir);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to move staging dir {Staging} to {Final}", stagingDir, walletDataDir);
            try { Directory.Delete(stagingDir, true); }
            catch { }
            throw new InvalidOperationException("Failed to finalize restored wallet data");
        }

        try
        {
            await using var ctx = _db.CreateContext();
            ctx.RGBWallets.Add(wallet);
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_RGB_Wallets_StoreId", StringComparison.OrdinalIgnoreCase) == true
            || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
        {
            try { Directory.Delete(walletDataDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after duplicate wallet detection", walletDataDir); }
            throw new InvalidOperationException("A wallet already exists for this store. Delete it first if you want to restore a different one.");
        }
        catch
        {
            try { Directory.Delete(walletDataDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after DB save failure", walletDataDir); }
            throw;
        }

        try
        {
            await _rgbLib.GetOrCreateWalletAsync(wallet.Id, ct);
            await _rgbLib.GetAddressAsync(wallet.Id, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Mnemonic/backup consistency check failed for wallet {Id}", wallet.Id);
            try
            {
                await using var ctx = _db.CreateContext();
                ctx.RGBWallets.Remove(wallet);
                await ctx.SaveChangesAsync(ct);
            }
            catch (Exception dbEx) { _log.LogDebug(dbEx, "Failed to roll back wallet row {Id}", wallet.Id); }
            try { _rgbLib.UnloadWallet(wallet.Id); } catch { }
            try { Directory.Delete(walletDataDir, true); }
            catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after consistency check failure", walletDataDir); }
            throw new InvalidOperationException(
                "Backup could not be loaded with the supplied mnemonic. The mnemonic does not match the keys in this backup.");
        }

        _signerProvider.RegisterSigner(wallet.Id, mnemonic, network);

        try
        {
            await _rgbLib.RefreshAsync(wallet.Id, ct);
            await _rgbLib.GetBtcBalanceAsync(wallet.Id, ct, sync: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post-restore sync failed for wallet {Id}", wallet.Id);
        }

        _log.LogInformation("restored wallet {Id} from backup for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task DeleteWalletAsync(string walletId, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);

        _rgbLib.UnloadWallet(walletId);
        _signerProvider.UnloadSigner(walletId);
        _addressCache.TryRemove(walletId, out _);
        _sendLocks.TryRemove(walletId, out _);

        await using var ctx = _db.CreateContext();
        ctx.RGBWallets.Remove(wallet);
        await ctx.SaveChangesAsync(ct);

        _log.LogInformation("deleted wallet {Id}, data dir left at {Dir}",
            walletId, _rgbLib.GetWalletDataDir(walletId, wallet.Network));
    }

    public async Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
        return await SendBtcInternalAsync(walletId, destinationAddress, amountSats, feeRate, ct);
        }
        finally { sendLock.Release(); }
    }

    async Task<(string Txid, long AmountSent, long Fee)> SendBtcInternalAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);

        var destAddr = BitcoinAddress.Create(destinationAddress, network);

        var unspents = await _rgbLib.ListUnspentsAsync(walletId, ct);
        var spendableUtxos = unspents
            .Where(u => u.Utxo.BtcAmount > 0 && u.RgbAllocations.Count == 0 && !u.Utxo.Colorable)
            .OrderByDescending(u => u.Utxo.BtcAmount)
            .ToList();

        if (spendableUtxos.Count == 0)
            throw new InvalidOperationException("No spendable UTXOs available (all UTXOs have RGB allocations)");

        var selected = new List<UnspentOutput>();
        long totalInput = 0;
        foreach (var utxo in spendableUtxos)
        {
            selected.Add(utxo);
            totalInput += utxo.Utxo.BtcAmount;
            if (totalInput >= amountSats + EstimateTaprootFee(selected.Count, 2, feeRate))
                break;
        }

        var minFee = EstimateTaprootFee(selected.Count, 1, feeRate);
        if (amountSats == totalInput)
        {
            amountSats = totalInput - minFee;
            if (amountSats < 546)
                throw new InvalidOperationException("Amount after fee would be below dust limit (546 sats)");
        }
        else if (totalInput < amountSats + minFee)
        {
            var maxSendable = totalInput - minFee;
            throw new InvalidOperationException(
                $"Insufficient funds after fee. Maximum sendable: {maxSendable:N0} sats (from {totalInput:N0} sats, fee ~{minFee:N0} sats)");
        }

        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
        var allowsPlainElectrum = NetworkSettings.AllowsPlainElectrum(wallet.Network);
        using var electrum = BitcoinChainClientFactory.Create(networkSettings.ElectrumUrl, allowInsecure: allowsPlainElectrum);
        await electrum.ConnectAsync(ct);

        var rawTxCache = new Dictionary<string, Transaction>();
        foreach (var utxo in selected)
        {
            if (!rawTxCache.ContainsKey(utxo.Utxo.Outpoint.Txid))
            {
                var expectedTxid = utxo.Utxo.Outpoint.Txid;
                var rawHex = await electrum.GetRawTransactionAsync(expectedTxid, ct);
                var rawTx = Transaction.Parse(rawHex, network);
                if (rawTx.GetHash().ToString() != expectedTxid)
                    throw new InvalidOperationException(
                        $"Electrum returned transaction with wrong txid: expected {expectedTxid}, got {rawTx.GetHash()}");
                rawTxCache[expectedTxid] = rawTx;
            }
        }

        var changeAddress = BitcoinAddress.Create(
            await _rgbLib.GetAddressAsync(walletId, ct), network);

        var fee = EstimateTaprootFee(selected.Count, 2, feeRate);
        var change = totalInput - amountSats - fee;
        var hasChange = change >= 546;
        if (!hasChange)
            fee = totalInput - amountSats;

        var tx = Transaction.Create(network);
        foreach (var utxo in selected)
        {
            tx.Inputs.Add(new TxIn(new OutPoint(
                uint256.Parse(utxo.Utxo.Outpoint.Txid), utxo.Utxo.Outpoint.Vout)));
        }

        tx.Outputs.Add(new TxOut(Money.Satoshis(amountSats), destAddr.ScriptPubKey));
        if (hasChange)
            tx.Outputs.Add(new TxOut(Money.Satoshis(change), changeAddress.ScriptPubKey));

        var psbt = tx.CreatePSBT(network);

        var signer = await _signerProvider.GetSignerAsync(walletId, ct)
            ?? throw new InvalidOperationException("No local signer available");

        for (int i = 0; i < selected.Count; i++)
        {
            var utxo = selected[i];
            var prevTx = rawTxCache[utxo.Utxo.Outpoint.Txid];
            var prevOut = prevTx.Outputs[utxo.Utxo.Outpoint.Vout];
            psbt.Inputs[i].WitnessUtxo = prevOut;
        }

        var policy = new SigningPolicy
        {
            ExpectedDestination = destinationAddress,
            ExpectedAmountSats = amountSats,
            MaxFeeSats = fee,
            AllowedScripts = new HashSet<Script> { changeAddress.ScriptPubKey },
            MaxOutputCount = hasChange ? 2 : 1,
            StrictAllowedScriptsOnly = true
        };

        var signedBase64 = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy, ct);
        psbt = PSBT.Parse(signedBase64, network);

        var signedTx = psbt.ExtractTransaction();
        var localTxid = signedTx.GetHash().ToString();
        var broadcastTxid = await electrum.BroadcastTransactionAsync(signedTx.ToHex(), ct);
        if (!string.Equals(broadcastTxid, localTxid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Broadcast returned mismatched txid: expected {localTxid}, got {broadcastTxid}");
        var txid = localTxid;

        _log.LogInformation("Sent {Amount} sats to {Address}, txid={Txid}, fee={Fee}",
            amountSats, destinationAddress, txid, fee);

        try { await _rgbLib.RefreshAsync(walletId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send refresh failed"); }

        return (txid, amountSats, fee);
    }

    public async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? BroadcastWarning)> SendAssetAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
        return await SendAssetInternalAsync(walletId, rgbInvoice, assetId, amount, feeRate, ct);
        }
        finally { sendLock.Release(); }
    }

    async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? BroadcastWarning)> SendAssetInternalAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(wallet.Network);
        var allowPrivateEndpoints = wallet.Network.Equals("regtest", StringComparison.OrdinalIgnoreCase)
            && _cfg.AllowPrivateTransportEndpoints;

        var invoiceData = _rgbLib.DecodeInvoice(rgbInvoice);
        EnsureInvoiceNetworkMatchesWallet(invoiceData.Network, wallet.Network);
        var assets = await _rgbLib.ListAssetsAsync(walletId, ct);
        var (resolvedAssetId, asset) = ValidateSendAssetRequest(invoiceData, assetId, amount, assets);

        var pinnedEndpoints = await TransportEndpointValidator.ValidateAsync(invoiceData.TransportEndpoints, allowPrivateEndpoints, ct);

        var recipientMap = JsonSerializer.Serialize(new Dictionary<string, object[]>
        {
            [resolvedAssetId] = [new
            {
                recipient_id = invoiceData.RecipientId,
                witness_data = (object?)null,
                assignment = new { Fungible = amount },
                transport_endpoints = pinnedEndpoints
            }]
        });

        _log.LogInformation("SendAsset: {Ticker} amount={Amount} to {RecipientId}",
            asset.Ticker, amount, invoiceData.RecipientId[..Math.Min(30, invoiceData.RecipientId.Length)]);

        var sendBeginResult = await _rgbLib.SendBeginAsync(walletId, recipientMap, feeRate, 1, ct);

        string signedPsbt;
        try
        {
            var unsignedPsbt = ExtractPsbt(sendBeginResult);
            var changeAddr = BitcoinAddress.Create(await _rgbLib.GetAddressAsync(walletId, ct), network);
            signedPsbt = await SignPsbtLocallyAsync(walletId, unsignedPsbt, network,
                new SigningPolicy
                {
                    MaxUnknownOutputSats = 0,
                    MaxFeeSats = EstimateTaprootFee(3, 3, feeRate) * 3,
                    AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey },
                    MaxOutputCount = 10
                }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SendAsset: signing failed after SendBegin for wallet {WalletId} — reloading wallet to reset rgb-lib state", walletId);
            try
            {
                _rgbLib.UnloadWallet(walletId);
                await _rgbLib.GetOrCreateWalletAsync(walletId, ct);
            }
            catch (Exception reloadEx) { _log.LogWarning(reloadEx, "Failed to reload wallet {WalletId} after signing failure", walletId); }
            throw;
        }

        var txid = await _rgbLib.SendEndAsync(walletId, signedPsbt, ct);

        string? broadcastWarning = null;
        try
        {
            var psbtObj = PSBT.Parse(signedPsbt, network);
            psbtObj.TryFinalize(out _);
            var rawTx = psbtObj.ExtractTransaction();

            var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
            var allowsPlainElectrum = NetworkSettings.AllowsPlainElectrum(wallet.Network);
            using var electrum = BitcoinChainClientFactory.Create(networkSettings.ElectrumUrl, allowInsecure: allowsPlainElectrum);
            await electrum.ConnectAsync(ct);
            await electrum.BroadcastTransactionAsync(rawTx.ToHex(), ct);
        }
        catch (Exception ex)
        {
            broadcastWarning = "RGB state committed but transaction broadcast failed. It may need to be rebroadcast manually.";
            _log.LogError(ex, "SendAsset: broadcast failed for txid={Txid}. RGB state committed but tx may not be on chain.", txid);
        }

        _log.LogInformation("SendAsset completed: {Ticker} amount={Amount}, txid={Txid}",
            asset.Ticker, amount, txid);

        try { await _rgbLib.RefreshAsync(walletId, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send-asset refresh failed"); }

        return (txid, amount, resolvedAssetId, asset.Ticker, broadcastWarning);
    }

    static bool IsTaproot(Script script)
    {
        var bytes = script.ToBytes();
        return bytes.Length == 34 && bytes[0] == 0x51 && bytes[1] == 0x20;
    }

    internal static void EnsureInvoiceNetworkMatchesWallet(string invoiceNetwork, string walletNetwork)
    {
        var expectedRgbNetwork = NetworkHelper.MapNetworkToRgbLibFormat(walletNetwork);
        if (!string.Equals(invoiceNetwork, expectedRgbNetwork, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"RGB invoice network '{invoiceNetwork}' does not match wallet network '{expectedRgbNetwork}'.");
    }

    internal static (string ResolvedAssetId, RgbAsset Asset) ValidateSendAssetRequest(
        RgbInvoiceData invoiceData, string assetId, long amount, List<RgbAsset> walletAssets)
    {
        if (invoiceData.ExpirationTimestamp > 0
            && DateTimeOffset.FromUnixTimeSeconds(invoiceData.ExpirationTimestamp) < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("This RGB invoice has expired");

        var resolvedAssetId = invoiceData.AssetId ?? assetId;
        if (string.IsNullOrEmpty(resolvedAssetId))
            throw new InvalidOperationException("Asset ID must be provided — the invoice does not specify one");

        if (invoiceData.AssetId != null && !string.IsNullOrEmpty(assetId)
            && invoiceData.AssetId != assetId)
            throw new InvalidOperationException(
                $"Invoice requires a different asset than the one you selected");

        if (invoiceData.Amount.HasValue && invoiceData.Amount.Value != amount)
            throw new InvalidOperationException(
                $"Invoice requires exactly {invoiceData.Amount.Value:N0} — you entered {amount:N0}");

        var asset = walletAssets.FirstOrDefault(a => a.AssetId == resolvedAssetId);
        if (asset == null)
            throw new InvalidOperationException($"Asset {resolvedAssetId[..Math.Min(20, resolvedAssetId.Length)]}... not found in wallet");

        if (asset.SpendableBalance < amount)
            throw new InvalidOperationException(
                $"Insufficient {asset.Ticker} spendable balance: have {asset.SpendableBalance:N0}, need {amount:N0}");

        return (resolvedAssetId, asset);
    }

    internal static long EstimateTaprootFee(int numInputs, int numOutputs, float feeRate)
    {
        var vsize = 10.5 + numInputs * 57.5 + numOutputs * 43.0;
        return (long)Math.Ceiling(vsize * feeRate);
    }

    async Task<RGBWallet> GetWalletOrThrow(string id, CancellationToken ct = default) =>
        await GetWalletAsync(id, ct) ?? throw new KeyNotFoundException($"wallet {id} not found");

    static string ExtractPsbt(string nativeResult)
    {
        if (!nativeResult.TrimStart().StartsWith('{'))
            return nativeResult;

        var json = JsonSerializer.Deserialize<JsonElement>(nativeResult);
        if (json.TryGetProperty("psbt", out var psbtProp) && psbtProp.GetString() is { } psbt)
            return psbt;

        throw new RgbLibException("Unexpected response format from rgb-lib");
    }

    internal static (string Ticker, string Name) NormalizeAssetMetadata(string? ticker, string? name)
    {
        return (Truncate(StripControlChars(ticker ?? ""), 32),
                Truncate(StripControlChars(name ?? ""), 64));
    }

    static string StripControlChars(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var buf = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch >= 0x20 && ch != 0x7F) buf.Append(ch);
        }
        return buf.ToString();
    }

    static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        var cut = maxChars;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s.Substring(0, cut);
    }
}



