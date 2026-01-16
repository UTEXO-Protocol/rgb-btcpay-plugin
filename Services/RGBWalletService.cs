using BTCPayServer.Plugins.RGB.Data;
using BTCPayServer.Plugins.RGB.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RGB.Services;

public class RGBWalletService
{
    readonly RgbNodeClient _node;
    readonly RgbSdkService _sdk;
    readonly RGBPluginDbContextFactory _db;
    readonly RGBConfiguration _cfg;
    readonly MnemonicProtectionService _mnemonicProtection;
    readonly RgbWalletSignerProvider _signerProvider;
    readonly ILogger<RGBWalletService> _log;

    public RGBWalletService(RgbNodeClient node, RgbSdkService sdk, RGBPluginDbContextFactory db, 
        RGBConfiguration cfg, MnemonicProtectionService mnemonicProtection, 
        RgbWalletSignerProvider signerProvider, ILogger<RGBWalletService> log)
    {
        _node = node; _sdk = sdk; _db = db; _cfg = cfg; 
        _mnemonicProtection = mnemonicProtection; 
        _signerProvider = signerProvider;
        _log = log;
    }

    public async Task<RGBWallet> CreateWalletAsync(string storeId, string? name = null)
    {
        var keys = await _node.GenerateKeysAsync();
        var network = GetNetwork(_cfg.Network);

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
        await ctx.SaveChangesAsync();

        _signerProvider.RegisterSigner(wallet.Id, keys.Mnemonic, network);

        await _node.RegisterAsync(GetCredentials(wallet));
        _log.LogInformation("created wallet {Id} for {Store}", wallet.Id, storeId);
        return wallet;
    }
    
    static Network GetNetwork(string network) => network.ToLowerInvariant() switch
    {
        "mainnet" or "main" => Network.Main,
        "testnet" or "test" => Network.TestNet,
        "signet" => Network.GetNetwork("signet") ?? Network.TestNet,
        _ => Network.RegTest
    };

    public async Task<RGBWallet?> GetWalletAsync(string id)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FindAsync(id);
    }

    public async Task<RGBWallet?> GetWalletForStoreAsync(string storeId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBWallets.FirstOrDefaultAsync(w => w.StoreId == storeId);
    }

    public RGBWalletCredentials GetCredentials(RGBWallet w) => new()
    {
        XpubVanilla = w.XpubVanilla, 
        XpubColored = w.XpubColored,
        MasterFingerprint = w.MasterFingerprint,
        Network = w.Network
    };

    public async Task<BtcBalance> GetBtcBalanceAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        return await _node.GetBtcBalanceAsync(GetCredentials(w));
    }

    public async Task<int> CreateColorableUtxosAsync(string walletId, int count = 5, int size = 10000)
    {
        var w = await GetWalletOrThrow(walletId);
        var c = GetCredentials(w);
        var network = GetNetwork(_cfg.Network);

        try
        {
            var psbt = await _node.CreateUtxosBeginAsync(c, count, size);
            if (string.IsNullOrEmpty(psbt)) return 0;

            var signed = await SignPsbtLocallyAsync(walletId, psbt, network);
            await _node.CreateUtxosEndAsync(c, signed);
            return count;
        }
        catch (Exception ex) when (ex.Message.Contains("AlreadyAvailable", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }
    
    async Task<string> SignPsbtLocallyAsync(string walletId, string psbt, Network network)
    {
        var signer = await _signerProvider.GetSignerAsync(walletId);
        if (signer == null)
        {
            throw new InvalidOperationException($"No local signer available for wallet {walletId}. Keys may not be loaded.");
        }
        
        _log.LogDebug("Signing PSBT locally for wallet {WalletId}", walletId);
        return await signer.SignPsbtAsync(psbt, network);
    }

    public async Task<int> GetColorableUtxoCountAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        var unspents = await _node.ListUnspentsAsync(GetCredentials(w));
        return unspents.Count(u => u.Utxo.Colorable && u.RgbAllocations.Count == 0);
    }

    public async Task EnsureColorableUtxosAsync(string walletId, int min = 2)
    {
        var have = await GetColorableUtxoCountAsync(walletId);
        if (have >= min) return;
        
        try { await CreateColorableUtxosAsync(walletId, min - have + 2); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "couldnt auto-create utxos (have {N})", have);
            if (have == 0) throw new InvalidOperationException("no colorable utxos - fund wallet first");
        }
    }

    public async Task<List<RgbAsset>> ListAssetsAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        return await _node.ListAssetsAsync(GetCredentials(w));
    }

    public async Task<AssetBalance> GetAssetBalanceAsync(string wid, string assetId)
    {
        var w = await GetWalletOrThrow(wid);
        return await _node.GetAssetBalanceAsync(GetCredentials(w), assetId);
    }

    public async Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0)
    {
        var w = await GetWalletOrThrow(walletId);
        return await _node.IssueAssetNiaAsync(GetCredentials(w), ticker, name, [amt], precision);
    }

    public async Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null)
    {
        var w = await GetWalletOrThrow(walletId);
        
        long? expTs = expiration.HasValue ? DateTimeOffset.UtcNow.Add(expiration.Value).ToUnixTimeSeconds() : null;
        var resp = await _node.BlindReceiveAsync(GetCredentials(w), assetId, amount, expTs);

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
        await ctx.SaveChangesAsync();
        return inv;
    }

    public async Task<RGBInvoice?> GetInvoiceByRecipientIdAsync(string recipientId)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBInvoices.FirstOrDefaultAsync(i => i.RecipientId == recipientId);
    }

    public async Task RefreshWalletAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        await _node.RefreshAsync(GetCredentials(w));
    }

    public async Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null)
    {
        var w = await GetWalletOrThrow(walletId);
        return await _node.ListTransfersAsync(GetCredentials(w), assetId);
    }

    public async Task<List<RGBInvoice>> CheckIncomingTransfersAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        var c = GetCredentials(w);
        
        await _node.RefreshAsync(c);
        var transfers = await _node.ListTransfersAsync(c);
        
        var received = transfers.Where(t => t.Status == 2 && t.Kind is 1 or 2).ToList();
        if (received.Count == 0) return [];

        await using var ctx = _db.CreateContext();
        var settled = new List<RGBInvoice>();
        
        foreach (var tx in received)
        {
            if (string.IsNullOrEmpty(tx.RecipientId)) continue;
            
            var inv = await ctx.RGBInvoices.FirstOrDefaultAsync(i => 
                i.RecipientId == tx.RecipientId && i.Status == RGBInvoiceStatus.Pending);
            if (inv == null) continue;

            inv.Status = RGBInvoiceStatus.Settled;
            inv.SettledAt = DateTimeOffset.UtcNow;
            inv.Txid = tx.Txid;
            inv.ReceivedAmount = tx.Amount;
            settled.Add(inv);
            _log.LogInformation("invoice {Id} settled ({Txid})", inv.Id, tx.Txid);
        }

        if (settled.Count > 0) await ctx.SaveChangesAsync();
        return settled;
    }

    public async Task FailExpiredTransfersAsync(string walletId)
    {
        var w = await GetWalletOrThrow(walletId);
        await _node.FailTransfersAsync(GetCredentials(w));
    }

    async Task<RGBWallet> GetWalletOrThrow(string id) =>
        await GetWalletAsync(id) ?? throw new KeyNotFoundException($"wallet {id} not found");
}
