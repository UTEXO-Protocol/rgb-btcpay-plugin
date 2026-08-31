using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class RgbStoreWalletUniquenessDatabaseTests
{
    const string ContestedStore = "store-with-two-active-wallets";
    const string FirstWallet = "11111111-1111-1111-1111-111111111111";
    const string SecondWallet = "22222222-2222-2222-2222-222222222222";

    const string LegacyNonUniqueIndex = """
        DROP INDEX IF EXISTS "IX_RGB_Wallets_StoreId";
        CREATE INDEX "IX_RGB_Wallets_StoreId" ON "RGB_Wallets" ("StoreId");
        """;

    const string IsIndexUnique = """
        SELECT i.indisunique AS "Value" FROM pg_index i
        JOIN pg_class c ON c.oid = i.indexrelid
        WHERE c.relname = 'IX_RGB_Wallets_StoreId'
        """;

    static RGBWallet Wallet(string walletId) => new()
    {
        Id = walletId,
        StoreId = ContestedStore,
        Network = "regtest",
        XpubVanilla = "xpub-vanilla-" + walletId,
        XpubColored = "xpub-colored-" + walletId,
        MasterFingerprint = walletId[..8],
        IsActive = true
    };

    [IntegrationFact]
    public async Task ADatabaseThatAlreadyHoldsDuplicateActiveWallets_StillFinishesTheStartupTask()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        await using (var seed = harness.Factory.CreateContext())
        {
            await seed.Database.ExecuteSqlRawAsync(LegacyNonUniqueIndex);
            seed.RGBWallets.AddRange(Wallet(FirstWallet), Wallet(SecondWallet));
            await seed.SaveChangesAsync();
        }

        await harness.RunPluginMigrationsAsync();
        await harness.RunPluginMigrationsAsync();

        await using var verify = harness.Factory.CreateContext();

        var survivors = await verify.RGBWallets
            .Where(w => w.StoreId == ContestedStore)
            .OrderBy(w => w.Id)
            .Select(w => new { w.Id, w.IsActive })
            .ToListAsync();
        Assert.Equal([FirstWallet, SecondWallet], survivors.Select(w => w.Id));
        Assert.All(survivors, wallet => Assert.True(wallet.IsActive,
            "either row may hold the only copy of a funded wallet's recovery phrase, so the startup task "
            + "must never resolve the duplicate by deactivating or deleting one"));

        var uniqueness = await verify.Database.SqlQueryRaw<bool>(IsIndexUnique).ToListAsync();
        Assert.Equal([false], uniqueness);
    }

    [IntegrationFact]
    public async Task ACleanDatabase_StillEndsUpWithTheUniqueStoreWalletIndex()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        await using (var seed = harness.Factory.CreateContext())
        {
            seed.RGBWallets.Add(Wallet(FirstWallet));
            await seed.SaveChangesAsync();
        }

        await harness.RunPluginMigrationsAsync();

        await using var verify = harness.Factory.CreateContext();

        var uniqueness = await verify.Database.SqlQueryRaw<bool>(IsIndexUnique).ToListAsync();
        Assert.Equal([true], uniqueness);

        verify.RGBWallets.Add(Wallet(SecondWallet));
        var rejection = await Assert.ThrowsAsync<DbUpdateException>(() => verify.SaveChangesAsync());
        Assert.Contains("IX_RGB_Wallets_StoreId", rejection.InnerException?.Message ?? string.Empty);
    }
}
