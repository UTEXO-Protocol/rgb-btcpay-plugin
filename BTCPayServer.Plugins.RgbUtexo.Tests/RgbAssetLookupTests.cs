using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbAssetLookupTests
{
    // Both tests apply the PRODUCTION expression, not a re-authored copy of it. A copy would be
    // tautological: it would stay green if GetAssetAsync later dropped the WalletId filter, which is
    // exactly the false-ACCEPT this file exists to catch.

    // 37b — dropping the wallet filter would return another wallet's row for the same asset id and
    // silently mis-scale the unit count via that row's Precision.
    [Fact]
    public void AssetPredicate_FiltersOnBothWalletIdAndAssetId()
    {
        // SetPostgresVersion mirrors BaseDbContextFactory.ConfigureBuilder, which the plugin's context
        // always goes through. Npgsql renders SQL without opening a connection, so this needs no database.
        var options = new DbContextOptionsBuilder<RGBPluginDbContext>()
            .UseNpgsql("Host=localhost;Database=none", o => o.SetPostgresVersion(12, 0))
            .Options;
        using var db = new RGBPluginDbContext(options);

        var sql = db.RGBAssets
            .Where(RGBWalletService.AssetPredicate("w1", "rgb:abc"))
            .ToQueryString();

        var where = sql[sql.IndexOf("WHERE ", StringComparison.Ordinal)..].Trim();

        // Both column filters, both parameterised rather than inlined, in one clause and nothing else.
        // The EF parameter NAMES are deliberately not pinned: EF derives them from AssetPredicate's
        // parameter identifiers, so renaming those is property-preserving and must stay green.
        Assert.Matches(@"^WHERE r\.""WalletId"" = @\w+ AND r\.""AssetId"" = @\w+$", where);
    }

    // 37c — the predicate must range over the RGB_Assets ENTITY, whose Precision drives the unit
    // count, not over rgb-lib's node-side RgbAsset. Confusing the two is a false-ACCEPT direction.
    [Fact]
    public void AssetPredicate_RangesOverThePersistedEntity_NotTheNodeSideModel()
    {
        var parameter = RGBWalletService.AssetPredicate("w1", "rgb:abc").Parameters[0];

        Assert.Equal(typeof(RGBAsset), parameter.Type);
        Assert.NotEqual(typeof(RgbAsset), parameter.Type);
    }
}
