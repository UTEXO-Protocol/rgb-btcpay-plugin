using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbVanillaReservationStartupProbe : IHostedService
{
    readonly RGBPluginDbContextFactory _db;
    readonly RGBWalletService _wallets;
    readonly ILogger<RgbVanillaReservationStartupProbe> _log;
    CancellationTokenSource? _cts;
    Task? _probe;

    public RgbVanillaReservationStartupProbe(
        RGBPluginDbContextFactory db,
        RGBWalletService wallets,
        ILogger<RgbVanillaReservationStartupProbe> log)
    {
        _db = db;
        _wallets = wallets;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _probe = ProbeAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_probe != null)
        {
            try { await _probe; }
            catch (OperationCanceledException) { }
        }
    }

    async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var ctx = _db.CreateContext();
            var walletIds = await ctx.RGBWallets.Where(w => w.IsActive).Select(w => w.Id).ToListAsync(ct);
            foreach (var walletId in walletIds)
            {
                if (ct.IsCancellationRequested) return;
                try { await ReportAsync(walletId, ct); }
                catch (Exception ex)
                {
                    _log.LogDebug(ex,
                        "Wallet {WalletId}: pending vanilla reservation probe failed", walletId);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "pending vanilla reservation probe failed");
        }
    }

    async Task ReportAsync(string walletId, CancellationToken ct)
    {
        var report = await _wallets.GetVanillaReservationReportAsync(walletId, ct);
        switch (report.State)
        {
            case RgbVanillaReservationState.LiveAndConstraining:
                _log.LogWarning(
                    "Wallet {WalletId}: {Count} reserved vanilla outpoint(s) are still unspent and are excluded from coin selection, so automatic colorable-UTXO creation cannot fund itself and the spendable BTC balance reads low. Remedy: send BTC from this wallet to an address of your own, then press Refresh so the wallet re-syncs — the reserved outpoints are spent by that transaction and normal operation resumes.",
                    walletId, report.StillUnspent.Count);
                break;
            case RgbVanillaReservationState.InertAlreadyRecovered:
                _log.LogInformation(
                    "Wallet {WalletId}: {Count} reserved vanilla outpoint(s) remain recorded but are all already spent, so they exclude nothing and constrain no spend. This is the expected residue of the self-send remedy and is not a fault.",
                    walletId, report.Reserved.Count);
                break;
            case RgbVanillaReservationState.Unknown:
                _log.LogInformation(
                    "Wallet {WalletId}: {Count} reserved vanilla outpoint(s) are recorded but their spent-ness cannot be determined right now, so no conclusion is drawn.",
                    walletId, report.Reserved.Count);
                break;
        }
    }
}
