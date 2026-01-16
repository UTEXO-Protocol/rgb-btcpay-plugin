using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RGB.Data;

public class RGBPluginDbContextFactory : BaseDbContextFactory<RGBPluginDbContext>
{
    public RGBPluginDbContextFactory(IOptions<DatabaseOptions> options) : base(options, "BTCPayServer.Plugins.RGB")
    {
    }

    public override RGBPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<RGBPluginDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new RGBPluginDbContext(builder.Options);
    }
}

