using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BTCPayServer.Plugins.RGB.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RGBPluginDbContext>
{
    public RGBPluginDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BTCPAY_POSTGRES") 
            ?? "Host=localhost;Database=btcpay_design;Username=postgres";
        
        var builder = new DbContextOptionsBuilder<RGBPluginDbContext>();
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly("BTCPayServer.Plugins.RGB"));
        
        return new RGBPluginDbContext(builder.Options);
    }
}


