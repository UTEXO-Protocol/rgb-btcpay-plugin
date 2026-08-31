using BTCPayServer.Plugins.RgbUtexo.Data;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbStoreWalletUniquenessHardeningTests
{
    const string StoreA = "store-aaaaaaaaaaaaaaaaaaaaaaaa";
    const string StoreB = "store-bbbbbbbbbbbbbbbbbbbbbbbb";
    const string WalletA1 = "11111111-1111-1111-1111-111111111111";
    const string WalletA2 = "22222222-2222-2222-2222-222222222222";
    const string WalletB1 = "33333333-3333-3333-3333-333333333333";
    const string WalletB2 = "44444444-4444-4444-4444-444444444444";

    static RGBPluginMigrationRunner.DuplicateActiveStoreWallet Duplicate(string storeId, string walletId) =>
        new(storeId, walletId);

    static Func<CancellationToken, Task<IReadOnlyList<RGBPluginMigrationRunner.DuplicateActiveStoreWallet>>>
        Probe(params RGBPluginMigrationRunner.DuplicateActiveStoreWallet[] duplicates) =>
        _ => Task.FromResult<IReadOnlyList<RGBPluginMigrationRunner.DuplicateActiveStoreWallet>>(duplicates);

    [Fact]
    public async Task DuplicateActiveWallets_LetTheHostFinishStartingInsteadOfAbortingIt()
    {
        var log = new CapturingLogger();
        var indexAttempts = 0;

        var hardened = await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
            Probe(Duplicate(StoreA, WalletA1), Duplicate(StoreA, WalletA2)),
            _ =>
            {
                indexAttempts++;
                return Task.CompletedTask;
            },
            log,
            CancellationToken.None);

        Assert.False(hardened,
            "a database that already holds two active wallets for one store cannot take the unique index, "
            + "so the hardening must report that it did not apply");
        Assert.True(indexAttempts == 0,
            "the DDL drops IX_RGB_Wallets_StoreId before recreating it, so attempting a creation that is "
            + "known to fail risks leaving the table with no index on StoreId at all; the probe must gate it");
        Assert.Contains(LogLevel.Critical, log.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public async Task DuplicateActiveWallets_AreNamedForTheOperatorByStoreAndWalletId()
    {
        var log = new CapturingLogger();

        await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
            Probe(Duplicate(StoreA, WalletA1), Duplicate(StoreA, WalletA2),
                Duplicate(StoreB, WalletB1), Duplicate(StoreB, WalletB2)),
            _ => Task.CompletedTask,
            log,
            CancellationToken.None);

        var critical = Assert.Single(log.Entries.Where(entry => entry.Level == LogLevel.Critical)).Message;
        foreach (var identifier in new[] { StoreA, StoreB, WalletA1, WalletA2, WalletB1, WalletB2 })
            Assert.Contains(identifier, critical);
        Assert.Contains("2 store(s)", critical);
        Assert.Contains("RGB Settings", critical);
    }

    [Fact]
    public async Task ARejectedIndexCreation_LetTheHostFinishStartingAndIsLoggedCritical()
    {
        var log = new CapturingLogger();
        var rejection = new InvalidOperationException("could not create unique index");

        var hardened = await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
            Probe(),
            _ => Task.FromException(rejection),
            log,
            CancellationToken.None);

        Assert.False(hardened);
        var critical = Assert.Single(log.Entries.Where(entry => entry.Level == LogLevel.Critical));
        Assert.Same(rejection, critical.Exception);
        Assert.Contains("WITHOUT that guard", critical.Message);
    }

    [Fact]
    public async Task AFailedDuplicateProbe_LetTheHostFinishStartingAndSkipsTheDdl()
    {
        var log = new CapturingLogger();
        var probeFault = new InvalidOperationException("probe failed");
        var indexAttempts = 0;

        var hardened = await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
            _ => Task.FromException<IReadOnlyList<RGBPluginMigrationRunner.DuplicateActiveStoreWallet>>(probeFault),
            _ =>
            {
                indexAttempts++;
                return Task.CompletedTask;
            },
            log,
            CancellationToken.None);

        Assert.False(hardened);
        Assert.True(indexAttempts == 0,
            "with the duplicate state unknown the DDL must not drop the existing index on a guess");
        Assert.Same(probeFault,
            Assert.Single(log.Entries.Where(entry => entry.Level == LogLevel.Critical)).Exception);
    }

    [Fact]
    public async Task ACleanDatabase_StillGetsTheUniqueIndexAndNothingCriticalIsLogged()
    {
        var log = new CapturingLogger();
        var indexAttempts = 0;

        var hardened = await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
            Probe(Duplicate(StoreA, WalletA1)),
            _ =>
            {
                indexAttempts++;
                return Task.CompletedTask;
            },
            log,
            CancellationToken.None);

        Assert.True(hardened,
            "one active wallet on a store is not a duplicate; the guard against the wallet-create race must "
            + "still be installed on every database that can take it");
        Assert.True(indexAttempts == 1);
        Assert.DoesNotContain(LogLevel.Critical, log.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public async Task RepeatedStartups_ReapplyTheHardeningWithoutFailingTheSecondRun()
    {
        var log = new CapturingLogger();
        var indexAttempts = 0;

        Func<CancellationToken, Task> createIndex = _ =>
        {
            indexAttempts++;
            return Task.CompletedTask;
        };

        for (var startup = 0; startup < 3; startup++)
        {
            Assert.True(await RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
                Probe(), createIndex, log, CancellationToken.None),
                $"startup {startup + 1} must harden exactly like the first: ExecuteAsync runs on every boot");
        }

        Assert.True(indexAttempts == 3);
        Assert.DoesNotContain(LogLevel.Critical, log.Entries.Select(entry => entry.Level));
        Assert.Contains("DROP INDEX IF EXISTS", RGBPluginMigrationRunner.StoreWalletUniqueIndexSql);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS", RGBPluginMigrationRunner.StoreWalletUniqueIndexSql);
    }

    [Fact]
    public async Task ShutdownCancellation_IsNotSwallowedByTheStartupGuard()
    {
        var log = new CapturingLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RGBPluginMigrationRunner.HardenStoreWalletUniquenessAsync(
                _ => Task.FromException<IReadOnlyList<RGBPluginMigrationRunner.DuplicateActiveStoreWallet>>(
                    new OperationCanceledException(cts.Token)),
                _ => Task.CompletedTask,
                log,
                cts.Token));

        Assert.DoesNotContain(LogLevel.Critical, log.Entries.Select(entry => entry.Level));
    }

    [Fact]
    public void OnlyStoresHoldingMoreThanOneActiveWallet_CountAsContested()
    {
        var contested = RGBPluginMigrationRunner.ContestedActiveStoreWallets(
        [
            Duplicate(StoreA, WalletA1),
            Duplicate(StoreB, WalletB1),
            Duplicate(StoreB, WalletB2)
        ]);

        Assert.Equal([Duplicate(StoreB, WalletB1), Duplicate(StoreB, WalletB2)], contested);
    }

    [Fact]
    public void TheDuplicateRecord_CarriesOnlyStoreAndWalletIdentifiers()
    {
        var properties = typeof(RGBPluginMigrationRunner.DuplicateActiveStoreWallet)
            .GetProperties()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["StoreId", "WalletId"], properties);
    }

    [Fact]
    public void TheOperatorDirective_GroupsEveryWalletUnderItsStore()
    {
        var described = RGBPluginMigrationRunner.DescribeDuplicateActiveStoreWallets(
        [
            Duplicate(StoreA, WalletA1),
            Duplicate(StoreA, WalletA2),
            Duplicate(StoreB, WalletB1)
        ]);

        Assert.Equal(
            $"store {StoreA} -> wallets {WalletA1}, {WalletA2}; store {StoreB} -> wallets {WalletB1}",
            described);
    }

    sealed class CapturingLogger : ILogger
    {
        internal List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
