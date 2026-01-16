using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RGB.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RGBPluginDbContext>
{
    public RGBPluginDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<RGBPluginDbContext>();
        builder.UseNpgsql("Host=localhost;Database=btcpay_design;Username=postgres;Password=postgres",
            o => o.MigrationsAssembly("BTCPayServer.Plugins.RGB"));
        
        return new RGBPluginDbContext(builder.Options);
    }
}


