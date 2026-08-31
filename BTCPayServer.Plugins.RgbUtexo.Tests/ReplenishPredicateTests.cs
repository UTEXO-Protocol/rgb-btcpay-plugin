using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReplenishPredicateTests
{
    const long Now = 1_000_000L;

    static RGBInvoice Row(RGBInvoiceStatus status = RGBInvoiceStatus.Pending, long? expiry = Now + 60,
        string walletId = "w1") =>
        new() { Id = "inv-1", WalletId = walletId, RecipientId = "utxob:abc", Status = status, ExpirationTimestamp = expiry };

    static bool Match(RGBInvoice row, string walletId = "w1") =>
        RGBInvoiceListener.ActivePendingInvoicePredicate(walletId, Now).Compile()(row);

    [Fact]
    public void UnexpiredPending_IsActive() => Assert.True(Match(Row()));

    [Fact]
    public void ExpiredPending_IsNotActive() => Assert.False(Match(Row(expiry: Now - 1)));

    // WHY null is inactive: the stored value is rgb-lib's echoed expiry (long?), so a null is a checkout-path
    // row whose expiry rgb-lib omitted — exactly the attacker-mintable kind.
    [Fact]
    public void NullExpiryPending_IsNotActive() => Assert.False(Match(Row(expiry: null)));

    [Fact]
    public void ExpiryExactlyNow_IsNotActive() => Assert.False(Match(Row(expiry: Now)));

    [Theory]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations)]
    [InlineData(RGBInvoiceStatus.Settled)]
    [InlineData(RGBInvoiceStatus.Failed)]
    [InlineData(RGBInvoiceStatus.Expired)]
    [InlineData(RGBInvoiceStatus.Underpaid)]
    public void NonPendingStatuses_AreNotActive(RGBInvoiceStatus status) => Assert.False(Match(Row(status)));

    [Fact]
    public void OtherWalletsRow_IsNotActive() => Assert.False(Match(Row(walletId: "w2")));

    // WHY this test exists at all: every test above compiles the expression to a delegate and runs it in
    // memory, which proves the LOGIC but says nothing about the only way the listener actually uses it —
    // as an EF Core predicate. If any clause failed to translate, EF would either throw or (worse, in the
    // false-ACCEPT direction) the count would be computed over a different row set than the one asserted
    // here. Asserting the whole WHERE clause rather than substrings also pins that all four conditions
    // survive, that the two variables are parameters rather than inlined literals, and that Pending maps to
    // 0. Npgsql generates SQL without opening a connection, so this needs no database.
    [Fact]
    public void Predicate_TranslatesEntirelyToServerSideSql()
    {
        // SetPostgresVersion mirrors BaseDbContextFactory.ConfigureBuilder, which the plugin's context always
        // goes through: the generated SQL is version-dependent in general, so a pin that configured the
        // provider differently would not be pinning what production runs.
        var options = new DbContextOptionsBuilder<RGBPluginDbContext>()
            .UseNpgsql("Host=localhost;Database=none", o => o.SetPostgresVersion(12, 0))
            .Options;
        using var db = new RGBPluginDbContext(options);

        var sql = db.RGBInvoices
            .Where(RGBInvoiceListener.ActivePendingInvoicePredicate("w1", Now))
            .ToQueryString();

        var where = sql[sql.IndexOf("WHERE ", StringComparison.Ordinal)..].Trim();

        Assert.Equal(
            "WHERE r.\"WalletId\" = @walletId AND r.\"Status\" = 0 "
            + "AND r.\"ExpirationTimestamp\" IS NOT NULL AND r.\"ExpirationTimestamp\" > @nowUnix",
            where);
    }
}
