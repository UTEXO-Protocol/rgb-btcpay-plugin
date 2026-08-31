using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCompletedIssuanceIsNotReportedAsAFailureTests
{
    const string WalletId = "wallet-under-test";
    const string Network = "signet";
    const string Fingerprint = "00000000";
    const string AssetId = "rgb:AAAA-BBBB-CCCC";
    const string Ticker = "TCK";

    sealed class AssetRowInsertRejectingContext(
        DbContextOptions<RGBPluginDbContext> options, Func<bool> rejectAssetRowInserts)
        : RGBPluginDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
            => rejectAssetRowInserts()
               && ChangeTracker.Entries<RGBAsset>().Any(e => e.State == EntityState.Added)
                ? Task.FromException<int>(new DbUpdateException(
                    "An error occurred while saving the entity changes.",
                    new InvalidOperationException(
                        "23505: duplicate key value violates unique constraint \"PK_RGB_Assets\"")))
                : base.SaveChangesAsync(ct);
    }

    sealed class InMemoryBackedFactory : RGBPluginDbContextFactory
    {
        readonly string _storeName = Guid.NewGuid().ToString();

        public bool RejectAssetRowInserts;

        public InMemoryBackedFactory()
            : base(Options.Create(new DatabaseOptions { ConnectionString = "Host=unused" }))
        {
        }

        public override RGBPluginDbContext CreateContext(
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
            => new AssetRowInsertRejectingContext(
                new DbContextOptionsBuilder<RGBPluginDbContext>()
                    .UseInMemoryDatabase(_storeName).Options,
                () => RejectAssetRowInserts);
    }

    sealed class WarningCapturingLogger : ILogger<RGBWalletService>
    {
        internal List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    sealed record Harness(
        RGBWalletService Service,
        InMemoryBackedFactory Db,
        WarningCapturingLogger Log,
        RGBConfiguration Cfg,
        FakeRgbLib RgbLib);

    static async Task<Harness> BuildHarnessAsync()
    {
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-issuance-outcome-{Guid.NewGuid():N}"));
        var stockDir = Path.Combine(cfg.GetWalletDataDir(WalletId, Network), Fingerprint, "rgb");
        Directory.CreateDirectory(stockDir);
        foreach (var name in new[] { "index.dat", "stash.dat", "state.dat" })
            await File.WriteAllBytesAsync(Path.Combine(stockDir, name), []);

        var db = new InMemoryBackedFactory();
        await using (var ctx = db.CreateContext())
        {
            ctx.RGBWallets.Add(new RGBWallet
            {
                Id = WalletId,
                StoreId = "store-1",
                Name = "Wallet",
                Network = Network,
                MasterFingerprint = Fingerprint,
                XpubVanilla = "v",
                XpubColored = "c"
            });
            await ctx.SaveChangesAsync();
        }

        var log = new WarningCapturingLogger();
        var rgbLib = new FakeRgbLib(cfg, Fingerprint);
        var service = new RGBWalletService(rgbLib, db, cfg, null!, null!,
            new CurrencyNameTable([], NullLogger<CurrencyNameTable>.Instance), null!, log, null!, null!);
        return new Harness(service, db, log, cfg, rgbLib);
    }

    static void Cleanup(RGBConfiguration cfg)
    {
        try { if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, recursive: true); }
        catch (IOException) { }
    }

    static RgbAsset Issued() => new()
    {
        AssetId = AssetId,
        Ticker = Ticker,
        Name = "Token",
        Precision = 0,
        IssuedSupply = 100
    };

    [Fact]
    public async Task AssetRowInsertLosingTheRaceWithTheListener_StillReportsTheIssuanceThatAlreadyHappened()
    {
        var h = await BuildHarnessAsync();
        try
        {
            h.RgbLib.IrreversibleNiaIssuance = (_, _, _, _, _) =>
            {
                h.Db.RejectAssetRowInserts = true;
                return Issued();
            };

            var asset = await h.Service.IssueAssetAsync(WalletId, Ticker, "Token", 100);

            Assert.Equal(AssetId, asset.AssetId);
            Assert.Contains(h.Log.Entries,
                e => e.Level == LogLevel.Warning && e.Exception is DbUpdateException);
        }
        finally { Cleanup(h.Cfg); }
    }

    [Fact]
    public async Task AssetRowInsertFailure_IsNeverRaisedToTheCaller_BecauseTheStockMutationCannotBeUndone()
    {
        var h = await BuildHarnessAsync();
        try
        {
            h.RgbLib.IrreversibleNiaIssuance = (_, _, _, _, _) =>
            {
                h.Db.RejectAssetRowInserts = true;
                return Issued();
            };

            var thrown = await Record.ExceptionAsync(
                () => h.Service.IssueAssetAsync(WalletId, Ticker, "Token", 100));

            Assert.True(thrown == null,
                "The NIA contract is already created and committed in the rgb-lib Stock and there is no "
                + "un-issue API. Letting the RGBAssets bookkeeping write escape makes the controller show "
                + "'Failed to issue asset', and the operator then issues a second contract with the same "
                + "ticker and burns another colorable-UTXO allocation. Observed instead: "
                + thrown?.GetType().Name);
        }
        finally { Cleanup(h.Cfg); }
    }

    [Fact]
    public async Task UneventfulIssuance_StillRecordsTheAssetRowAndLogsNoWarning()
    {
        var h = await BuildHarnessAsync();
        try
        {
            h.RgbLib.IrreversibleNiaIssuance = (_, _, _, _, _) => Issued();

            await h.Service.IssueAssetAsync(WalletId, Ticker, "Token", 100);

            await using var ctx = h.Db.CreateContext();
            var row = await ctx.RGBAssets.FindAsync([WalletId, AssetId]);
            Assert.NotNull(row);
            Assert.Equal(Ticker, row!.Ticker);
            Assert.DoesNotContain(h.Log.Entries, e => e.Level == LogLevel.Warning);
        }
        finally { Cleanup(h.Cfg); }
    }
}
