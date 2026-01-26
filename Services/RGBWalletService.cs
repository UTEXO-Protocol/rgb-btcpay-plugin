using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RGBWalletService
{
    readonly IRgbLibService _rgbLib;
    readonly RGBPluginDbContextFactory _db;
    readonly RGBConfiguration _cfg;
    readonly MnemonicProtectionService _mnemonicProtection;
    readonly RgbWalletSignerProvider _signerProvider;
    readonly ILogger<RGBWalletService> _log;

    public RGBWalletService(
        IRgbLibService rgbLib,
        RGBPluginDbContextFactory db,
        RGBConfiguration cfg,
        MnemonicProtectionService mnemonicProtection,
        RgbWalletSignerProvider signerProvider,
        ILogger<RGBWalletService> log)
    {
        _rgbLib = rgbLib;
        _db = db;
        _cfg = cfg;
        _mnemonicProtection = mnemonicProtection;
        _signerProvider = signerProvider;
        _log = log;
    }

    public async Task<RGBWallet> CreateWalletAsync(string storeId, string? name = null, CancellationToken ct = default)
    {
        var keys = _rgbLib.GenerateKeys(_cfg.Network);
        var network = NetworkHelper.GetNetwork(_cfg.Network);

        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            Name = name ?? "RGB Wallet",
            XpubVanilla = keys.AccountXpubVanilla,
            XpubColored = keys.AccountXpubColored,
            MasterFingerprint = keys.MasterFingerprint,
            EncryptedMnemonic = _mnemonicProtection.Protect(keys.Mnemonic),
            Network = _cfg.Network,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await using var ctx = _db.CreateContext();
        ctx.RGBWallets.Add(wallet);
        await ctx.SaveChangesAsync(ct);

        _signerProvider.RegisterSigner(wallet.Id, keys.Mnemonic, network);
        ClearSensitiveString(keys.Mnemonic);

        _log.LogInformation("created wallet {Id} for {Store}", wallet.Id, storeId);
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
        return await _rgbLib.GetAddressAsync(walletId, ct);
    }

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.GetBtcBalanceAsync(walletId, ct);
    }

    public async Task<int> CreateColorableUtxosAsync(string walletId, int count = 5, int size = 10000, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        var network = NetworkHelper.GetNetwork(_cfg.Network);

        try
        {
            var psbt = await _rgbLib.CreateUtxosBeginAsync(walletId, count, size, 2.0f, ct);
            if (string.IsNullOrEmpty(psbt)) return 0;

            var signed = await SignPsbtLocallyAsync(walletId, psbt, network, ct);
            await _rgbLib.CreateUtxosEndAsync(walletId, signed, ct);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(ex, "UTXOs already available for wallet {WalletId}", walletId);
            return 0;
        }
    }

    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network, CancellationToken ct = default)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId, ct);
        if (signer == null)
        {
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");
        }

        _log.LogDebug("Signing PSBT locally for wallet {WalletId}", walletId);
        return await signer.SignPsbtAsync(psbt, network, ct);
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListAssetsAsync(walletId, ct);
    }

    public async Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListUnspentsAsync(walletId, ct);
    }

    public async Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.IssueAssetNiaAsync(walletId, ticker, name, [amt], precision, ct);
    }

    public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);

        long? expTs = expiration.HasValue ? DateTimeOffset.UtcNow.Add(expiration.Value).ToUnixTimeSeconds() : null;
        var resp = await _rgbLib.BlindReceiveAsync(walletId, assetId, amount, expTs, ct);

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
    }

    public async Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default)
    {
        await GetWalletOrThrow(walletId, ct);
        return await _rgbLib.ListTransfersAsync(walletId, assetId, ct);
    }

    async Task<RGBWallet> GetWalletOrThrow(string id, CancellationToken ct = default) =>
        await GetWalletAsync(id, ct) ?? throw new KeyNotFoundException($"wallet {id} not found");

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining | 
        System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    static void ClearSensitiveString(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        unsafe
        {
            fixed (char* ptr = value)
            {
                for (int i = 0; i < value.Length; i++)
                    ptr[i] = '\0';
            }
        }
    }
}



