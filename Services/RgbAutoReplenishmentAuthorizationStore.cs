using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbAutoReplenishmentAuthorizationStore
{
    readonly RGBPluginDbContextFactory _db;

    public RgbAutoReplenishmentAuthorizationStore(RGBPluginDbContextFactory db) => _db = db;

    public static bool IsGranted(RGBStoreAutoReplenishment? row, string configuredWalletId)
        => row != null
           && row.Decision == RgbAutoReplenishmentDecision.Granted
           && row.DecidedForWalletId != null
           && string.Equals(row.DecidedForWalletId, configuredWalletId, StringComparison.Ordinal);

    public async Task<RGBStoreAutoReplenishment?> FindAsync(string storeId, CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        return await ctx.RGBStoreAutoReplenishments.AsNoTracking()
            .FirstOrDefaultAsync(r => r.StoreId == storeId, ct);
    }

    public async Task<bool> IsGrantedForWalletAsync(
        string storeId, string configuredWalletId, CancellationToken ct = default)
        => IsGranted(await FindAsync(storeId, ct), configuredWalletId);

    public async Task RecordDecisionAsync(
        string storeId,
        string configuredWalletId,
        RgbAutoReplenishmentDecision decision,
        string? decidedBy,
        CancellationToken ct = default)
    {
        await using var ctx = _db.CreateContext();
        var row = await ctx.RGBStoreAutoReplenishments.FirstOrDefaultAsync(r => r.StoreId == storeId, ct);
        if (row == null)
        {
            row = new RGBStoreAutoReplenishment { StoreId = storeId };
            ctx.RGBStoreAutoReplenishments.Add(row);
        }
        row.Decision = decision;
        row.DecidedForWalletId = decision == RgbAutoReplenishmentDecision.Granted ? configuredWalletId : null;
        row.DecidedAt = DateTimeOffset.UtcNow;
        row.DecidedBy = decidedBy;
        await ctx.SaveChangesAsync(ct);
    }
}
