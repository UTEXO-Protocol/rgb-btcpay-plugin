using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbWalletServiceSweepLockTests
{
    const string WalletId = "w1";
    const string Network = "signet";
    const string Fingerprint = "00000000";

    // BaseDbContextFactory.ConfigureBuilder applies EnableRetryOnFailure(10) and then invokes the
    // caller's options action, so this override replaces the retrying strategy. Without it the free-path
    // test spends ~3 minutes in EF's exponential backoff retrying a connection that is refused
    // instantly, which would nearly double the suite's wall time and make the ablation campaign
    // hour-long. It changes only HOW FAST _mark fails, not that it fails or with which identity.
    sealed class FastFailDbContextFactory : RGBPluginDbContextFactory
    {
        public FastFailDbContextFactory(IOptions<DatabaseOptions> options) : base(options) { }

        public override RGBPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
            => base.CreateContext(o =>
            {
                o.ExecutionStrategy(d => new NonRetryingExecutionStrategy(d));
                npgsqlOptionsAction?.Invoke(o);
            });
    }

    static (RGBWalletService Svc, RGBConfiguration Cfg) BuildService()
    {
        var cfg = new RGBConfiguration(Path.Combine(Path.GetTempPath(), $"rgb-sweeplock-{Guid.NewGuid():N}"));
        var db = new FastFailDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var svc = new RGBWalletService(new FakeRgbLib(cfg), db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, null!, null!);
        return (svc, cfg);
    }

    // Load-bearing: CleanupExpiredTransfersInternalAsync returns at its File.Exists guard when no
    // rgb_lib_db is present, so without this file a mutated body that runs the cleanup OUTSIDE the lock
    // returns harmlessly and the skip test passes anyway. A zero-byte file is a valid empty SQLite
    // database, so the connection opens and the UPDATE then fails on the missing batch_transfer table —
    // that throw is what discriminates "the op ran" from "the op was skipped".
    static void CreateDbFixture(RGBConfiguration cfg, string walletId, string network, string fingerprint)
    {
        var dir = Path.Combine(cfg.GetWalletDataDir(walletId, network), fingerprint);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "rgb_lib_db"), []);
    }

    // Guarded: unguarded, a failure before CreateDbFixture makes this throw DirectoryNotFoundException
    // out of the finally and REPLACE the real exception.
    static void DeleteFixture(RGBConfiguration cfg)
    {
        try
        {
            if (Directory.Exists(cfg.RgbBaseDir)) Directory.Delete(cfg.RgbBaseDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task Cleanup_WhenSendHoldsLock_SkipsWithoutBlocking()
    {
        var (svc, cfg) = BuildService();
        try
        {
            CreateDbFixture(cfg, WalletId, Network, Fingerprint);

            // The live per-wallet semaphore the coordinator consults, standing in for an in-flight send.
            var sendLock = svc.SendLockFor(WalletId);
            await sendLock.WaitAsync();
            try
            {
                var call = svc.CleanupExpiredTransfersAsync(WalletId, Network, Fingerprint);
                using var timeout = new CancellationTokenSource();
                var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5), timeout.Token));
                timeout.Cancel();

                Assert.True(finished == call,
                    "the sweep blocked on the held send lock instead of skipping the wallet (audit H2c-lite)");

                // Separate from the race: proves the cleanup was SKIPPED rather than run anyway. Running it
                // would open the fixture database and throw on the missing batch_transfer table.
                Assert.False(await call);
            }
            finally { sendLock.Release(); }
        }
        finally { DeleteFixture(cfg); }
    }

    [Fact]
    public async Task Cleanup_WhenLockFree_RunsUnderWriteAhead()
    {
        var (svc, cfg) = BuildService();
        try
        {
            CreateDbFixture(cfg, WalletId, Network, Fingerprint);

            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => svc.CleanupExpiredTransfersAsync(WalletId, Network, Fingerprint));
            AssertIsMarkFailure(ex);
        }
        finally { DeleteFixture(cfg); }
    }

    // _mark is SetNeedsRecoveryAsync, which opens a context and does FindAsync ?? throw
    // KeyNotFoundException. Which identity surfaces depends on the environment — unreachable host,
    // reachable server without this database (PostgresException 3D000, an NpgsqlException subclass), or
    // reachable database with no wallet row. All three prove the same thing: control entered
    // WriteAheadAsync and ran _mark. The two rejected identities are the incidental failures a fake or a
    // disposed handle raises, which would prove nothing about the write-ahead.
    static void AssertIsMarkFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            Assert.False(e is NotImplementedException,
                "the free path failed inside a fake, not inside the write-ahead: " + ex);
            Assert.False(e is ObjectDisposedException,
                "the free path failed on a disposed handle, not inside the write-ahead: " + ex);
        }
        var identified = false;
        for (var e = ex; e is not null; e = e.InnerException)
            identified |= e is Npgsql.NpgsqlException or System.Net.Sockets.SocketException
                          or KeyNotFoundException;
        Assert.True(identified,
            "expected the failure to identify _mark (SetNeedsRecoveryAsync): an NpgsqlException/"
            + "PostgresException from an unreachable or wrong database, a SocketException, or a "
            + "KeyNotFoundException from a reachable database with no wallet row. Got: " + ex);
    }
}
