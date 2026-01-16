using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RGB.Data;

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
    }
}


