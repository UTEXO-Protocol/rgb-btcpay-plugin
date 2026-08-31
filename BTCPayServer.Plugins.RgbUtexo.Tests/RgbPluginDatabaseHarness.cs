using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

internal sealed class RgbPluginDatabaseHarness : IAsyncDisposable
{
    internal const string ThrowawayDatabasePrefix = "rgb_plugin_test_";
    const string AdminConnectionStringEnvironmentVariable = "RGB_TEST_POSTGRES";

    const string DefaultAdminConnectionString =
        "User ID=postgres;Host=127.0.0.1;Port=6512;Database=postgres;Password=postgres";

    readonly string _adminConnectionString;
    readonly string _baseDir;

    internal string DatabaseName { get; }
    internal RGBPluginDbContextFactory Factory { get; }
    internal RGBConfiguration Configuration { get; }

    RgbPluginDatabaseHarness(string adminConnectionString, string databaseName, string baseDir)
    {
        _adminConnectionString = adminConnectionString;
        _baseDir = baseDir;
        DatabaseName = databaseName;
        Configuration = new RGBConfiguration(baseDir);

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName };
        Factory = new RGBPluginDbContextFactory(
            Options.Create(new DatabaseOptions { ConnectionString = builder.ConnectionString }));
    }

    internal static async Task<RgbPluginDatabaseHarness> CreateAsync(CancellationToken ct = default)
    {
        var adminConnectionString =
            Environment.GetEnvironmentVariable(AdminConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
            adminConnectionString = DefaultAdminConnectionString;

        try
        {
            await using var probe = new NpgsqlConnection(adminConnectionString);
            await probe.OpenAsync(ct);
        }
        catch (Exception fault)
        {
            Assert.Fail(
                $"no PostgreSQL reachable for the plugin database harness ({fault.GetType().Name}: {fault.Message}). "
                + "These rows are the only ones that exercise the operator grant through a real relational "
                + "provider, so an unreachable database must fail rather than quietly skip. Start the dev "
                + "instance with `docker start btcpay-postgres`, or point "
                + AdminConnectionStringEnvironmentVariable + " at another server. The harness creates and "
                + "drops its own throwaway database named " + ThrowawayDatabasePrefix
                + "<guid> and never opens the btcpayserver database.");
        }

        var databaseName = ThrowawayDatabasePrefix + Guid.NewGuid().ToString("N");
        var baseDir = Path.Combine(Path.GetTempPath(), "rgb-plugin-db-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        return new RgbPluginDatabaseHarness(adminConnectionString, databaseName, baseDir);
    }

    internal async Task RunPluginMigrationsAsync(CancellationToken ct = default)
    {
        var runner = new RGBPluginMigrationRunner(
            Factory, null!, null!, Configuration, NullLogger<RGBPluginMigrationRunner>.Instance);
        await runner.ExecuteAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        Assert.StartsWith(ThrowawayDatabasePrefix, DatabaseName);
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
        }

        try { Directory.Delete(_baseDir, true); } catch { }
    }
}
