using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Data;

public class RGBPluginMigrationRunner : IStartupTask
{
    private readonly RGBPluginDbContextFactory _dbContextFactory;

    public RGBPluginMigrationRunner(RGBPluginDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        await ctx.Database.MigrateAsync(cancellationToken);
        
        await ctx.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "RGB_Wallets" 
            ADD COLUMN IF NOT EXISTS "MaxAllocationsPerUtxo" integer NOT NULL DEFAULT 10
            """, cancellationToken);
    }
}


