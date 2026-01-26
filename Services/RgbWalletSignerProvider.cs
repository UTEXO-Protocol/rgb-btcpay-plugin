using System.Collections.Concurrent;
using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbWalletSignerProvider : IHostedService, IRgbWalletSignerProvider
{
    readonly RGBPluginDbContextFactory _dbFactory;
    readonly MnemonicProtectionService _mnemonicProtection;
    readonly RGBConfiguration _config;
    readonly ILogger<RgbWalletSignerProvider> _logger;
    
    readonly ConcurrentDictionary<string, IRgbWalletSigner> _signers = new();
    
    TaskCompletionSource _started = new();

    public RgbWalletSignerProvider(
        RGBPluginDbContextFactory dbFactory,
        MnemonicProtectionService mnemonicProtection,
        RGBConfiguration config,
        ILogger<RgbWalletSignerProvider> logger)
    {
        _dbFactory = dbFactory;
        _mnemonicProtection = mnemonicProtection;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            
            var wallets = await ctx.RGBWallets
                .Where(w => w.IsActive && !string.IsNullOrEmpty(w.EncryptedMnemonic))
                .Select(w => new { w.Id, w.EncryptedMnemonic, w.Network })
                .ToListAsync(cancellationToken);
            
            var network = NetworkHelper.GetNetwork(_config.Network);
            
            foreach (var wallet in wallets)
            {
                try
                {
                    LoadWalletSigner(wallet.Id, wallet.EncryptedMnemonic, network);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load signer for wallet {WalletId}", wallet.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load wallet signers");
        }
        
        _started.SetResult();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var signer in _signers.Values)
        {
            try { signer.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose signer"); }
        }
        _signers.Clear();
        _started = new TaskCompletionSource();
        return Task.CompletedTask;
    }

    public async Task<bool> CanHandleAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await _started.Task;
        return _signers.ContainsKey(walletId);
    }

    public async Task<IRgbWalletSigner?> GetSignerAsync(string walletId, CancellationToken cancellationToken = default)
    {
        await _started.Task;
        return _signers.TryGetValue(walletId, out var signer) ? signer : null;
    }

    public void LoadWalletSigner(string walletId, string encryptedMnemonic, Network network)
    {
        var mnemonic = _mnemonicProtection.Unprotect(encryptedMnemonic);
        
        if (string.IsNullOrEmpty(mnemonic))
            return;
        
        var signer = new MemoryWalletSigner(mnemonic, network, _logger);
        _signers[walletId] = signer;
    }

    public void RegisterSigner(string walletId, string mnemonic, Network network)
    {
        var signer = new MemoryWalletSigner(mnemonic, network, _logger);
        _signers[walletId] = signer;
    }

    public void UnloadSigner(string walletId)
    {
        if (_signers.TryRemove(walletId, out var signer))
        {
            try { signer.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose signer for wallet {WalletId}", walletId); }
        }
    }
}
