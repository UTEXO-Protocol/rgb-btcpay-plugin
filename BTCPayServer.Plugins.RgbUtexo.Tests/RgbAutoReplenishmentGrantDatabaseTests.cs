using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class RgbAutoReplenishmentGrantDatabaseTests
{
    const string StoreId = "store-under-test";
    const string OperatorId = "operator-under-test";

    [IntegrationFact]
    public async Task PluginMigrationsRunTwiceOverAnActiveWallet_NeverGrantUnattendedSigning()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var walletId = await SeedActiveWalletAsync(harness);

        await using (var seeded = harness.Factory.CreateContext())
        {
            var wallet = await seeded.RGBWallets.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == walletId);
            Assert.True(wallet != null,
                $"the seed inserted no RGB wallet row for '{walletId}', so every clause below would only "
                + "be asserting that an absent wallet is unauthorized — which holds of any empty database");
            Assert.True(wallet!.StoreId == StoreId && wallet.IsActive,
                $"the seeded wallet must be the ACTIVE wallet of '{StoreId}' for the grant lookup below to "
                + $"mean anything; got StoreId '{wallet.StoreId}', IsActive {wallet.IsActive}");
        }

        var store = new RgbAutoReplenishmentAuthorizationStore(harness.Factory);
        Assert.False(await store.IsGrantedForWalletAsync(StoreId, walletId),
            "a store whose RGB wallet exists and is active must still be unauthorized: there is no "
            + "behaviour-preserving upgrade, so the grant can only come from a deliberate operator action");
        Assert.Equal(0, await CountGrantRowsAsync(harness));

        await harness.RunPluginMigrationsAsync();

        Assert.False(await store.IsGrantedForWalletAsync(StoreId, walletId),
            "a second migration pass models a restart, and a restart must not manufacture a grant either");
        Assert.Equal(0, await CountGrantRowsAsync(harness));
    }

    [IntegrationFact]
    public async Task PluginMigrationsRunTwiceOverARevokedRow_LeaveItRevoked()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var walletId = await SeedActiveWalletAsync(harness);

        var store = new RgbAutoReplenishmentAuthorizationStore(harness.Factory);
        await store.RecordDecisionAsync(
            StoreId, walletId, RgbAutoReplenishmentDecision.Revoked, OperatorId);

        await harness.RunPluginMigrationsAsync();
        await harness.RunPluginMigrationsAsync();

        var row = await store.FindAsync(StoreId);
        Assert.NotNull(row);
        Assert.False(await store.IsGrantedForWalletAsync(StoreId, walletId),
            "an operator who revoked the grant must not have it restored by any number of migration passes");
        Assert.Equal(RgbAutoReplenishmentDecision.Revoked, row!.Decision);
        Assert.True(row.DecidedForWalletId == null,
            "revocation must also clear the wallet binding, or a later re-grant for a different wallet "
            + $"would find a stale binding already in place; got '{row.DecidedForWalletId}'");
    }

    [IntegrationFact]
    public async Task GrantSurvivesWalletDeleteAndRecreate_ButIsIneffectiveUntilReGranted()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var originalWalletId = await SeedActiveWalletAsync(harness);

        var store = new RgbAutoReplenishmentAuthorizationStore(harness.Factory);
        await store.RecordDecisionAsync(
            StoreId, originalWalletId, RgbAutoReplenishmentDecision.Granted, OperatorId);
        Assert.True(await store.IsGrantedForWalletAsync(StoreId, originalWalletId));

        await DeleteWalletAsync(harness, originalWalletId);
        var replacementWalletId = await SeedActiveWalletAsync(harness);

        var survived = await store.FindAsync(StoreId);
        Assert.NotNull(survived);
        Assert.Equal(RgbAutoReplenishmentDecision.Granted, survived!.Decision);
        Assert.Equal(originalWalletId, survived.DecidedForWalletId);
        Assert.Equal(1, await CountGrantRowsAsync(harness));

        Assert.False(await store.IsGrantedForWalletAsync(StoreId, replacementWalletId),
            "the row surviving is only half the design: the decision must become ineffective for a "
            + "wallet the operator never authorized, or delete-and-recreate silently inherits standing "
            + "unattended-signing authority over new keys");

        await store.RecordDecisionAsync(
            StoreId, replacementWalletId, RgbAutoReplenishmentDecision.Granted, OperatorId);
        Assert.True(await store.IsGrantedForWalletAsync(StoreId, replacementWalletId),
            "and a permanent refusal would be fund loss, so a deliberate re-grant must restore it");
    }

    [IntegrationFact]
    public async Task GrantIsUntouchedByEveryOtherPluginTableWrite()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var walletId = await SeedActiveWalletAsync(harness);

        var store = new RgbAutoReplenishmentAuthorizationStore(harness.Factory);
        await store.RecordDecisionAsync(
            StoreId, walletId, RgbAutoReplenishmentDecision.Granted, OperatorId);
        var before = await store.FindAsync(StoreId);

        await using (var ctx = harness.Factory.CreateContext())
        {
            var wallet = await ctx.RGBWallets.FirstAsync(w => w.Id == walletId);
            wallet.Name = "renamed by the operator";
            wallet.MaxAllocationsPerUtxo = 7;
            wallet.LastSyncAt = DateTimeOffset.UtcNow;
            ctx.RGBAssets.Add(new RGBAsset
            {
                WalletId = walletId,
                AssetId = "rgb:unit-test-asset",
                Ticker = "UT",
                Name = "unit test asset",
                AcceptForPayment = true
            });
            await ctx.SaveChangesAsync();
        }

        var after = await store.FindAsync(StoreId);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.Decision, after!.Decision);
        Assert.Equal(before.DecidedForWalletId, after.DecidedForWalletId);
        Assert.Equal(before.DecidedAt, after.DecidedAt);
        Assert.Equal(before.DecidedBy, after.DecidedBy);
        Assert.True(await store.IsGrantedForWalletAsync(StoreId, walletId),
            "the grant lives in a plugin-owned table of its own precisely so that no other write path "
            + "can reach it; a config-resident or wallet-resident grant looks correct until something else is saved");
    }

    [IntegrationFact]
    public async Task EachNoticeCauseKeepsItsOwnPersistedMarker_AndTheMarkerSurvivesARestart()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();

        DateTimeOffset stampedAt;
        await using (var ctx = harness.Factory.CreateContext())
        {
            var row = new RGBStoreNoticeState { StoreId = StoreId };
            stampedAt = DateTimeOffset.UtcNow;
            RgbReplenishmentNoticeService.StampMarker(
                row, RgbReplenishmentNoticeCause.CapDisabledDeploymentWide, stampedAt);
            ctx.RGBStoreNoticeStates.Add(row);
            await ctx.SaveChangesAsync();
        }

        await using (var reopened = harness.Factory.CreateContext())
        {
            var row = await reopened.RGBStoreNoticeStates.AsNoTracking()
                .FirstAsync(r => r.StoreId == StoreId);

            Assert.NotNull(RgbReplenishmentNoticeService.MarkerOf(
                row, RgbReplenishmentNoticeCause.CapDisabledDeploymentWide));
            Assert.Null(RgbReplenishmentNoticeService.MarkerOf(
                row, RgbReplenishmentNoticeCause.NotAuthorized));
            Assert.Null(RgbReplenishmentNoticeService.MarkerOf(
                row, RgbReplenishmentNoticeCause.ConfigOutOfBounds));
        }

        await using (var ctx = harness.Factory.CreateContext())
        {
            var row = await ctx.RGBStoreNoticeStates.FirstAsync(r => r.StoreId == StoreId);
            RgbReplenishmentNoticeService.StampMarker(
                row, RgbReplenishmentNoticeCause.NotAuthorized, DateTimeOffset.UtcNow);
            await ctx.SaveChangesAsync();
        }

        await using (var reopened = harness.Factory.CreateContext())
        {
            var row = await reopened.RGBStoreNoticeStates.AsNoTracking()
                .FirstAsync(r => r.StoreId == StoreId);
            Assert.Equal(stampedAt, RgbReplenishmentNoticeService.MarkerOf(
                row, RgbReplenishmentNoticeCause.CapDisabledDeploymentWide));
            Assert.NotNull(RgbReplenishmentNoticeService.MarkerOf(
                row, RgbReplenishmentNoticeCause.NotAuthorized));
        }
    }

    [IntegrationFact]
    public async Task OneActiveWalletPerStoreIsEnforcedByTheDatabase()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        await SeedActiveWalletAsync(harness);

        var fault = await Assert.ThrowsAnyAsync<Exception>(() => SeedActiveWalletAsync(harness));

        Assert.Contains("IX_RGB_Wallets_StoreId", Flatten(fault));
    }

    static string Flatten(Exception fault)
    {
        var text = fault.Message;
        for (var inner = fault.InnerException; inner != null; inner = inner.InnerException)
            text += " | " + inner.Message;
        return text;
    }

    static async Task<string> SeedActiveWalletAsync(RgbPluginDatabaseHarness harness)
    {
        var walletId = Guid.NewGuid().ToString();
        await using var ctx = harness.Factory.CreateContext();
        ctx.RGBWallets.Add(new RGBWallet
        {
            Id = walletId,
            StoreId = StoreId,
            Network = "regtest",
            XpubVanilla = "xpub-vanilla-" + walletId,
            XpubColored = "xpub-colored-" + walletId,
            MasterFingerprint = walletId[..8],
            IsActive = true
        });
        await ctx.SaveChangesAsync();
        return walletId;
    }

    static async Task DeleteWalletAsync(RgbPluginDatabaseHarness harness, string walletId)
    {
        await using var ctx = harness.Factory.CreateContext();
        var wallet = await ctx.RGBWallets.FirstAsync(w => w.Id == walletId);
        ctx.RGBWallets.Remove(wallet);
        await ctx.SaveChangesAsync();
    }

    static async Task<int> CountGrantRowsAsync(RgbPluginDatabaseHarness harness)
    {
        await using var ctx = harness.Factory.CreateContext();
        return await ctx.RGBStoreAutoReplenishments.CountAsync();
    }
}
