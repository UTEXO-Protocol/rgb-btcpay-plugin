using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Data.Sqlite;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbVanillaReservationRealSchemaTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public async Task PinnedRgbLibBeta30_CarriesTheTwoTablesTheInspectorQueriesAndNoReservedRows()
    {
        var restoredJson = RgbLibWallet.RestoreKeys("Regtest", TestMnemonic);
        using var restored = JsonDocument.Parse(restoredJson);
        var root = restored.RootElement;
        var fingerprint = root.GetProperty("master_fingerprint").GetString()!;
        var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-reservation-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var configJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data_dir"] = dataDir,
            ["bitcoin_network"] = "Regtest",
            ["database_type"] = "Sqlite",
            ["max_allocations_per_utxo"] = 5,
            ["supported_schemas"] = new[] { "Nia", "Cfa" }
        });
        var keysJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["account_xpub_vanilla"] = root.GetProperty("account_xpub_vanilla").GetString()!,
            ["account_xpub_colored"] = root.GetProperty("account_xpub_colored").GetString()!,
            ["master_fingerprint"] = fingerprint,
            ["vanilla_keychain"] = (int?)null,
            ["mnemonic"] = (string?)null
        });

        try
        {
            using (var wallet = new RgbLibWallet(configJson, keysJson)) { }

            var dbPath = Path.Combine(dataDir, fingerprint, "rgb_lib_db");
            Assert.True(File.Exists(dbPath), $"the pinned binding wrote no rgb_lib_db at {dbPath}");

            var tables = ReadTableNames(dbPath);
            foreach (var table in new[]
                     {
                         RgbVanillaReservationInspector.ReservedTxoTable,
                         RgbVanillaReservationInspector.WalletTransactionTable
                     })
            {
                Assert.True(tables.Contains(table),
                    $"the pinned rgb-lib binding's own database has no '{table}' table. The inspector "
                    + "queries that name, so a misspelling or an upstream rename makes it silently report "
                    + $"'clean' for every wallet. Tables present: {string.Join(", ", tables.Order())}");
            }

            Assert.Empty(await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(dbPath));
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        }
    }

    static HashSet<string> ReadTableNames(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        };
        using var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
