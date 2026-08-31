using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
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
    readonly SendLockCoordinator _sendCoordinator;
    static readonly SemaphoreSlim _restoreGate = new(1, 1);
    // Static for the same reason _restoreGate is: both bound a process-wide resource, so per-instance
    // state would be defeated by anything that resolves a second RGBWalletService.
    static RestoreCooldownGate? _restoreCooldown;
    readonly RestoreExecutor _restoreExecutor;
    readonly INativeSendProcessRunner _nativeSendRunner;

    public RGBWalletService(
        IRgbLibService rgbLib,
        RGBPluginDbContextFactory db,
        RGBConfiguration cfg,
        MnemonicProtectionService mnemonicProtection,
        RgbWalletSignerProvider signerProvider,
        CurrencyNameTable currencyNameTable,
        EventAggregator events,
        ILogger<RGBWalletService> log,
        RestoreExecutor restoreExecutor,
        INativeSendProcessRunner nativeSendRunner)
    {
        _rgbLib = rgbLib;
        _db = db;
        _cfg = cfg;
        _mnemonicProtection = mnemonicProtection;
        _signerProvider = signerProvider;
        _currencyNameTable = currencyNameTable;
        _events = events;
        _log = log;
        _restoreExecutor = restoreExecutor;
        _nativeSendRunner = nativeSendRunner;
        _sendCoordinator = new SendLockCoordinator(
            _sendLocks, SetNeedsRecoveryAsync, ClearNeedsRecoveryAsync, id => _rgbLib.UnloadWallet(id),
            FsyncStockAsync);
    }

    // internal, not private: the sweep's non-blocking acquisition (CleanupExpiredTransfersAsync,
    // RefreshWalletAsync) is only observable from a test that can hold the same per-wallet semaphore the
    // coordinator uses, so the tests take it through here via InternalsVisibleTo. It must return the LIVE
    // instance out of _sendLocks: a fresh SemaphoreSlim would leave the test holding a lock the coordinator
    // never consults, so the test would not be exercising the skip path at all. A production caller that
    // acquires the lock this way owes the wallet a write-ahead; there is no single convention to copy, because
    // the direct acquisition sites in this file pair with several different write-ahead treatments — one uses
    // WriteAheadInlineAsync, one hand-rolls set/clear, two rely on the row being born quarantined and two do
    // nothing (audit H2c-lite D4/R1).
    internal SemaphoreSlim SendLockFor(string walletId)
        => _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));

    // Returns whether THIS call set the flag, which is what lets the coordinator restore the pre-operation
    // state rather than discharge a quarantine it never established. False means the wallet was already
    // quarantined on entry; the polarity is pinned, because inverting it defeats the write-ahead entirely.
    async Task<bool> SetNeedsRecoveryAsync(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var w = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"wallet {walletId} not found");
        if (w.NeedsRecovery) return false;
        w.NeedsRecovery = true;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    async Task ClearNeedsRecoveryAsync(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var w = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"wallet {walletId} not found");

        // Nothing to make durable if the flag is already clear, and RefreshWalletAsync now discharges inside
        // the coordinator's delegate, so the coordinator's own clear reaches this method with the flag already
        // false on every healthy poll — fsyncing first would cost three file syncs per wallet per cycle.
        if (!w.NeedsRecovery) return;

        // Fsync the Stock .dat files BEFORE clearing so that cleared implies the accepted
        // state is durable on disk; a crash after fsync but before the commit only re-quarantines.
        RgbStockDurability.FsyncStockDats(RgbStockDurability.ResolveStockDir(
            _rgbLib.GetWalletDataDir(walletId, w.Network), w.MasterFingerprint));

        w.NeedsRecovery = false;
        await ctx.SaveChangesAsync(ct);
    }

    // The coordinator calls this instead of the clear when the write-ahead did NOT set the flag. It exists
    // because ClearNeedsRecoveryAsync was the only fsync on that path, so making the clear conditional would
    // silently drop it. The operation on that path MAY have mutated the Stock — IssueAssetAsync does, and
    // commits its RGBAssets row after the coordinator returns, so without this a crash could leave an asset
    // row whose Stock issuance never reached disk; the cleanup sweep's zero-row and missing-file exits touch
    // nothing, and for them this is simply a cheap no-op. The coordinator cannot tell the two apart, which is
    // why the barrier is unconditional rather than per-caller. Must not swallow: on the
    // paths where the flag is still set, propagating leaves it set, which is the safe direction.
    //
    // Two callers reach here and they differ. From IssueAssetAsync or the cleanup sweep the quarantine is
    // still set and this is the operation's only durability barrier. From RefreshWalletAsync the delegate's
    // own ClearNeedsRecoveryAsync has already fsynced and discharged, so this is a redundant second fsync —
    // harmless, once per wallet per poll, and the price of the coordinator not needing to know which is which.
    async Task FsyncStockAsync(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var w = await ctx.RGBWallets.FindAsync([walletId], ct)
            ?? throw new KeyNotFoundException($"wallet {walletId} not found");

        RgbStockDurability.FsyncStockDats(RgbStockDurability.ResolveStockDir(
            _rgbLib.GetWalletDataDir(walletId, w.Network), w.MasterFingerprint));

        _log.LogDebug("Fsynced Stock for wallet {WalletId} on the write-ahead path that does not discharge", walletId);
    }

    async Task<bool> IsNeedsRecoveryAsync(string walletId, CancellationToken ct)
    {
        await using var ctx = _db.CreateContext();
        var w = await ctx.RGBWallets.FindAsync([walletId], ct);
        return w?.NeedsRecovery ?? true;
    }

    public const int MinAllocationsPerUtxo = RgbConfigBounds.AllocationsPerUtxoMin;
    public const int MaxAllocationsPerUtxoLimit = RgbConfigBounds.AllocationsPerUtxoMax;
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
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo),
            NeedsRecovery = true
        };

        // Born-quarantined: hold the send lock BEFORE the row becomes visible so a racing send
        // both blocks and observes NeedsRecovery=true; the reconciling refresh clears it on success.
        var sendLock = _sendLocks.GetOrAdd(wallet.Id, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
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
                await ClearNeedsRecoveryAsync(wallet.Id, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Post-restore sync failed for wallet {Id} — left quarantined", wallet.Id);
                try { _rgbLib.UnloadWallet(wallet.Id); } catch { }
            }
        }
        finally { sendLock.Release(); }

        _log.LogInformation("restored wallet {Id} for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    public async Task<RGBWallet?> GetWalletAsync(string id, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FindAsync([id], ct);
    }

    // Extracted rather than inlined so tests 37b/37c can pin the REAL predicate. A ToQueryString()
    // test over a re-authored lambda proves nothing about production — it stays green if this method
    // later drops the WalletId filter, which is the false-ACCEPT 37b exists to catch. This mirrors
    // ActivePendingInvoicePredicate from finding C, and ReplenishPredicateTests works for exactly
    // this reason.
    internal static Expression<Func<RGBAsset, bool>> AssetPredicate(string walletId, string assetId)
        => a => a.WalletId == walletId && a.AssetId == assetId;

    public async Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBAssets.FirstOrDefaultAsync(AssetPredicate(walletId, assetId), ct);
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
        => await CreateColorableUtxosWithAuthorizationAsync(walletId, count, size, null, ct);

    // The listener uses this path so its store/payment-method decision is revalidated after waiting for
    // the wallet send lock and acquiring the cross-process lease. The public method remains the explicit
    // merchant/admin path and intentionally requires no automatic-replenishment authorization.
    internal async Task<int> CreateColorableUtxosAutomaticallyAsync(
        string walletId, int count, int size,
        Func<CancellationToken, Task<bool>> authorize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        return await CreateColorableUtxosWithAuthorizationAsync(walletId, count, size, authorize, ct);
    }

    internal static void EnsureStandingColorableRoom(
        int standingColorable, int requested, int manualCeiling)
    {
        var ceilingNeverBelowOneBatch = Math.Max(manualCeiling, requested);
        if ((long)standingColorable + requested <= ceilingNeverBelowOneBatch) return;
        throw new RgbColorableUtxoCeilingReachedException(
            $"refusing to create {requested} more colorable UTXOs: this wallet already holds "
            + $"{standingColorable} and the manual ceiling is {ceilingNeverBelowOneBatch}. Colorable "
            + "UTXOs cannot be spent by this plugin's BTC send, so each extra one parks vanilla BTC "
            + "beyond its reach. Spend the colorable UTXOs this wallet already holds on RGB sends, or "
            + $"raise the manual ceiling to at least {(long)standingColorable + requested} by setting "
            + "the RGB_MAX_MANUAL_COLORABLE_UTXOS environment variable (or max_manual_colorable_utxos "
            + "in rgb.json) and restarting BTCPay Server. This ceiling belongs to this button alone: "
            + "RGB_MAX_AUTO_COLORABLE_UTXOS bounds automatic creation only, and setting it to 0 to stop "
            + "unattended signing never blocks manual provisioning.");
    }

    async Task<int> CreateColorableUtxosWithAuthorizationAsync(
        string walletId, int count, int size,
        Func<CancellationToken, Task<bool>>? authorize,
        CancellationToken ct)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
            var isManualOperatorPath = authorize == null;
            if (isManualOperatorPath)
                EnsureStandingColorableRoom(
                    (await _rgbLib.ListUnspentsAsync(walletId, ct)).Count(u => u.Utxo.Colorable),
                    count, _cfg.MaxManualColorableUtxos);

            var wallet = await GetWalletOrThrow(walletId, ct);
            var walletDir = Path.Combine(
                _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network), wallet.MasterFingerprint);
            using var operationLease = AcquireNativeSendParentLease(walletDir);
            try { return await CreateColorableUtxosInternalAsync(walletId, count, size, authorize, ct); }
            finally { operationLease.ClearActiveMarker(walletDir); }
        }
        finally { sendLock.Release(); }
    }

    async Task<int> CreateColorableUtxosInternalAsync(
        string walletId, int count, int size,
        Func<CancellationToken, Task<bool>>? authorize,
        CancellationToken ct)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        // A quarantined wallet is one whose Stock and rgb-lib database may disagree, because send_end writes
        // both together — so refuse to sign a transaction spending its UTXOs until a refresh has reconciled
        // it. This sits after GetWalletOrThrow so an unknown wallet still surfaces KeyNotFoundException, and
        // it is the authoritative refusal: the listener's eligibility pre-filter only avoids the rgb-lib work.
        if (await IsNeedsRecoveryAsync(walletId, ct))
            throw new RgbWalletQuarantinedException(
                "wallet is quarantined pending recovery — refusing to create UTXOs");
        var network = NetworkHelper.GetNetwork(wallet.Network);

        try
        {
            var ownAddr = BitcoinAddress.Create(await _rgbLib.GetAddressAsync(walletId, ct), network);
            var policy = new SigningPolicy
            {
                MaxUnknownOutputSats = 0,
                MaxFeeSats = CreateUtxosMaxFeeSatsAtOneInput(count),
                MaxFeeSatsPerAdditionalInput = CreateUtxosMaxFeeSatsPerAdditionalInput(count),
                AllowedScripts = new HashSet<Script> { ownAddr.ScriptPubKey },
                MaxOutputCount = count + 1,
                RequireRgbVanillaKeychainInputs = true
            };
            var signer = await ResolveSignerOrThrowAsync(walletId, ct);

            if (authorize != null && !await authorize(ct))
                throw new RgbAutomaticReplenishmentNotAuthorizedException(
                    "automatic RGB UTXO creation is no longer authorized by current store state");

            var result = await _rgbLib.CreateUtxosBeginAsync(walletId, count, size, CreateUtxosFeeRate, ct);
            if (string.IsNullOrEmpty(result)) return 0;

            var psbt = ExtractPsbt(result);

            if (authorize != null && !await authorize(ct))
                throw new RgbAutomaticReplenishmentNotAuthorizedException(
                    "automatic RGB UTXO creation stopped being authorized while the unsigned transaction was built — discarding it unsigned");

            var signed = await SignPsbtWithSignerAsync(signer, walletId, psbt, network, policy, ct);
            await _rgbLib.CreateUtxosEndAsync(walletId, signed, ct);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(ex, "UTXOs already available for wallet {WalletId}", walletId);
            return 0;
        }
    }

    async Task<IRgbWalletSigner> ResolveSignerOrThrowAsync(string walletId, CancellationToken ct)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId, ct);
        if (signer == null)
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");
        return signer;
    }

    async Task<string> SignPsbtWithSignerAsync(IRgbWalletSigner signer, string walletId, string psbt,
        Network network, SigningPolicy policy, CancellationToken ct)
    {
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

    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network, SigningPolicy policy, CancellationToken ct = default)
    {
        var signer = await ResolveSignerOrThrowAsync(walletId, ct);
        return await SignPsbtWithSignerAsync(signer, walletId, psbt, network, policy, ct);
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
        var asset = await _sendCoordinator.WithSendLockAsync(walletId,
            () => _rgbLib.IssueAssetNiaAsync(walletId, ticker, name, [amt], precision, ct), ct);

        try
        {
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
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Asset {AssetId} was issued on wallet {WalletId} and the RGB Stock mutation is irreversible, "
                + "but recording its RGBAssets row failed. Reporting the issuance as failed would make the "
                + "operator issue a second contract; the row is reconciled by the Assets page instead",
                asset.AssetId, walletId);
        }

        return asset;
    }

    public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default)
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
            MonitoringExpirationTimestamp = monitoringExpirationTimestamp,
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

    public async Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        // Background refresh: skip if a send holds the lock — the send's own write-ahead
        // covers state mutation; a concurrent refresh would either block it or race the Stock.
        return await _sendCoordinator.TryWithSendLockAsync(walletId, async markedByThisRefresh =>
        {
            await ReconcileWalletRecoveryAsync(wallet, ct,
                durableRecoveryWasPending: !markedByThisRefresh);
            // The one place a quarantine this call did not set is discharged, and the reason both halves of
            // its position are load-bearing. INSIDE the delegate: the coordinator releases the send lock
            // before returning, so a clear placed after the call would commit unlocked, and because
            // SetNeedsRecoveryAsync early-returns on an already-set flag, a holder that marked in that window
            // is invisible — the clear would discharge ITS quarantine mid-mutation. IMMEDIATELY AFTER the
            // refresh: RefreshAsync is what reconciles the Stock, so gating the discharge on anything further
            // lets an unrelated failure hold the quarantine open with nothing left to lift it.
            await _rgbLib.GetBtcBalanceAsync(walletId, ct, sync: true);
        }, ct);
    }

    public async Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListTransfersAsync(walletId, assetId, ct);
    }

    internal async Task<List<RgbMatchedTransfer>> GetIncomingTransfersForRecipientsAsync(
        string walletId, IReadOnlyCollection<string> recipientIds, string? assetId,
        CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListIncomingTransfersForRecipientsAsync(
            walletId, recipientIds, assetId, ct);
    }

    const int RgbLibTransferStatusInitiated = 5;
    const int RgbLibTransferStatusWaitingCounterparty = 1;
    const int RgbLibTransferStatusWaitingConfirmations = 2;
    const int RgbLibTransferStatusSettled = 3;
    const int RgbLibTransferStatusFailed = 4;
    internal const string SendRecoveryAdvisory =
        "The wallet is pending automatic recovery; do not retry this payment.";
    internal const int StagedRecoveryBatchSize = 64;
    internal const int StagedRecoveryMaxRowsPerAttempt = 4_096;

    internal static async Task<IReadOnlyList<int>> FindOrphanedOutgoingBatchIndicesAsync(
        string dbPath, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath))
            return Array.Empty<int>();

        var found = new List<int>();
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT bt.idx
            FROM batch_transfer AS bt
            INNER JOIN asset_transfer AS atx ON atx.batch_transfer_idx = bt.idx
            INNER JOIN transfer AS t ON t.asset_transfer_idx = atx.idx
            WHERE bt.status = @initiated AND t.incoming = 0
            ORDER BY bt.idx
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@initiated", RgbLibTransferStatusInitiated);
        cmd.Parameters.AddWithValue("@limit", StagedRecoveryBatchSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            found.Add(reader.GetInt32(0));
        return found;
    }

    internal static async Task<int?> FindOutgoingBatchStatusAsync(
        string dbPath, int batchTransferIdx, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath)) return null;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bt.status
            FROM batch_transfer AS bt
            WHERE bt.idx = @idx
              AND EXISTS (
                  SELECT 1
                  FROM asset_transfer AS atx
                  INNER JOIN transfer AS t ON t.asset_transfer_idx = atx.idx
                  WHERE atx.batch_transfer_idx = bt.idx AND t.incoming = 0)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@idx", batchTransferIdx);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value == null || value == DBNull.Value ? null : Convert.ToInt32(value);
    }

    internal static async Task<(int Status, string? Txid)?> FindOutgoingBatchRowAsync(
        string dbPath, int batchTransferIdx, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath)) return null;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            DefaultTimeout = 2
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bt.status, bt.txid
            FROM batch_transfer AS bt
            WHERE bt.idx = @idx
              AND EXISTS (
                  SELECT 1
                  FROM asset_transfer AS atx
                  INNER JOIN transfer AS t ON t.asset_transfer_idx = atx.idx
                  WHERE atx.batch_transfer_idx = bt.idx AND t.incoming = 0)
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@idx", batchTransferIdx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    internal static async Task<bool> VerifyRecordedSendEndAsync(
        Func<string, int, CancellationToken, Task<(int Status, string? Txid)?>> batchRowReader,
        string dbPath, int batchTransferIdx, string? expectedTxid, Exception sendException,
        ILogger logger, CancellationToken ct = default)
    {
        var row = await batchRowReader(dbPath, batchTransferIdx, ct);
        var accepted = row is
            { Status: RgbLibTransferStatusWaitingCounterparty
                or RgbLibTransferStatusWaitingConfirmations
                or RgbLibTransferStatusSettled, Txid: not null }
            && expectedTxid != null
            && string.Equals(row.Value.Txid, expectedTxid, StringComparison.OrdinalIgnoreCase);
        if (accepted)
            logger.LogError(sendException,
                "SendAsset: send_end helper failed after rgb-lib recorded transfer initiation");
        return accepted;
    }

    internal static async Task<bool> HasOutgoingBatchStatusAsync(
        string dbPath, int status, CancellationToken ct = default)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM batch_transfer AS bt
                INNER JOIN asset_transfer AS atx ON atx.batch_transfer_idx = bt.idx
                INNER JOIN transfer AS t ON t.asset_transfer_idx = atx.idx
                WHERE bt.status = @status AND t.incoming = 0)
            """;
        cmd.Parameters.AddWithValue("@status", status);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 0;
    }

    internal static async Task DrainOrphanedOutgoingBatchesAsync(
        IReadOnlyList<int> firstPage,
        Func<Task<IReadOnlyList<int>>> findNextPage,
        Func<int, Task> failBatch)
    {
        var page = firstPage;
        var processed = 0;
        while (page.Count != 0)
        {
            if (processed + page.Count > StagedRecoveryMaxRowsPerAttempt)
                throw new RgbWalletQuarantinedException(
                    "staged-send recovery reached its per-attempt work bound; remaining rows will retry");

            foreach (var batchTransferIdx in page)
                await failBatch(batchTransferIdx);
            processed += page.Count;

            var next = await findNextPage();
            if (next.SequenceEqual(page))
                throw new RgbWalletQuarantinedException(
                    "staged-send recovery made no durable progress");
            page = next;
        }
    }

    public async Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
        string walletId, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var walletDir = Path.Combine(
            _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network), wallet.MasterFingerprint);
        var reserved = await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(
            Path.Combine(walletDir, "rgb_lib_db"), ct);
        if (reserved.Count == 0) return RgbVanillaReservationInspector.Clean;

        List<Outpoint>? bdkUnspentOutpoints = null;
        try
        {
            var unspents = await _rgbLib.ListUnspentsAsync(walletId, ct);
            bdkUnspentOutpoints = unspents.Select(u => u.Utxo.Outpoint).ToList();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex,
                "Wallet {WalletId}: cannot classify pending vanilla reservations without the unspent set",
                walletId);
        }
        return RgbVanillaReservationInspector.Classify(reserved, bdkUnspentOutpoints);
    }

    async Task ReconcileWalletRecoveryAsync(RGBWallet wallet, CancellationToken ct,
        RgbNativeSendLease? operationLease = null, bool durableRecoveryWasPending = false)
    {
        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network);
        var walletDir = Path.Combine(walletDataDir, wallet.MasterFingerprint);
        var dbPath = Path.Combine(walletDir, "rgb_lib_db");
        var journalPath = RgbSendRecoveryJournal.PathFor(walletDataDir, wallet.MasterFingerprint);
        var databaseExistedBeforeAnyNativeMutationOfThisReconciliation = File.Exists(dbPath);

        var durableRecoveryObserved = operationLease != null
            || durableRecoveryWasPending
            || File.Exists(journalPath);
        if (operationLease == null && !durableRecoveryObserved && !RgbNativeSendLease.Exists(walletDir))
        {
            IReadOnlyList<int> probe;
            using (RgbNativeSendLease.AcquireWalletAccess(walletDir))
                probe = await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct);
            if (probe.Count == 0)
            {
                // Healthy refreshes use normal native serialization. Publishing a helper marker here
                // would reject unrelated checkout and UI calls throughout every background poll.
                await _rgbLib.RefreshAsync(wallet.Id, ct);
                return;
            }
            durableRecoveryObserved = true;
        }

        // Parse before publishing a recovery marker. A truncated or corrupt journal must fail closed,
        // but it must not manufacture a second durable reason that blocks every native read forever.
        RgbSendRecoveryRecord? recovery;
        var journalUnparseable = false;
        try { recovery = RgbSendRecoveryJournal.Read(journalPath); }
        catch (InvalidDataException unparseable)
        {
            recovery = null;
            journalUnparseable = true;
            _log.LogError(unparseable,
                "Wallet {WalletId}: send recovery journal cannot be parsed — treating it as an unknown "
                + "staged send that only the orphan sweep may discharge", wallet.Id);
        }
        using var acquiredLease = operationLease == null ? AcquireNativeSendRecoveryLease(walletDir) : null;
        var nativeSendLease = operationLease ?? acquiredLease!;
        var completed = false;
        var markerOnlyProven = false;
        try
        {
            var phase = recovery?.Phase;
            var orphans = await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct);
            durableRecoveryObserved |= orphans.Count != 0 || phase != null;
            markerOnlyProven = phase == null && orphans.Count == 0;

            // Once send_end was entered, reconcile first: it may have committed even if its caller
            // never observed success. Settlement remains skipped while this method owns the wallet
            // lock. An exact journal first replays a surviving Initiated batch; if that reaped replay
            // proves the proxy already consumed the recipient ID but status 5 remains, it is safely failed.
            if (phase == RgbSendRecoveryPhase.SendEndIndeterminate)
            {
                if (recovery is { HasSendEndReplay: true })
                {
                    var preRefreshStatus = await FindOutgoingBatchStatusAsync(
                        dbPath, recovery.BatchTransferIdx!.Value, ct);
                    if (preRefreshStatus == RgbLibTransferStatusWaitingCounterparty)
                    {
                        ValidateRecoveryPsbt(
                            recovery.SignedPsbt!, recovery.RawTransaction!,
                            recovery.TransactionId!, wallet.Network);
                        RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
                            walletDir, recovery.TransactionId!, recovery.SignedPsbt!);
                    }
                }
                await _rgbLib.RefreshAsync(wallet.Id, ct);
                orphans = await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct);

                // Older phase-only journals, and exact journals written without a signed PSBT, cannot
                // restore the artifact beta.30 needs after an ACK. Never accept or delete such a journal
                // while its status-1 transfer may still need to broadcast. With no batch index the legacy
                // format cannot distinguish its row from another outbound transfer, so ambiguity is kept
                // quarantined in the fail-closed direction.
                if (recovery is not { HasSendEndReplay: true })
                {
                    var hasExactTransaction = recovery is { HasExactTransactionRecovery: true };
                    var exactStatus = hasExactTransaction
                        ? await FindOutgoingBatchStatusAsync(
                            dbPath, recovery!.BatchTransferIdx!.Value, ct)
                        : null;
                    var anyWaitingCounterparty = !hasExactTransaction
                        && await HasOutgoingBatchStatusAsync(
                            dbPath, RgbLibTransferStatusWaitingCounterparty, ct);
                    if (ShouldQuarantineIncompleteAckRecovery(
                            hasSendEndReplay: false,
                            hasExactTransaction,
                            exactStatus,
                            anyWaitingCounterparty))
                        throw new RgbWalletQuarantinedException(
                            "send_end recovery cannot prove the ACK-broadcast PSBT is durable");
                }

                if (recovery is { HasExactTransactionRecovery: true })
                {
                    var status = await FindOutgoingBatchStatusAsync(
                        dbPath, recovery.BatchTransferIdx!.Value, ct);
                    if (status == null)
                        throw new RgbWalletQuarantinedException(
                            "send_end recovery cannot find its outbound batch");

                    // For this plugin's non-donation flow beta.30 moves Initiated to
                    // WaitingCounterparty without broadcasting. Exact replay is still preferable to
                    // failure: it completes the already-signed transfer deterministically. The native
                    // refresh path remains responsible for the ACK-gated broadcast.
                    if (status == RgbLibTransferStatusInitiated)
                    {
                        if (!recovery.HasSendEndReplay)
                            throw new RgbWalletQuarantinedException(
                                "send_end recovery lacks the signed PSBT required to finish an Initiated batch");
                        ValidateRecoveryPsbt(
                            recovery.SignedPsbt!, recovery.RawTransaction!,
                            recovery.TransactionId!, wallet.Network);
                        if (RecoveredPsbtKeepsItsUnsignedTransactionId(
                                recovery.SignedPsbt!, recovery.TransactionId!, wallet.Network))
                        {
                            nativeSendLease.PrepareWorkerReplay(walletDir);
                            string? sendEndResult = null;
                            var replayFailedAfterReap = false;
                            try
                            {
                                sendEndResult = await RunNativeSendIsolatedAsync(
                                    wallet, "send-end", recipientMapJson: null, feeRate: 1,
                                    minConfirmations: 1, recovery.SignedPsbt, ct);
                            }
                            catch (NativeSendReapedFailureException ex)
                            {
                                // beta.30 uploads before committing status 1. If the helper dies in that
                                // interval, replay gets RecipientIDAlreadyUsed while the authoritative row
                                // remains Initiated. A confirmed-reaped helper makes failing that row safe;
                                // an unreaped or pre-launch failure is deliberately not caught here.
                                replayFailedAfterReap = true;
                                _log.LogWarning(ex,
                                    "Exact send_end replay failed after child reap for wallet {WalletId}; "
                                    + "the authoritative transfer status will decide recovery",
                                    wallet.Id);
                            }
                            finally
                            {
                                try { nativeSendLease.ReclaimWorkerAfterReplay(walletDir); }
                                catch { throw new NativeSendChildUnreapedException(); }
                            }
                            status = await FindOutgoingBatchStatusAsync(
                                dbPath, recovery.BatchTransferIdx.Value, ct);
                            if (status == null)
                                throw new RgbWalletQuarantinedException(
                                    "send_end replay lost its outbound batch");

                            if (!replayFailedAfterReap)
                            {
                                ValidateSendEndTransactionId(sendEndResult!, recovery.TransactionId!);
                                RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
                                    walletDir, recovery.TransactionId!, recovery.SignedPsbt!);
                            }

                            if (ShouldFailInitiatedAfterReapedReplayFailure(
                                    status.Value, replayFailedAfterReap))
                            {
                                // Leave status 5 in the refreshed orphan page below. The bounded sweep
                                // changes it to Failed and the final empty-page barrier proves progress.
                            }
                            else
                            {
                                await _rgbLib.RefreshAsync(wallet.Id, ct);
                                status = await FindOutgoingBatchStatusAsync(
                                    dbPath, recovery.BatchTransferIdx.Value, ct);
                                if (status == null)
                                    throw new RgbWalletQuarantinedException(
                                        "send_end recovery lost its outbound batch after refresh");
                            }

                            if (status == RgbLibTransferStatusInitiated && !replayFailedAfterReap)
                                throw new RgbWalletQuarantinedException(
                                    "replayed send_end left its outbound batch Initiated");
                        }
                        else
                        {
                            _log.LogError(
                                "Wallet {WalletId}: the journalled send_end PSBT does not keep its durable "
                                + "transaction id through finalization — refusing to replay it; its staged "
                                + "batch is failed by the bounded sweep instead", wallet.Id);
                        }
                    }

                    if (status != RgbLibTransferStatusInitiated
                        && ShouldRebroadcastRecoveredTransaction(status.Value))
                    {
                        await EnsureRecoveryTransactionBroadcastAsync(
                            wallet.Network,
                            recovery.RawTransaction!,
                            recovery.TransactionId!,
                            ct);
                    }
                    else if (status != RgbLibTransferStatusInitiated
                             && !IsRecoveredTransactionSafeWithoutBroadcast(status.Value))
                    {
                        throw new RgbWalletQuarantinedException(
                            $"send_end recovery found unknown transfer status {status}");
                    }

                    // send_end replay may have moved the batch out of Initiated. Refresh the orphan
                    // page so the generic staged cleanup below cannot fail an already-broadcast send.
                    orphans = await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct);
                }
                // The prior release's phase-only journal has no exact PSBT to replay. In beta.30's
                // non-donation path an Initiated row proves send_end did not reach its status update and
                // did not broadcast, so it is safe to fail through the same bounded sweep as Staged.
            }

            // A missing journal does not mean there is no staging: an older build or a crash before
            // managed send_begin returned can leave only rgb-lib's durable Initiated rows. Those rows
            // are authoritative. WaitingCounterparty is a live protocol state and must never be swept.
            await DrainOrphanedOutgoingBatchesAsync(
                orphans,
                () => FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct),
                batchTransferIdx => _rgbLib.FailTransfersAsync(wallet.Id, batchTransferIdx,
                    noAssetOnly: false, skipSync: true, ct));

            await _rgbLib.RefreshAsync(wallet.Id, ct);

            var stillIndeterminate = await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct);
            if (stillIndeterminate.Count != 0)
                throw new RgbWalletQuarantinedException(
                    "outbound staged transfers remain after reconciliation");

            if (journalUnparseable
                && (!databaseExistedBeforeAnyNativeMutationOfThisReconciliation
                    || !File.Exists(dbPath)
                    || ShouldQuarantineIncompleteAckRecovery(
                        hasSendEndReplay: false,
                        hasExactTransaction: false,
                        exactStatus: null,
                        anyWaitingCounterparty: await HasOutgoingBatchStatusAsync(
                            dbPath, RgbLibTransferStatusWaitingCounterparty, ct))))
                throw new RgbWalletQuarantinedException(
                    "unparseable send recovery journal kept: absence of an unresolved outbound send is "
                    + "unproven. Evidence read after this reconciliation's own native work cannot serve "
                    + "— rgb-lib opens rgb_lib_db rwc and would have created a replacement — and a "
                    + "database that cannot be read is unknown, never absent. A status-1 send still "
                    + "needs the journalled PSBT for its ACK broadcast; the status-5 rows were drained "
                    + "above before this decision precisely so deletion stays available as the escape");

            // Drop the artifacts that make a wallet UNDISCOVERABLE first; commit the flag that makes it
            // DISCOVERABLE last. Clearing NeedsRecovery first left a window in which a crash kept the
            // marker and journal — which refuse sends, AcquireParent and deletion — while the listener's
            // (IsActive || NeedsRecovery) page no longer enumerated an inactive wallet, so nothing
            // re-armed the artifact-driven reconciliation that removes them. This order has no
            // undiscoverable intermediate state: a crash after the deletes leaves NeedsRecovery set with
            // no artifacts, which the next sweep reconciles against an empty orphan set and clears.
            // ClearNeedsRecovery still fsyncs the Stock before committing the database flag.
            nativeSendLease.ClearActiveMarker(walletDir);
            RgbSendRecoveryJournal.Delete(journalPath);
            await ClearNeedsRecoveryAsync(wallet.Id, ct);
            completed = true;
        }
        finally
        {
            // A marker with no journal, quarantine, or Initiated row can only predate staged mutation.
            // Do not turn an unrelated refresh outage into a wallet-wide helper quarantine.
            if (!completed && markerOnlyProven)
                nativeSendLease.ClearActiveMarker(walletDir);
            if (!completed && journalUnparseable)
                acquiredLease?.ClearActiveMarker(walletDir);
        }
    }

    static RgbNativeSendLease AcquireNativeSendRecoveryLease(string walletDir)
    {
        try { return RgbNativeSendLease.AcquireRecovery(walletDir); }
        catch (IOException ex)
        {
            throw new RgbWalletQuarantinedException(
                "native send helper is still running — wallet recovery remains quarantined", ex);
        }
    }

    static RgbNativeSendLease AcquireNativeSendParentLease(string walletDir)
    {
        try { return RgbNativeSendLease.AcquireParent(walletDir); }
        catch (IOException ex)
        {
            throw new RgbWalletQuarantinedException(
                "another process owns this RGB wallet — refusing to stage a send", ex);
        }
    }

    // beta.30 status 1 is WaitingCounterparty: broadcasting there bypasses the recipient's ACK/NACK.
    // Status 2/6 proves refresh already crossed the ACK-gated native broadcast transition. Settled is
    // already confirmed, so it needs no rebroadcast either.
    internal static bool ShouldRebroadcastRecoveredTransaction(int status) => status is 2 or 6;

    // WaitingCounterparty is the expected post-send_end state until the recipient ACKs. Settled and
    // Failed are also complete protocol decisions that require no managed broadcast. Initiated is not
    // accepted here: the exact replay above must first move it out of status 5.
    internal static bool IsRecoveredTransactionSafeWithoutBroadcast(int status) => status is 1 or 3 or 4;

    internal static bool ShouldQuarantineIncompleteAckRecovery(
        bool hasSendEndReplay, bool hasExactTransaction, int? exactStatus,
        bool anyWaitingCounterparty) =>
        !hasSendEndReplay
        && (hasExactTransaction
            ? exactStatus == RgbLibTransferStatusWaitingCounterparty
            : anyWaitingCounterparty);

    internal static bool ShouldFailInitiatedAfterReapedReplayFailure(
        int status, bool replayFailedAfterReap) =>
        replayFailedAfterReap && status == RgbLibTransferStatusInitiated;

    async Task EnsureRecoveryTransactionBroadcastAsync(
        string walletNetwork, string rawTransaction, string transactionId, CancellationToken ct)
    {
        var settings = RGBConfiguration.GetNetworkSettings(walletNetwork);
        var allowInsecure = NetworkSettings.AllowsPlainElectrum(walletNetwork);
        using var chain = BitcoinChainClientFactory.Create(
            settings.ElectrumUrl, allowInsecure: allowInsecure);
        await chain.ConnectAsync(ct);
        await EnsureTransactionBroadcastAsync(
            chain, NetworkHelper.GetNetwork(walletNetwork), rawTransaction, transactionId, ct);
    }

    internal static async Task EnsureTransactionBroadcastAsync(
        IBitcoinChainClient chain,
        Network network,
        string rawTransaction,
        string transactionId,
        CancellationToken ct = default)
    {
        var parsed = Transaction.Parse(rawTransaction, network);
        var computedTransactionId = parsed.GetHash().ToString();
        if (!string.Equals(computedTransactionId, transactionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "recovery transaction does not match its durable transaction id");

        Exception? initialLookupError = null;
        try
        {
            var existing = Transaction.Parse(await chain.GetRawTransactionAsync(transactionId, ct), network);
            if (!string.Equals(existing.GetHash().ToString(), transactionId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chain server returned the wrong recovery transaction");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            initialLookupError = ex;
        }

        try
        {
            var broadcastTransactionId = await chain.BroadcastTransactionAsync(rawTransaction, ct);
            if (!string.Equals(broadcastTransactionId, transactionId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chain server returned the wrong broadcast transaction id");
            return;
        }
        catch (Exception broadcastError) when (broadcastError is not OperationCanceledException)
        {
            // Electrum/Esplora may report an already-known transaction as a broadcast error. Verify
            // after that response so retries remain idempotent without accepting an unrelated txid.
            try
            {
                var existing = Transaction.Parse(
                    await chain.GetRawTransactionAsync(transactionId, ct), network);
                if (string.Equals(existing.GetHash().ToString(), transactionId,
                        StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch (Exception verificationError) when (verificationError is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "RGB recovery could neither verify nor broadcast its transaction",
                    new AggregateException(initialLookupError!, broadcastError, verificationError));
            }

            throw new InvalidOperationException(
                "RGB recovery broadcast could not be verified",
                new AggregateException(initialLookupError!, broadcastError));
        }
    }

    internal static bool RecoveredPsbtKeepsItsUnsignedTransactionId(
        string signedPsbt, string transactionId, string networkName)
    {
        var psbt = PSBT.Parse(signedPsbt, NetworkHelper.GetNetwork(networkName));
        return string.Equals(psbt.GetGlobalTransaction().GetHash().ToString(), transactionId,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static void ValidateRecoveryPsbt(
        string signedPsbt, string rawTransaction, string transactionId, string networkName)
    {
        try
        {
            var psbt = PSBT.Parse(signedPsbt, NetworkHelper.GetNetwork(networkName));
            if (!psbt.TryFinalize(out var errors))
                throw new InvalidDataException(
                    $"recovery PSBT cannot be finalized: {string.Join("; ", errors)}");
            var transaction = psbt.ExtractTransaction();
            if (!string.Equals(transaction.ToHex(), rawTransaction, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(transaction.GetHash().ToString(), transactionId,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "recovery PSBT does not match the durable transaction identity");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidDataException("recovery PSBT is invalid", ex);
        }
    }

    internal static void ValidateSendEndTransactionId(string sendEndResult, string transactionId)
    {
        using var document = JsonDocument.Parse(sendEndResult);
        var returned = document.RootElement.TryGetProperty("txid", out var txidProperty)
            ? txidProperty.GetString() ?? sendEndResult
            : sendEndResult;
        if (!string.Equals(returned.Trim('"'), transactionId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "send_end returned a transaction id that differs from the durable signed transaction");
    }

    public async Task<bool> CleanupExpiredTransfersAsync(string walletId, string walletNetwork, string masterFingerprint, CancellationToken ct = default)
    {
        // Background sweep, so it skips a busy wallet instead of blocking on it, exactly as
        // RefreshWalletAsync does: this call sits in RefreshAllWallets' sequential per-wallet loop, so
        // blocking here stalls the wallets after this one for as long as the holder keeps the lock. The
        // holder is whatever operation currently holds this wallet's send lock; the unbounded case is an RGB
        // asset send, which holds it across an uncancellable upload to a remote endpoint (audit H2c). Skipping
        // cannot settle an invoice wrongly — that needs a status-3 transfer either way — and what it skips is a
        // REJECTION, so withholding it is the permitted direction. It is not free, and it is not merely
        // postponed: the predicate matches status 1 only, so a refresh that advances the row voids it
        // permanently. Meanwhile ProcessTransfers keeps matching the status-1 transfer, so a Pending invoice can
        // advance to WaitingConfirmations and record a Processing payment this flip would have suppressed, which
        // nothing in this plugin ever invalidates; and the wallet's Transfers page reads "Waiting Confirmations"
        // for as long as the row sits at status 1 or 2 (audit H2c-lite R10, R11).
        var acquired = await _sendCoordinator.TryWithSendLockAsync(walletId,
            () => CleanupExpiredTransfersInternalAsync(walletId, walletNetwork, masterFingerprint, ct), ct);
        if (!acquired)
            _log.LogDebug("Expired-transfer cleanup skipped for wallet {WalletId}: the send lock is held", walletId);
        return acquired;
    }

    async Task<int> CleanupExpiredTransfersInternalAsync(string walletId, string walletNetwork, string masterFingerprint, CancellationToken ct)
    {
        var walletDataDir = _rgbLib.GetWalletDataDir(walletId, walletNetwork);
        var walletDir = Path.Combine(walletDataDir, masterFingerprint);
        var dbPath = Path.Combine(walletDir, "rgb_lib_db");
        if (!File.Exists(dbPath)) return 0;

        // The raw SQLite write is native wallet state too. Own the same cross-process interval as
        // sends and deletion so it cannot flip an ACK-pending batch while a helper is reconciling it.
        using var operationLease = AcquireNativeSendParentLease(walletDir);
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var connStr = $"Data Source={dbPath}";
            await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE batch_transfer SET status = {RgbLibTransferStatusFailed} WHERE status = {RgbLibTransferStatusWaitingCounterparty} AND expiration IS NOT NULL AND expiration < @now";
            cmd.Parameters.AddWithValue("@now", now);
            var count = await cmd.ExecuteNonQueryAsync(ct);
            if (count > 0)
            {
                _log.LogInformation("Cleaned up {Count} expired blind receive transfers for wallet {WalletId}", count, walletId);
                // WHY: this runs inside the WithSendLock write-ahead op; a swallowed refresh failure
                // would let the coordinator clear NeedsRecovery over a possibly-incomplete Stock.
                // Must propagate so the failure path leaves the wallet quarantined + evicts the handle.
                await _rgbLib.RefreshAsync(walletId, ct);
            }
            return count;
        }
        finally { operationLease.ClearActiveMarker(walletDir); }
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
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(maxAllocationsPerUtxo ?? _cfg.MaxAllocationsPerUtxo),
            NeedsRecovery = true
        };

        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, walletNetwork);
        var parentDir = Path.GetDirectoryName(walletDataDir)!;
        Directory.CreateDirectory(parentDir);

        var cooldown = GetOrCreateRestoreCooldown(() => new RestoreCooldownGate(
            TimeSpan.FromSeconds(_cfg.RestoreKillCooldownSeconds)));
        var nowUtc = DateTimeOffset.UtcNow;
        if (cooldown.IsCoolingDown(nowUtc))
            throw new InvalidOperationException(
                "A wallet restore was attempted recently. Try again in "
                + $"{RgbLibService.DescribeRetryDelayWithoutUnderstatingIt(cooldown.Remaining(nowUtc))}.");

        // SECURITY: Backup file is validated before reaching native code:
        // - ZIP structure + entry validation (controller ValidateBackupFileHeader)
        // - scrypt KDF cost cap (RgbBackupScryptGuard, inside the gate below)
        // - Post-extraction size cap (configurable RestoreDiskCapBytes, below)
        // The native restore runs in a separate killable child process (RestoreExecutor):
        // a hung/oversized restore is terminated and the staging dir is deleted only once the
        // child is confirmed reaped, else left for the startup sweep.
        // The single-flight gate guards the expensive native restore and every persistent
        // side effect (staging dir, Directory.Move, DB row). The cheap
        // key derivation and in-memory row build above remain outside it; a rejected concurrent restore
        // still creates no staging dir and no wallet row.
        var entered = await _restoreGate.WaitAsync(TimeSpan.Zero, ct);
        if (!entered)
            throw new InvalidOperationException(
                "Another wallet restore is already in progress. Try again once it completes.");
        try
        {
            // The scrypt cost lives inside the uploaded file and is spent BEFORE decryption, so it is
            // bounded on the path every restore caller takes, not in the controller, which is only one
            // of them. This must stay inside the process-wide gate: method-93 public data requires
            // bounded decompression in the parent, and concurrent requests must not multiply that work.
            RgbBackupScryptGuard.ValidateFile(backupPath, _cfg.RestoreScryptMemoryCapBytes);

            var stagingDir = Path.Combine(parentDir, $"{RestoreStagingPrefix}{wallet.Id}-{Guid.NewGuid():N}");

            try { await _restoreExecutor.ExecuteAsync(backupPath, stagingDir, password, ct); }
            finally
            {
                // The KDF runs before password validation, and a measured log_n=18 wrong-password
                // attempt consumed 290 MiB in 399 ms. Neither exit code nor a duration threshold can
                // distinguish that from honest work, so every native attempt pays the same duty cycle.
                cooldown.RecordAttempt(DateTimeOffset.UtcNow);
            }

            var dirSize = new DirectoryInfo(stagingDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
            var diskCap = _cfg.ToRestoreLimits().DiskCapBytes;
            if (dirSize > diskCap)
            {
                try { Directory.Delete(stagingDir, true); }
                catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up oversized staging dir {Dir}", stagingDir); }
                _log.LogWarning(
                    "Refusing restore for wallet {Id}: the decompressed wallet directory measured {DirSizeBytes} bytes against a staging cap of {DiskCapBytes} bytes",
                    wallet.Id, dirSize, diskCap);
                throw new InvalidOperationException(
                    RestoreExecutor.RefusalForAWalletDirectoryThatOutgrewTheStagingBudget(diskCap));
            }

            var reservedNameUsedAsDirectory = FindDirectoryAtAReservedSingleFileName(stagingDir);
            if (reservedNameUsedAsDirectory != null)
            {
                _log.LogError(
                    "Refusing restore for wallet {Id}: the backup holds a directory at the reserved single-file name {ReservedName}, which would leave the wallet unable to send or to be deleted",
                    wallet.Id, reservedNameUsedAsDirectory);
                try { Directory.Delete(stagingDir, true); }
                catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up staging dir after reserved name rejection"); }
                throw new InvalidOperationException(
                    ReservedSingleFileNameUsedAsDirectoryRefusal(reservedNameUsedAsDirectory));
            }

            var reservedNameUsedAsRegularFile = FindRegularFileAtAReservedDirectoryName(stagingDir);
            if (reservedNameUsedAsRegularFile != null)
            {
                _log.LogError(
                    "Refusing restore for wallet {Id}: the backup holds a regular file at the reserved directory name {ReservedName}, which would leave the wallet unable to send or to receive any RGB asset",
                    wallet.Id, reservedNameUsedAsRegularFile);
                try { Directory.Delete(stagingDir, true); }
                catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up staging dir after reserved directory name rejection"); }
                throw new InvalidOperationException(
                    ReservedDirectoryNameUsedAsRegularFileRefusal(reservedNameUsedAsRegularFile));
            }

            var expectedFingerprint = wallet.MasterFingerprint?.ToLowerInvariant();
            var stagingFingerprintDirs = Directory.GetDirectories(stagingDir)
                .Select(d => Path.GetFileName(d).ToLowerInvariant())
                .Where(name => name.Length == 8 && name.All(c => "0123456789abcdef".Contains(c)))
                .ToList();

            if (stagingFingerprintDirs.Count > 0 && !string.IsNullOrEmpty(expectedFingerprint)
                && !stagingFingerprintDirs.Contains(expectedFingerprint))
            {
                _log.LogError(
                    "Mnemonic/backup mismatch for wallet {Id}: the supplied recovery phrase derives a master fingerprint that none of the backup's {BackupFingerprintDirectoryCount} key directories match (the fingerprints themselves are withheld from logs)",
                    wallet.Id, stagingFingerprintDirs.Count);
                try { Directory.Delete(stagingDir, true); }
                catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up staging dir after fingerprint mismatch"); }
                throw new InvalidOperationException(
                    "Backup could not be loaded with the supplied mnemonic. The mnemonic does not match the keys in this backup.");
            }

            var walletDirectoryRgbLibWillOpenAfterTheMove =
                IsAFingerprintShapedWalletDirectoryName(wallet.MasterFingerprint)
                    ? Path.Combine(stagingDir, wallet.MasterFingerprint)
                    : null;
            if (walletDirectoryRgbLibWillOpenAfterTheMove == null
                || !Directory.Exists(walletDirectoryRgbLibWillOpenAfterTheMove))
            {
                var refusal = walletDirectoryRgbLibWillOpenAfterTheMove == null
                    ? RecoveryPhraseYieldedNoWalletDirectoryNameRefusal
                    : stagingFingerprintDirs.Count > 0
                        ? BackupWalletDirectoryDiffersOnlyInLetterCaseRefusal
                        : BackupCarriesNoWalletDirectoryForThisRecoveryPhraseRefusal;
                _log.LogError(
                    "Refusing restore for wallet {Id}: the backup carries no wallet directory at the name rgb-lib joins onto the data dir for the supplied recovery phrase, so publishing it would leave rgb-lib to create a fresh empty wallet beside the restored data (fingerprint-shaped top-level directories in the backup: {BackupFingerprintDirectoryCount}; the recovery phrase yielded a usable directory name: {RecoveryPhraseYieldedAUsableDirectoryName}; the fingerprints themselves are withheld from logs)",
                    wallet.Id, stagingFingerprintDirs.Count,
                    walletDirectoryRgbLibWillOpenAfterTheMove != null);
                try { Directory.Delete(stagingDir, true); }
                catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up staging dir after a missing wallet directory"); }
                throw new InvalidOperationException(refusal);
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

            // Born-quarantined: hold the send lock BEFORE the row becomes visible so a racing send
            // both blocks and observes NeedsRecovery=true; the reconciling refresh clears it on success.
            var sendLock = _sendLocks.GetOrAdd(wallet.Id, _ => new SemaphoreSlim(1, 1));
            await sendLock.WaitAsync(ct);
            try
            {
                try
                {
                    await using var ctx = _db.CreateContext();
                    ctx.RGBWallets.Add(wallet);
                    await ctx.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_RGB_Wallets_StoreId", StringComparison.OrdinalIgnoreCase) == true
                    || ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _log.LogError(ex,
                        "The insert of the restored wallet row {Id} was rejected as a duplicate. Its "
                        + "unpacked wallet data is left at {Dir} rather than deleted: the row that "
                        + "collided can be this same attempt's own committed row, re-executed by the "
                        + "configured EF retry after an acknowledgement was lost, and deleting the data "
                        + "under a row that survives would leave rgb-lib to create a fresh empty wallet "
                        + "at that path and present it as the restored one",
                        wallet.Id, walletDataDir);
                    throw new InvalidOperationException(RestoreFoundThisStoreAlreadyHoldsAWalletRecordRefusal);
                }
                catch (Exception saveFailure)
                {
                    _log.LogError(saveFailure,
                        "The insert of the restored wallet row {Id} reported failure, which is not proof "
                        + "that it did not commit. Its unpacked wallet data is left at {Dir} rather than "
                        + "deleted: deleting it under a row that did commit would leave rgb-lib to create "
                        + "a fresh empty wallet at that path and present it as the restored one",
                        wallet.Id, walletDataDir);
                    throw;
                }

                try
                {
                    await _rgbLib.GetOrCreateWalletAsync(wallet.Id, ct);
                    await _rgbLib.GetAddressAsync(wallet.Id, ct);
                }
                catch (OperationCanceledException ex)
                {
                    _log.LogWarning(ex, "Restore of wallet {Id} was cancelled while opening the restored wallet data; rolling the restore back", wallet.Id);
                    await RollBackTheJustPublishedRestoreAsync(wallet, walletDataDir);
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Restored wallet data for wallet {Id} could not be opened and brought online; rolling the restore back", wallet.Id);
                    await RollBackTheJustPublishedRestoreAsync(wallet, walletDataDir);
                    throw new InvalidOperationException(
                        RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(walletNetwork, ex));
                }

                _signerProvider.RegisterSigner(wallet.Id, mnemonic, network);

                try
                {
                    await _rgbLib.RefreshAsync(wallet.Id, ct);
                    await _rgbLib.GetBtcBalanceAsync(wallet.Id, ct, sync: true);
                    await ClearNeedsRecoveryAsync(wallet.Id, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Post-restore sync failed for wallet {Id} — left quarantined", wallet.Id);
                    try { _rgbLib.UnloadWallet(wallet.Id); } catch { }
                }
            }
            finally { sendLock.Release(); }
        }
        finally
        {
            _restoreGate.Release();
        }

        _log.LogInformation("restored wallet {Id} from backup for {Store} on {Network}", wallet.Id, storeId, walletNetwork);
        return wallet;
    }

    internal static RestoreCooldownGate GetOrCreateRestoreCooldown(Func<RestoreCooldownGate> create)
    {
        var current = Volatile.Read(ref _restoreCooldown);
        if (current is not null) return current;
        var candidate = create();
        return Interlocked.CompareExchange(ref _restoreCooldown, candidate, null) ?? candidate;
    }

    internal static readonly StringComparer ReservedNameComparerThatMatchesCaseInsensitiveFilesystems =
        StringComparer.OrdinalIgnoreCase;

    internal static string? FindDirectoryAtAReservedSingleFileName(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return null;
        foreach (var directory in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(directory);
            var reserved = RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories
                .FirstOrDefault(candidate =>
                    ReservedNameComparerThatMatchesCaseInsensitiveFilesystems.Equals(candidate, name));
            if (reserved != null) return reserved;
        }
        return null;
    }

    internal static string? FindRegularFileAtAReservedDirectoryName(string stagingDir)
    {
        if (!Directory.Exists(stagingDir)) return null;
        foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var reserved = RgbWalletDirectoryReservedNames.NamesThatMustBeDirectoriesNotRegularFiles
                .FirstOrDefault(candidate =>
                    ReservedNameComparerThatMatchesCaseInsensitiveFilesystems.Equals(candidate, name));
            if (reserved != null) return reserved;
        }
        return null;
    }

    internal static string ReservedDirectoryNameUsedAsRegularFileRefusal(string reservedName) =>
        $"This backup holds a regular file named \"{reservedName}\". That name is reserved for a directory "
        + "rgb-lib creates inside the wallet directory, so restoring the backup would produce a wallet that "
        + "could never send or receive an RGB asset again and would need filesystem access to repair. A backup "
        + "produced by this plugin never contains a regular file with that name. Restore a backup taken by "
        + "this plugin, or remove that file from the archive, then try again.";

    internal static string ReservedSingleFileNameUsedAsDirectoryRefusal(string reservedName) =>
        $"This backup holds a directory named \"{reservedName}\". That name is reserved for a single file "
        + "this plugin writes inside the wallet directory, so restoring the backup would produce a wallet "
        + "that can neither send nor be deleted and would need filesystem access to repair. A backup "
        + "produced by this plugin never contains a directory with that name. Restore a backup taken by "
        + "this plugin, or remove that directory from the archive, then try again.";

    static bool IsAFingerprintShapedWalletDirectoryName([NotNullWhen(true)] string? name) =>
        name is { Length: 8 } && name.All(Uri.IsHexDigit);

    internal const string RecoveryPhraseYieldedNoWalletDirectoryNameRefusal =
        "The recovery phrase you supplied did not yield a usable master fingerprint, so this plugin cannot tell "
        + "which directory inside the backup holds the wallet, and restoring it would present an empty wallet "
        + "while the backed-up assets stayed unreachable. No wallet was created on the server and your backup "
        + "file is unchanged. Re-enter the recovery phrase, check the network you selected, and try again.";

    internal const string BackupCarriesNoWalletDirectoryForThisRecoveryPhraseRefusal =
        "This backup holds no wallet directory for the recovery phrase you supplied. A backup taken by this "
        + "plugin keeps the whole wallet under one top-level directory named for the master fingerprint that the "
        + "recovery phrase derives, and this archive has no directory at that name. Restoring it would report "
        + "success and then present an empty wallet, because rgb-lib silently creates a new empty wallet when the "
        + "directory it expects is missing, leaving the backed-up assets unreachable under the other name. No "
        + "wallet was created on the server and your backup file is unchanged. Restore an unmodified backup taken "
        + "by this plugin, using the recovery phrase that belongs to it.";

    internal const string BackupWalletDirectoryDiffersOnlyInLetterCaseRefusal =
        "This backup's wallet directory is named for the same master fingerprint as the recovery phrase you "
        + "supplied but in different letter case, and this server's filesystem treats those as two different "
        + "directories. Restoring it would report success and then present an empty wallet, because rgb-lib would "
        + "create a new empty wallet at the name it expects while the backed-up data sat under the other name. No "
        + "wallet was created on the server and your backup file is unchanged. This archive was repacked after "
        + "this plugin produced it; restore an unmodified backup taken by this plugin instead.";

    internal const string RestoreFoundThisStoreAlreadyHoldsAWalletRecordRefusal =
        "This store already holds a wallet record, so this restore could not add one. Open this store's "
        + "RGB page to see the wallet it holds. If the Refresh button on that page brings that wallet "
        + "online, it is no longer held pending recovery and you can delete it there, after which this "
        + "backup can be restored again. Refresh can only do that when this server can open that "
        + "wallet's data and reach the indexer; a wallet whose data cannot be opened fails Refresh the "
        + "same way every time, cannot be deleted while it is held, and no further restore is accepted "
        + "for this store while its record exists, so clearing it needs someone with access to this "
        + "server to remove that record and its wallet data. The "
        + "wallet data this attempt unpacked was left on this server rather than deleted, because this "
        + "server cannot tell whether the record this store holds is the one this restore wrote, and "
        + "deleting the data under a record that is using it would leave an empty wallet in its place; "
        + "the BTCPay server log records where that data was left. Your backup file is undamaged and "
        + "still holds everything.";

    async Task RollBackTheJustPublishedRestoreAsync(RGBWallet wallet, string walletDataDir)
    {
        var theRowRemovalReturnedNormallyWhichIsTheOnlyProofTheRowIsGone = false;
        try
        {
            await using var ctx = _db.CreateContext();
            ctx.RGBWallets.Remove(wallet);
            await ctx.SaveChangesAsync(CancellationToken.None);
            theRowRemovalReturnedNormallyWhichIsTheOnlyProofTheRowIsGone = true;
        }
        catch (Exception dbEx)
        {
            _log.LogError(dbEx,
                "The removal of wallet row {Id} reported failure, which is not proof that it did not "
                + "commit, so this rollback claims nothing about that row. The restored wallet data is "
                + "left at {Dir}: deleting it under a row that survives would leave rgb-lib to create a "
                + "fresh empty wallet at that path and present it as the restored one",
                wallet.Id, walletDataDir);
        }
        try { _rgbLib.UnloadWallet(wallet.Id); } catch { }
        if (!theRowRemovalReturnedNormallyWhichIsTheOnlyProofTheRowIsGone) return;
        try { Directory.Delete(walletDataDir, true); }
        catch (Exception cleanupEx) { _log.LogDebug(cleanupEx, "Failed to clean up {Dir} after rolling back a restore", walletDataDir); }
    }

    internal const string IndexerUrlEnvironmentVariable = "RGB_ELECTRUM_URL";

    internal static string WhereToLookAfterARestoreThisServerRolledBack =>
        "Which of two states this store is now in is something you can see and this server cannot: open "
        + "this store's RGB page. If it offers the restore form, no wallet record is held for this store "
        + "and the step to take is to restore the same backup again. If instead it shows a wallet, a "
        + "record was written and no further restore is accepted for this store while that record "
        + "exists; the step to take is then the Refresh button on that page, which releases a wallet "
        + "held pending recovery once this server can open that wallet's data and reach the indexer. "
        + "If Refresh keeps failing the same way, that wallet cannot be deleted either, and someone "
        + "with access to this server has to remove that record and its wallet data before this backup "
        + "can be restored here again. Either way your "
        + "backup file is undamaged and still holds everything, so nothing is lost.";

    internal static string RefusalForRestoredWalletDataThatCouldNotBeBroughtOnline(
        string walletNetwork, Exception failure) =>
        "Your backup was decrypted and its wallet data was restored, but this server could not then open "
        + "that wallet and bring it online against the RGB indexer, so this server attempted to roll "
        + "that restore back. "
        + WhereToLookAfterARestoreThisServerRolledBack
        + " This is NOT evidence that your recovery phrase is wrong. The phrase was already checked "
        + "against this backup one step earlier and it matched the wallet directory the backup carries, "
        + "so keep that phrase. Several different faults reach this point and this server cannot tell "
        + $"them apart on its own. The usual one is that the RGB indexer for the {walletNetwork} network "
        + "was unreachable, was serving a different chain, or is misconfigured. The next most common is "
        + "a fault on this server's own storage — a full disk, a read-only or exhausted volume, or a "
        + "lock file that could not be written — because opening a wallet writes to this server before "
        + "it ever reaches the network. Wallet data inside the backup that rgb-lib cannot open also "
        + "reaches here, and so does a shutdown or another operation holding this wallet. Check free "
        + "space and write access on this server's storage first, then take whichever of the two steps "
        + "above this store's RGB page shows you, once the indexer is reachable. To point this server "
        + "at a different indexer, set the "
        + $"{IndexerUrlEnvironmentVariable} environment variable, which overrides the indexer for every "
        + "network, and restart BTCPay. The full underlying error is in the BTCPay server log, and that "
        + "entry is what identifies which of these it was."
        + (Controllers.RgbOperatorFacingFailure.MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime(failure)
            ? $" The underlying failure was: {failure.Message}"
            : $" The underlying failure was a {failure.GetType().Name}; its text names server filesystem "
              + "locations and is in the BTCPay server log rather than here.");

    static bool CanDiscardUnparseableRecoveryJournal(string journalPath, string dbPath) =>
        RgbSendRecoveryJournal.IsUnparseable(journalPath) && File.Exists(dbPath);

    public async Task DeleteWalletAsync(string walletId, CancellationToken ct = default)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);

        // WHY: hold the wallet's send lock across delete+commit so no concurrent send runs with
        // broken exclusivity; only evict the semaphore AFTER the row-delete has committed. If the
        // commit fails the wallet survives WITH its lock intact rather than losing exclusivity.
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        var releaseLock = true;
        var deletionCommitted = false;
        try
        {
            var walletDataDir = _rgbLib.GetWalletDataDir(walletId, wallet.Network);
            var walletDir = Path.Combine(walletDataDir, wallet.MasterFingerprint);
            var journalPath = RgbSendRecoveryJournal.PathFor(
                walletDataDir, wallet.MasterFingerprint);
            var dbPath = Path.Combine(walletDir, "rgb_lib_db");
            if (((await IsNeedsRecoveryAsync(walletId, ct) || File.Exists(journalPath))
                 && !CanDiscardUnparseableRecoveryJournal(journalPath, dbPath))
                || RgbNativeSendLease.Exists(walletDir))
                throw new RgbWalletQuarantinedException(
                    "wallet has pending durable recovery and cannot be deleted");

            // Publish the same cross-process marker as a send, then re-check after publication. This
            // closes the gap in which another server instance could stage a send between the guard and
            // row deletion, removing the only row the startup recovery sweep can discover.
            using var deleteLease = AcquireNativeSendParentLease(walletDir);
            try
            {
                if ((await IsNeedsRecoveryAsync(walletId, ct) || File.Exists(journalPath))
                    && !CanDiscardUnparseableRecoveryJournal(journalPath, dbPath))
                    throw new RgbWalletQuarantinedException(
                        "wallet became quarantined before deletion");
                if ((await FindOrphanedOutgoingBatchIndicesAsync(dbPath, ct)).Count != 0)
                    throw new RgbWalletQuarantinedException(
                        "wallet has staged outbound transfers and cannot be deleted");
                if (!_rgbLib.UnloadWallet(walletId))
                {
                    // Deletion never launches a child. False means cache construction/disposal was
                    // deferred, not that a native-send worker may still exist, so unwind this attempt.
                    deleteLease.ClearActiveMarker(walletDir);
                    throw new RgbWalletQuarantinedException(
                        "wallet remained busy while preparing deletion; retry after native access finishes");
                }

                await using var ctx = _db.CreateContext();
                var current = await ctx.RGBWallets.FindAsync([walletId], ct)
                    ?? throw new KeyNotFoundException($"wallet {walletId} not found");
                ctx.RGBWallets.Remove(current);
                try { await ctx.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException noRowWasThereForTheDeleteToAffect)
                {
                    _log.LogWarning(noRowWasThereForTheDeleteToAffect,
                        "The delete of wallet row {WalletId} affected no rows, so that row was already "
                        + "absent when the statement ran: an earlier execution of this same delete "
                        + "committed and lost its acknowledgement, or another instance removed the row. "
                        + "The record is gone either way, which is the outcome this deletion was for, so "
                        + "it completes rather than reporting a failure the caller would answer by "
                        + "restoring RGB payment configuration on a store that no longer has a wallet",
                        walletId);
                }
                deletionCommitted = true;

                _signerProvider.UnloadSigner(walletId);
                _addressCache.TryRemove(walletId, out _);
                deleteLease.ClearActiveMarker(walletDir);
            }
            finally
            {
                if (!deletionCommitted)
                {
                    try { deleteLease.ClearActiveMarker(walletDir); }
                    catch (Exception markerEx)
                    {
                        _log.LogDebug(markerEx,
                            "Failed to release the deletion marker for wallet {WalletId}", walletId);
                    }
                }
            }
        }
        catch (Exception ex) when (deletionCommitted)
        {
            // The authoritative row is already gone. Reporting failure would make the controller
            // compensate by re-enabling a store whose wallet no longer exists. Cleanup is best effort;
            // the orphaned data directory and marker are unreachable without a wallet row.
            _log.LogWarning(ex,
                "Wallet {WalletId} deletion committed but post-commit cleanup was incomplete",
                walletId);
        }
        catch (NativeSendChildUnreapedException)
        {
            releaseLock = false;
            throw;
        }
        finally { if (releaseLock) sendLock.Release(); }

        _sendLocks.TryRemove(walletId, out _);

        _log.LogInformation("deleted wallet {Id}, data dir left at {Dir}",
            walletId, _rgbLib.GetWalletDataDir(walletId, wallet.Network));
    }

    public async Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        try
        {
            var wallet = await GetWalletOrThrow(walletId, ct);
            var walletDir = Path.Combine(
                _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network), wallet.MasterFingerprint);
            using var operationLease = AcquireNativeSendParentLease(walletDir);
            try { return await SendBtcInternalAsync(walletId, destinationAddress, amountSats, feeRate, ct); }
            finally { operationLease.ClearActiveMarker(walletDir); }
        }
        finally { sendLock.Release(); }
    }

    internal static string RefusalForABroadcastThisServerCouldNotAccountFor(
        string txid, Exception broadcastFailure) =>
        "This server signed a Bitcoin transaction and could not confirm that it reached the network. "
        + (Controllers.RgbOperatorFacingFailure.MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime(
                broadcastFailure)
            ? $"The broadcast reported: {broadcastFailure.Message} "
            : "What the broadcast reported is in the BTCPay server log. ")
        + $"The transaction this server signed has id {txid}. Look that id up in a block explorer for "
        + "this wallet's network before sending again, because this server cannot tell you whether it "
        + "is there: if it is, the payment is already on its way and sending again would pay a second "
        + "time.";

    internal static bool TheIndexerReturnedExactlyThisTransaction(
        string rawHex, Transaction signed, Network network)
    {
        try
        {
            var returned = Transaction.Parse(rawHex.Trim(), network);
            return returned.GetHash() == signed.GetHash()
                && returned.GetWitHash() == signed.GetWitHash();
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static readonly TimeSpan BroadcastReconciliationDeadline = TimeSpan.FromSeconds(20);

    async Task<bool> TheNetworkAlreadyHoldsTheSignedTransactionAsync(
        string walletNetwork, Transaction signed)
    {
        var txid = signed.GetHash().ToString();
        try
        {
            using var deadline = new CancellationTokenSource(BroadcastReconciliationDeadline);
            var settings = RGBConfiguration.GetNetworkSettings(walletNetwork);
            using var probe = BitcoinChainClientFactory.Create(
                settings.ElectrumUrl, allowInsecure: NetworkSettings.AllowsPlainElectrum(walletNetwork));
            await probe.ConnectAsync(deadline.Token);
            var rawHex = await probe.GetRawTransactionAsync(txid, deadline.Token);
            return TheIndexerReturnedExactlyThisTransaction(
                rawHex, signed, NetworkHelper.GetNetwork(walletNetwork));
        }
        catch (Exception probeFailure)
        {
            _log.LogWarning(probeFailure,
                "Could not determine whether transaction {Txid} reached the network", txid);
            return false;
        }
    }

    internal static async Task<Transaction> ParentTransactionAsync(
        IBitcoinChainClient electrum,
        Dictionary<string, Transaction> cache,
        string expectedTxid,
        Network network,
        CancellationToken ct)
    {
        if (cache.TryGetValue(expectedTxid, out var cached))
            return cached;

        var rawHex = await electrum.GetRawTransactionAsync(expectedTxid, ct);
        var rawTx = Transaction.Parse(rawHex, network);
        if (rawTx.GetHash().ToString() != expectedTxid)
            throw new InvalidOperationException(
                $"Electrum returned transaction with wrong txid: expected {expectedTxid}, got {rawTx.GetHash()}");
        cache[expectedTxid] = rawTx;
        return rawTx;
    }

    internal static async Task<bool?> ConfirmationOfAsync(
        IBitcoinChainClient electrum,
        Dictionary<string, Transaction> cache,
        HashSet<Script> scriptsAlreadyAsked,
        Dictionary<(string Txid, int Vout), bool> confirmationByOutpoint,
        Outpoint outpoint,
        Network network,
        CancellationToken ct)
    {
        if (confirmationByOutpoint.TryGetValue((outpoint.Txid, outpoint.Vout), out var alreadyAnswered))
            return alreadyAnswered;

        var parent = await ParentTransactionAsync(electrum, cache, outpoint.Txid, network, ct);
        if (outpoint.Vout < 0 || outpoint.Vout >= parent.Outputs.Count)
            return null;

        var script = parent.Outputs[outpoint.Vout].ScriptPubKey;
        if (!scriptsAlreadyAsked.Add(script))
            return null;

        var rows = await electrum.ListUnspentWithConfirmationByScriptAsync(script, ct);
        foreach (var row in rows)
            confirmationByOutpoint[(row.Outpoint.Txid, row.Outpoint.Vout)] = row.ConfirmedInABlock;

        return ConfirmedBtcInputSelection.ConfirmationOf(outpoint, rows);
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

        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
        var allowsPlainElectrum = NetworkSettings.AllowsPlainElectrum(wallet.Network);
        using var electrum = BitcoinChainClientFactory.Create(networkSettings.ElectrumUrl, allowInsecure: allowsPlainElectrum);
        await electrum.ConnectAsync(ct);

        var rawTxCache = new Dictionary<string, Transaction>();
        var scriptsAlreadyAsked = new HashSet<Script>();
        var confirmationByOutpoint = new Dictionary<(string Txid, int Vout), bool>();

        var walk = await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            spendableUtxos
                .Select(u => new ConfirmedBtcInputSelection.Candidate(u.Utxo.Outpoint, u.Utxo.BtcAmount))
                .ToList(),
            amountSats,
            feeRate,
            (outpoint, token) =>
                ConfirmationOfAsync(
                    electrum, rawTxCache, scriptsAlreadyAsked, confirmationByOutpoint,
                    outpoint, network, token),
            ct);

        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(
            walk.Confirmed, amountSats, feeRate, walk.UnconfirmedSatsSkipped);

        var byOutpoint = spendableUtxos.ToDictionary(u => (u.Utxo.Outpoint.Txid, u.Utxo.Outpoint.Vout));
        var selected = choice.Inputs
            .Select(c => byOutpoint[(c.Outpoint.Txid, c.Outpoint.Vout)])
            .ToList();
        amountSats = choice.AmountSats;
        var fee = choice.Fee;
        var change = choice.Change;
        var hasChange = choice.HasChange;

        var changeAddress = BitcoinAddress.Create(
            await _rgbLib.GetAddressAsync(walletId, ct), network);

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
            var prevTx = await ParentTransactionAsync(
                electrum, rawTxCache, utxo.Utxo.Outpoint.Txid, network, ct);
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
            StrictAllowedScriptsOnly = true,
            RequireRgbVanillaKeychainInputs = true
        };

        var signedBase64 = await signer.SignPsbtAsync(psbt.ToBase64(), network, policy, ct);
        psbt = PSBT.Parse(signedBase64, network);

        var signedTx = psbt.ExtractTransaction();
        var localTxid = signedTx.GetHash().ToString();
        string broadcastTxid;
        try
        {
            broadcastTxid = await electrum.BroadcastTransactionAsync(signedTx.ToHex(), ct);
        }
        catch (Exception broadcastFailure)
        {
            if (!await TheNetworkAlreadyHoldsTheSignedTransactionAsync(wallet.Network, signedTx))
                throw new InvalidOperationException(
                    RefusalForABroadcastThisServerCouldNotAccountFor(localTxid, broadcastFailure),
                    broadcastFailure);
            _log.LogWarning(broadcastFailure,
                "Broadcast reply was lost for {Txid}, but the indexer holds that transaction", localTxid);
            broadcastTxid = localTxid;
        }
        if (!string.Equals(broadcastTxid, localTxid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Broadcast returned mismatched txid: expected {localTxid}, got {broadcastTxid}");
        var txid = localTxid;

        _log.LogInformation("Sent {Amount} sats to {Address}, txid={Txid}, fee={Fee}",
            amountSats, destinationAddress, txid, fee);

        // Inline write-ahead (already holds _sendLocks): a refresh failure leaves the wallet
        // quarantined (sync-pending) rather than silently proceeding with possibly-stale state.
        try { await _sendCoordinator.WriteAheadInlineAsync(walletId, () => _rgbLib.RefreshAsync(walletId, ct), ct); }
        catch (Exception ex) { _log.LogDebug(ex, "Post-send refresh failed"); }

        return (txid, amountSats, fee);
    }

    public async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default)
    {
        var sendLock = _sendLocks.GetOrAdd(walletId, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(ct);
        var releaseLock = true;
        try
        {
        return await SendAssetInternalAsync(walletId, rgbInvoice, assetId, amount, feeRate, ct);
        }
        catch (NativeSendChildUnreapedException)
        {
            // A parent handle that could not be evicted or a child that could not be reaped makes
            // continued in-process use indeterminate. Retaining this wallet's semaphore is the only
            // way to prove it cannot race refresh, settlement, or another send; the durable helper
            // marker lets restart recover safely. Other wallets use other locks.
            releaseLock = false;
            throw;
        }
        finally { if (releaseLock) sendLock.Release(); }
    }

    internal async Task<string> RunNativeSendIsolatedAsync(
        RGBWallet wallet, string operation, string? recipientMapJson, float feeRate,
        int minConfirmations, string? signedPsbt, CancellationToken ct)
    {
        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network);
        var leaseWalletDir = Path.Combine(walletDataDir, wallet.MasterFingerprint);
        var request = JsonSerializer.Serialize(new
        {
            DataDir = walletDataDir,
            BitcoinNetwork = NetworkHelper.MapNetworkToRgbLibFormat(wallet.Network),
            ElectrumUrl = RGBConfiguration.GetNetworkSettings(wallet.Network).ElectrumUrl,
            wallet.XpubVanilla,
            wallet.XpubColored,
            wallet.MasterFingerprint,
            LeaseWalletDir = leaseWalletDir,
            LeaseToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(leaseWalletDir),
            MaxAllocationsPerUtxo = ResolveAllocationsPerUtxo(wallet.MaxAllocationsPerUtxo),
            RecipientMapJson = recipientMapJson,
            FeeRate = feeRate,
            MinConfirmations = minConfirmations,
            SignedPsbt = signedPsbt
        });
        var limits = _cfg.ToNativeSendLimits();
        var result = await _nativeSendRunner.RunAsync(operation, request, leaseWalletDir,
            () => _rgbLib.UnloadWallet(wallet.Id), limits, ct);
        if (!result.ChildReaped)
            throw new NativeSendChildUnreapedException();
        if (result.Outcome == NativeSendOutcome.TimedOut)
            throw new NativeSendReapedFailureException(
                RefusalForANativeSendThatRanOutOfTime(operation, limits.Timeout));
        if (result.Outcome == NativeSendOutcome.KilledRam)
            throw new NativeSendReapedFailureException(
                RefusalForANativeSendThatReachedItsMemoryBudget(operation, limits.RamCapBytes));
        if (result.ExitCode != 0)
        {
            _log.LogError(
                "RGB {Operation} helper exited with code {ExitCode}; helper stderr with host paths "
                + "intact and key material redacted: {StdErr} (wallet data dir {WalletDataDir}, "
                + "lease wallet dir {LeaseWalletDir}, helper {HelperDll})",
                operation, result.ExitCode,
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? HelperWroteNothingToStdErr
                    : RgbNativeMessageSanitizer.Sanitize(result.StdErr),
                walletDataDir, leaseWalletDir, result.HelperDllHandedToTheDotnetHost);
            var redactedStdErr = RgbHelperStderrRedaction
                .ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheNativeSendHelper(
                    result.StdErr, walletDataDir, leaseWalletDir,
                    result.HelperDllHandedToTheDotnetHost)
                .Trim();
            throw new NativeSendReapedFailureException(
                string.IsNullOrWhiteSpace(redactedStdErr)
                || RgbBoundRefusal.AnExitStatusNoHelperInThisPluginReturnsOfItsOwnAccord(
                    result.ExitCode)
                    ? RefusalForANativeSendHelperWhoseExitCannotRuleOutTheBudgetsThisPluginGaveIt(
                        operation, result.ExitCode, redactedStdErr, limits.RamCapBytes)
                    : redactedStdErr);
        }
        if (string.IsNullOrWhiteSpace(result.StdOut))
            throw new NativeSendReapedFailureException(
                $"RGB {operation} helper returned no result");
        return result.StdOut;
    }

    internal const string HelperWroteNothingToStdErr = "(the helper wrote nothing to stderr)";

    internal const string WhatANativeSendRefusalCanHonestlySayAboutTheWalletsState =
        "Whether this attempt staged anything is recorded in the BTCPay server log and in this wallet's "
        + "own recovery state, so do not delete this wallet: the plugin finishes or fails a partly "
        + "staged transfer by itself.";

    internal static string RefusalForANativeSendThatRanOutOfTime(string operation, TimeSpan timeout) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"The RGB {operation} helper reached the {(int)timeout.TotalSeconds} second native "
            + "execution deadline and was stopped.",
            "That deadline covers the whole out-of-process helper, including the rgb-lib wallet "
            + "construction and the indexer handshake and chain sync every send pays before the native "
            + "call starts, so a slow or congested indexer can reach it on an ordinary send.",
            WhatANativeSendRefusalCanHonestlySayAboutTheWalletsState,
            "RGB_NATIVE_SEND_TIMEOUT_SECONDS",
            $"maximum {RGBConfiguration.NativeSendSecondsMax} seconds",
            "retry the send");

    internal static string RefusalForANativeSendThatReachedItsMemoryBudget(
        string operation, long ramCapBytes) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"The RGB {operation} helper reached the {ramCapBytes / (1024 * 1024)} MB native memory "
            + "limit and was stopped.",
            "That limit covers the whole out-of-process helper, including the rgb-lib wallet "
            + "construction and chain sync every send pays before the native call starts, so a wallet "
            + "holding many transfers or allocations can need more than the shipped budget. A wallet "
            + "whose helper is stopped here every time can never move its assets.",
            WhatANativeSendRefusalCanHonestlySayAboutTheWalletsState,
            "RGB_NATIVE_SEND_RAM_CAP_BYTES",
            $"maximum {RGBConfiguration.NativeSendRamMaxBytes / (1024 * 1024)} MB",
            "retry the send");

    internal static string RefusalForANativeSendHelperWhoseExitCannotRuleOutTheBudgetsThisPluginGaveIt(
        string operation, int? exitCode, string whatTheHelperPrintedBeforeItStopped, long ramCapBytes) =>
        $"The RGB {operation} helper stopped with "
        + RgbBoundRefusal.DescribeExitStatusForAnOperatorWithoutShellAccess(exitCode)
        + (string.IsNullOrWhiteSpace(whatTheHelperPrintedBeforeItStopped)
            ? " and wrote no error output at all."
            : $", after writing: {whatTheHelperPrintedBeforeItStopped.Trim()}")
        + " The helper applies this plugin's own memory and CPU budgets to itself before it constructs "
        + $"the rgb-lib wallet, and an allocation the {ramCapBytes / (1024 * 1024)} MB memory budget "
        + "refuses is answered inside the helper, which ends there without this server's watchdog ever "
        + "seeing it grow — so an exit like this one does not rule the memory budget out. A helper "
        + "killed from outside, by the memory the host or container allows BTCPay, ends the same way. "
        + "The limits to raise first are therefore the native send memory limit "
        + $"(RGB_NATIVE_SEND_RAM_CAP_BYTES, maximum "
        + $"{RGBConfiguration.NativeSendRamMaxBytes / (1024 * 1024)} MB) and the native send CPU limit "
        + "(RGB_NATIVE_SEND_CPU_LIMIT_SECONDS); restart BTCPay after changing either, then retry the "
        + "send. Not every exit that reaches this message is a budget, though: a status the helper "
        + "never returns of its own accord is also what a helper that could not start at all leaves "
        + "behind, which an incomplete or mismatched BTCPay installation causes and which no limit will "
        + $"fix. {WhatANativeSendRefusalCanHonestlySayAboutTheWalletsState} The BTCPay server log "
        + "records this attempt in full, and that entry is what separates the two.";

    async Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetInternalAsync(
        string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct)
    {
        var wallet = await GetWalletOrThrow(walletId, ct);
        var walletDataDir = _rgbLib.GetWalletDataDir(wallet.Id, wallet.Network);
        var recoveryJournal = RgbSendRecoveryJournal.PathFor(walletDataDir, wallet.MasterFingerprint);
        if (await IsNeedsRecoveryAsync(walletId, ct) || File.Exists(recoveryJournal))
            throw new RgbWalletQuarantinedException(
                "wallet is quarantined pending recovery — refusing to stage an asset send");
        var network = NetworkHelper.GetNetwork(wallet.Network);
        var allowPrivateEndpoints = wallet.Network.Equals("regtest", StringComparison.OrdinalIgnoreCase)
            && _cfg.AllowPrivateTransportEndpoints;

        if (string.IsNullOrWhiteSpace(rgbInvoice)
            || rgbInvoice.Length > TransportEndpointValidator.MaxRgbInvoiceLength)
            throw new InvalidOperationException(
                $"RGB invoice exceeds the {TransportEndpointValidator.MaxRgbInvoiceLength}-character limit");
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

        var leaseWalletDir = Path.Combine(walletDataDir, wallet.MasterFingerprint);
        using var operationLease = AcquireNativeSendParentLease(leaseWalletDir);
        RgbVerificationSnapshot? verificationSnapshot = null;
        var sendBeginMayHaveRun = false;
        var sendEndStarted = false;
        string? sentTxid = null;
        string? recoveryAdvisory = null;
        int? batchTransferIdx = null;
        string? sendEndTxid = null;
        var quarantineDischargeEarned = false;
        try
        {
            // The early check avoids expensive validation for an already-quarantined wallet. This
            // check is authoritative: the cross-process lease closes the gap before write-ahead.
            if (await IsNeedsRecoveryAsync(walletId, ct) || File.Exists(recoveryJournal))
                throw new RgbWalletQuarantinedException(
                    "wallet became quarantined before staging — refusing to send");
            // Snapshot the on-disk Stock BEFORE send_begin so the gate scans state untouched by
            // this send. The operation-wide lease stays published through both helper phases.
            verificationSnapshot = await _rgbLib.SnapshotVerificationStateAsync(walletId, ct);
            // Durable before send_begin: managed code may never receive its batch index, so restart
            // recovery must be driven by the wallet DB plus this phase marker, not a local variable.
            await SetNeedsRecoveryAsync(walletId, ct);
            RgbSendRecoveryJournal.Write(recoveryJournal, RgbSendRecoveryPhase.Staged);
            sendBeginMayHaveRun = true;
            var sendAssetRoundedFeeRate = SendAssetRoundedFeeRate(feeRate);
            var sendBeginResult = await RunNativeSendIsolatedAsync(
                wallet, "send-begin", recipientMap, sendAssetRoundedFeeRate, 1, signedPsbt: null, ct);

            var parsedSendBegin = JsonSerializer.Deserialize<SendBeginResult>(sendBeginResult)
                ?? throw new RgbIntentVerificationException("send_begin returned an unparseable result");
            batchTransferIdx = parsedSendBegin.BatchTransferIdx
                ?? throw new RgbIntentVerificationException("send_begin did not return a batch_transfer_idx");

            string gateVerifiedTxid;
            try
            {
                gateVerifiedTxid = await RunIntentGateAsync(walletId, wallet, network, rgbInvoice,
                    parsedSendBegin, amount, resolvedAssetId, verificationSnapshot, ct);
            }
            catch (Exception gateEx)
            {
                throw await FailStagedTransferForIntentRejectionAsync(
                    walletId, batchTransferIdx.Value, gateEx);
            }

            string signedPsbt;
            try
            {
                var unsignedPsbt = parsedSendBegin.Psbt.Trim('"');
                var changeAddr = BitcoinAddress.Create(await _rgbLib.GetAddressAsync(walletId, ct), network);
                signedPsbt = await SignPsbtLocallyAsync(walletId, unsignedPsbt, network,
                    new SigningPolicy
                    {
                        MaxUnknownOutputSats = 0,
                        MaxFeeSats = SendAssetMaxFeeSatsAtOneInput(sendAssetRoundedFeeRate),
                        MaxFeeSatsPerAdditionalInput =
                            SendAssetMaxFeeSatsPerAdditionalInput(sendAssetRoundedFeeRate),
                        AllowedScripts = new HashSet<Script> { changeAddr.ScriptPubKey },
                        MaxOutputCount = 10,
                        RequireUnfinalizedWitnessProgramInputs = true
                    }, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "SendAsset: signing failed after SendBegin for wallet {WalletId}", walletId);
                throw;
            }

            var finalizedPsbt = PSBT.Parse(signedPsbt, network);
            if (!finalizedPsbt.TryFinalize(out var finalizeErrors))
                throw new RgbIntentVerificationException(
                    $"signed RGB transaction could not be finalized: {string.Join("; ", finalizeErrors)}");
            var rawTransaction = finalizedPsbt.ExtractTransaction();
            var rawTransactionHex = rawTransaction.ToHex();
            var txid = rawTransaction.GetHash().ToString();

            if (!string.Equals(txid, gateVerifiedTxid, StringComparison.OrdinalIgnoreCase))
                throw await FailStagedTransferForIntentRejectionAsync(walletId, batchTransferIdx.Value,
                    new RgbIntentVerificationException(
                        "finalized RGB transaction id does not match the transaction id the intent gate "
                        + "verified — refusing to broadcast a transaction the gate never saw"));

            sendEndTxid = txid;
            try
            {
                // From this durable transition onward, failure is not assumed to mean that
                // consume_fascia did nothing. Persist the exact transaction and signed PSBT before
                // send_end so a crash can replay that exact native state transition without re-signing.
                RgbSendRecoveryJournal.WriteSendEnd(
                    recoveryJournal, batchTransferIdx.Value, rawTransactionHex, txid, signedPsbt);
                sendEndStarted = true;
                RgbSendRecoveryJournal.FsyncPreSendEndArtifacts(leaseWalletDir, txid);
                var sendEndResult = await RunNativeSendIsolatedAsync(
                    wallet, "send-end", recipientMapJson: null, sendAssetRoundedFeeRate, 1, signedPsbt, ct);
                ValidateSendEndTransactionId(sendEndResult, txid);
                RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
                    leaseWalletDir, txid, signedPsbt);
            }
            catch (RgbWalletQuarantinedException)
            {
                // NativeSendProcessRunner uses this type only when its pre-launch quiescence
                // callback fails. No child exists to reap, including in the send_end phase.
                throw;
            }
            catch
            {
                // send_end failure is indeterminate — leave quarantined + evict the handle.
                try
                {
                    if (!_rgbLib.UnloadWallet(walletId))
                        throw new NativeSendChildUnreapedException();
                }
                catch (NativeSendChildUnreapedException) { throw; }
                catch { }
                throw;
            }

            _log.LogInformation("SendAsset completed: {Ticker} amount={Amount}, txid={Txid}",
                asset.Ticker, amount, txid);

            // beta.30's non-donation flow deliberately waits for the recipient ACK. Refresh owns the
            // native broadcast transition; broadcasting the extracted Bitcoin transaction here would
            // bypass NACK protection and can strand the RGB allocation state.
            try
            {
                await _rgbLib.RefreshAsync(walletId, ct);
                // Journal before flag, for the reason given in ReconcileWalletRecoveryAsync. The flag is
                // not committed here at all: earning the discharge is recorded, and the commit itself
                // runs past the finally below, so both artifacts that make this wallet findable — the
                // journal and the worker marker — are already gone when NeedsRecovery goes false. That
                // keeps the marker's single conditional release in the finally, which is what guarantees
                // release on the paths that must keep the quarantine, and still leaves no window in which
                // a discharged wallet holds an artifact no scan will come back for.
                RgbSendRecoveryJournal.Delete(recoveryJournal);
                quarantineDischargeEarned = true;
            }
            catch (Exception ex)
            {
                recoveryAdvisory = SendRecoveryAdvisory;
                _log.LogWarning(ex, "SendAsset: post-send refresh failed — wallet {WalletId} left quarantined (sync-pending)", walletId);
                try { _rgbLib.UnloadWallet(walletId); } catch { }
            }

            sentTxid = txid;
        }
        catch (Exception sendException)
        {
            var acceptRecordedSendEnd = false;
            if (sendException is NativeSendChildUnreapedException)
            {
                // A native handle (parent or child) is still unconfirmed. Do not open the wallet
                // for cleanup concurrently; retain its journal, quarantine, and semaphore.
            }
            else if (sendException is RgbWalletQuarantinedException
                     && sendBeginMayHaveRun)
            {
                // The current helper was never launched. Its pre-launch quiescence check found an
                // existing parent-side native call or constructor. Keep the durable staged journal
                // for normal recovery, but preserve the typed error so SendAssetAsync releases its
                // semaphore. This applies to both helper phases; sendEndStarted means intent, not launch.
                try { _rgbLib.UnloadWallet(walletId); } catch { }
            }
            else if (sendBeginMayHaveRun && !sendEndStarted)
            {
                try
                {
                    if (!_rgbLib.UnloadWallet(walletId))
                        throw new NativeSendChildUnreapedException();
                    await ReconcileWalletRecoveryAsync(wallet, CancellationToken.None, operationLease);
                }
                catch (NativeSendChildUnreapedException) { throw; }
                catch (Exception recoveryEx)
                {
                    _log.LogError(recoveryEx,
                        "SendAsset: immediate staged-send cleanup failed for wallet {WalletId}; durable quarantine retained",
                        walletId);
                    try { _rgbLib.UnloadWallet(walletId); } catch { }
                }
            }
            else if (sendEndStarted)
            {
                try { _rgbLib.UnloadWallet(walletId); } catch { }
                try
                {
                    acceptRecordedSendEnd = await VerifyRecordedSendEndAsync(
                        FindOutgoingBatchRowAsync, Path.Combine(leaseWalletDir, "rgb_lib_db"),
                        batchTransferIdx!.Value, sendEndTxid, sendException, _log,
                        CancellationToken.None);
                }
                catch (Exception verificationException)
                {
                    _log.LogError(verificationException,
                        "SendAsset: recorded send_end verification failed for wallet {WalletId}", walletId);
                    acceptRecordedSendEnd = false;
                }
                if (acceptRecordedSendEnd)
                {
                    sentTxid = sendEndTxid;
                    recoveryAdvisory = SendRecoveryAdvisory;
                }
            }
            if (!acceptRecordedSendEnd) throw;
        }
        finally
        {
            if (verificationSnapshot != null)
                RgbStockDurability.DeleteSnapshot(verificationSnapshot.RootDir);
            // Absence of the journal means no staged mutation is indeterminate. Remove the marker
            // while the parent mutex is still held; otherwise restart recovery owns its cleanup.
            if (!File.Exists(recoveryJournal)) operationLease.ClearActiveMarker(leaseWalletDir);
        }

        if (quarantineDischargeEarned)
        {
            try
            {
                await ClearNeedsRecoveryAsync(walletId, ct);
            }
            catch (Exception ex)
            {
                recoveryAdvisory = SendRecoveryAdvisory;
                _log.LogWarning(ex, "SendAsset: quarantine discharge failed after cleanup — wallet {WalletId} left quarantined", walletId);
            }
        }

        return (sentTxid!, amount, resolvedAssetId, asset.Ticker, recoveryAdvisory);
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

        if (amount < 0 || asset.SpendableBalance < (ulong)amount)
            throw new InvalidOperationException(
                $"Insufficient {asset.Ticker} spendable balance: have {asset.SpendableBalance:N0}, need {amount:N0}");

        return (resolvedAssetId, asset);
    }

    internal static long EstimateTaprootFee(int numInputs, int numOutputs, float feeRate)
    {
        var vsize = 10.5 + numInputs * 57.5 + numOutputs * 43.0;
        return (long)Math.Ceiling(vsize * feeRate);
    }

    internal const float CreateUtxosFeeRate = 2.0f;
    internal const int CreateUtxosFeeCeilingMultiplier = 3;

    internal static long CreateUtxosMaxFeeSatsAtOneInput(int requestCount)
        => EstimateTaprootFee(1, requestCount + 1, CreateUtxosFeeRate) * CreateUtxosFeeCeilingMultiplier;

    internal static long CreateUtxosMaxFeeSatsPerAdditionalInput(int requestCount)
        => EstimateTaprootFee(2, requestCount + 1, CreateUtxosFeeRate) * CreateUtxosFeeCeilingMultiplier
           - CreateUtxosMaxFeeSatsAtOneInput(requestCount);

    internal const int SendAssetFeeShapeOutputCount = 2;
    internal const int SendAssetFeeCeilingMultiplier = 3;
    internal const int SendAssetFeeMarginalMultiplier = 2;

    internal static int SendAssetRoundedFeeRate(float feeRate) => (int)Math.Round(feeRate);

    internal static long SendAssetMaxFeeSatsAtOneInput(int feeRate)
        => EstimateTaprootFee(1, SendAssetFeeShapeOutputCount, feeRate) * SendAssetFeeCeilingMultiplier;

    internal static long SendAssetMaxFeeSatsPerAdditionalInput(int feeRate)
        => (EstimateTaprootFee(2, SendAssetFeeShapeOutputCount, feeRate)
            - EstimateTaprootFee(1, SendAssetFeeShapeOutputCount, feeRate))
           * SendAssetFeeMarginalMultiplier;

    async Task<RGBWallet> GetWalletOrThrow(string id, CancellationToken ct = default) =>
        await GetWalletAsync(id, ct) ?? throw new KeyNotFoundException($"wallet {id} not found");

    async Task<RgbIntentVerificationException> FailStagedTransferForIntentRejectionAsync(
        string walletId, int batchTransferIdx, Exception rejection)
    {
        _log.LogError(rejection, "SendAsset: intent gate rejected transfer {Idx} for wallet {WalletId}", batchTransferIdx, walletId);
        try { await _rgbLib.FailTransfersAsync(walletId, batchTransferIdx, false, true, CancellationToken.None); }
        catch (Exception failEx) { _log.LogError(failEx, "SendAsset: FailTransfers failed after gate rejection for wallet {WalletId}", walletId); }
        return rejection as RgbIntentVerificationException
            ?? new RgbIntentVerificationException(
                $"RGB send intent verification failed: {rejection.Message}", rejection);
    }

    async Task<string> RunIntentGateAsync(string walletId, RGBWallet wallet, Network network, string rgbInvoice,
        SendBeginResult parsedSendBegin, long amount, string operatorAssetId,
        RgbVerificationSnapshot snapshot, CancellationToken ct)
    {
        var details = parsedSendBegin.Details
            ?? throw new RgbIntentVerificationException("send_begin returned no details");

        if (details.IsDonation)
            throw new RgbIntentVerificationException("send_begin reports a donation transfer, which is not supported");

        var unsignedPsbt = PSBT.Parse(parsedSendBegin.Psbt.Trim('"'), network);
        var unsignedTxid = unsignedPsbt.GetGlobalTransaction().GetHash().ToString();

        var opret = RgbPsbtInspector.ReadOpretCommitment(unsignedPsbt);
        var opretHex = Convert.ToHexString(opret).ToLowerInvariant();

        RgbSighashGuard.EnsureAllInputsAllowed(unsignedPsbt);

        var decode = RgbVerifyNative.DecodeInvoice(rgbInvoice);

        var consignmentPath = await _rgbLib.CreateConsignmentsAsync(walletId, parsedSendBegin.Psbt, ct);

        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
        var validate = RgbVerifyNative.ValidateV2(new RgbValidateV2Request
        {
            ConsignmentPath = consignmentPath,
            FasciaPath = details.FasciaPath,
            UnsignedTxid = unsignedTxid,
            OpretCommitmentBytes = opretHex,
            Entropy = details.Entropy,
            IndexerUrl = networkSettings.ElectrumUrl,
            Network = RgbChainNetMapper.PrefixForNetwork(network),
            StockDir = snapshot.StockDir,
            BdkStorePath = snapshot.BdkStorePath,
            AccountXpubVanilla = wallet.XpubVanilla,
            AccountXpubColored = wallet.XpubColored,
            MasterFingerprint = wallet.MasterFingerprint
        });

        var stagedEndpoints = RgbTransferDataReader.ReadTransportEndpoints(details.FasciaPath);

        var signer = await _signerProvider.GetSignerAsync(walletId, ct) as MemoryWalletSigner
            ?? throw new RgbIntentVerificationException("no local signer available for intent verification");

        var allowsPlainElectrum = NetworkSettings.AllowsPlainElectrum(wallet.Network);
        using var chainClient = BitcoinChainClientFactory.Create(networkSettings.ElectrumUrl, allowInsecure: allowsPlainElectrum);
        await chainClient.ConnectAsync(ct);

        await RgbIntentVerifier.VerifyAsync(decode, validate, unsignedPsbt, unsignedTxid,
            signer, network, amount, operatorAssetId, stagedEndpoints, chainClient, ct);

        return unsignedTxid;
    }

    internal static string ExtractPsbt(string nativeResult)
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
