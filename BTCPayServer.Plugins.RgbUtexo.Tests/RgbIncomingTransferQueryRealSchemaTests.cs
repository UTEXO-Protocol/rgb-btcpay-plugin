using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbIncomingTransferQueryRealSchemaTests
{
    const string TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    // The bounded-transfer query is the only way an asset-bound RGB invoice is ever seen to be paid, and
    // it is hand-written SQL against rgb-lib's private SQLite schema. BoundedTransferQueryTests exercises
    // it against a schema the TEST creates, so it certified `a.issued_supply` — a column the pinned
    // beta.30 does not have, because issued_supply is the name rgb-lib uses in its JSON, not in its
    // database, where the column is initial_supply. Running BTCPay showed the cost: every sweep logged
    // "no such column: a.issued_supply" as a warning and reported "Found 0 incoming transfers", so no
    // asset invoice could reach Processing or Settled, on every wallet, silently.
    //
    // So this asserts the query against a database rgb-lib itself creates. Offline: RestoreKeys and the
    // wallet constructor write the schema with no network and no funds.
    [Fact]
    public async Task TheBoundedTransferQueryPreparesAgainstTheSchemaRgbLibItselfWrites()
    {
        var restoredJson = RgbLibWallet.RestoreKeys("Regtest", TestMnemonic);
        using var restored = JsonDocument.Parse(restoredJson);
        var root = restored.RootElement;
        var fingerprint = root.GetProperty("master_fingerprint").GetString()!;
        var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-incoming-query-schema-{Guid.NewGuid():N}");
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

            // The production method, not a copy of its SQL: a copy is one more artifact that can drift
            // from the one that runs. An empty wallet returns no rows, so what is under test is that
            // every column and table this query names exists — the failure mode is SqliteException on
            // prepare, which is exactly what production hit.
            var matches = await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
                dbPath,
                new[] { "any-recipient-id" },
                assetId: null,
                CancellationToken.None);

            Assert.Empty(matches);
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        }
    }
}
