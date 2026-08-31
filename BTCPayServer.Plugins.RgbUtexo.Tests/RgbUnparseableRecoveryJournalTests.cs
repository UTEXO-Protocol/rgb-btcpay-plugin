using System.Reflection;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbUnparseableRecoveryJournalTests : IDisposable
{
    readonly string _baseDir = Path.Combine(
        Path.GetTempPath(), $"rgb-unparseable-journal-{Guid.NewGuid():N}");

    const string Fingerprint = "aabbccdd";
    const string WalletNetwork = "regtest";
    const string CorruptJournalContents = "{\"Phase\":";

    [Fact]
    public void UnparseableProbe_AnswersForExactlyTheJournalsReadRefuses()
    {
        var path = Path.Combine(_baseDir, Fingerprint, RgbSendRecoveryJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.False(RgbSendRecoveryJournal.IsUnparseable(path));

        RgbSendRecoveryJournal.Write(path, RgbSendRecoveryPhase.Staged);
        Assert.False(RgbSendRecoveryJournal.IsUnparseable(path));

        File.WriteAllText(path, "send-end-indeterminate\n");
        Assert.False(RgbSendRecoveryJournal.IsUnparseable(path));

        foreach (var contents in new[]
                 {
                     CorruptJournalContents,
                     "unknown",
                     "{\"Phase\":7}",
                     "{\"Phase\":1,\"BatchTransferIdx\":0}"
                 })
        {
            File.WriteAllText(path, contents);
            Assert.True(RgbSendRecoveryJournal.IsUnparseable(path),
                $"Read refuses `{contents}`, so the deletion escape must classify it as unparseable — "
                + "otherwise the two disagree and a wallet stays undeletable for a journal that the "
                + "recovery path has already decided carries no replayable transfer.");
        }

        RgbSendRecoveryJournal.Delete(path);
        Assert.False(RgbSendRecoveryJournal.IsUnparseable(path));
    }

    [Fact]
    public async Task Reconcile_KeepsAnUnparseableJournalWhileAWaitingCounterpartyOutboundSendExists()
    {
        var (service, wallet, walletDir) = await BuildAsync();
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 4, status: 1, incoming: false);
        WriteCorruptJournal(walletDir);

        var refusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => ReconcileAsync(service, wallet));

        AssertQuarantined(refusal);
        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath(walletDir)));
        AssertJournalRetainedWithoutANewDurableBlocker(walletDir);
    }

    [Fact]
    public async Task Reconcile_IncomingWaitingCounterpartyRowDoesNotExtendTheHold()
    {
        var (service, wallet, walletDir) = await BuildAsync();
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 5, status: 1, incoming: true);
        WriteCorruptJournal(walletDir);

        var reachedTheQuarantineDischarge = await Assert.ThrowsAnyAsync<Exception>(
            () => ReconcileAsync(service, wallet));

        Assert.NotEqual("BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException",
            reachedTheQuarantineDischarge.GetType().FullName);
        Assert.False(File.Exists(JournalPath(walletDir)),
            "the sweep proved no OUTBOUND transfer is unresolved, so the journal is discardable; only "
            + "the unreachable wallet database of this fixture stops the discharge from committing.");
    }

    [Fact]
    public async Task Reconcile_RefusesWhenARefreshRecreatesTheDatabaseItWouldReadAsProof()
    {
        var (service, wallet, walletDir) = await BuildAsync(refreshRecreatesTheDatabase: true);
        WriteCorruptJournal(walletDir);
        Assert.False(File.Exists(DbPath(walletDir)));

        AssertQuarantined(await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => ReconcileAsync(service, wallet)));

        Assert.True(File.Exists(DbPath(walletDir)),
            "the fixture must actually reproduce the hazard: rgb-lib opens rgb_lib_db rwc and applies "
            + "migrations, so a refresh inside this reconciliation CREATES the file. If it does not, "
            + "this row proves nothing.");
        AssertJournalRetainedWithoutANewDurableBlocker(walletDir);
        Assert.True((await RGBWalletService.HasOutgoingBatchStatusAsync(DbPath(walletDir), 1)) == false,
            "INVARIANT — evidence must be older than the operation that could manufacture it: the "
            + "recreated database truthfully reports no status-1 row, which is why the discharge has "
            + "to read existence captured BEFORE any native work rather than this honest answer from "
            + "a replacement file. A send awaiting its ACK would otherwise be forgotten.");
    }

    [Fact]
    public async Task Reconcile_KeepsAnUnparseableJournalWhenTheProbeDatabaseIsMissing()
    {
        var (service, wallet, walletDir) = await BuildAsync();
        WriteCorruptJournal(walletDir);
        Assert.False(File.Exists(DbPath(walletDir)));

        AssertQuarantined(await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => ReconcileAsync(service, wallet)));

        AssertJournalRetainedWithoutANewDurableBlocker(walletDir);
    }

    [Fact]
    public async Task Reconcile_KeepsAnUnparseableJournalWhenTheOrphanSweepCannotAnswer()
    {
        var (service, wallet, walletDir) = await BuildAsync();
        Directory.CreateDirectory(walletDir);
        await File.WriteAllTextAsync(DbPath(walletDir), "this is not a sqlite database");
        WriteCorruptJournal(walletDir);

        await Assert.ThrowsAnyAsync<Exception>(() => ReconcileAsync(service, wallet));

        AssertJournalRetainedWithoutANewDurableBlocker(walletDir);
    }

    static void AssertJournalRetainedWithoutANewDurableBlocker(string walletDir)
    {
        Assert.True(File.Exists(JournalPath(walletDir)),
            "an unproven absence must keep the journal: it is the only durable record that something "
            + "was staged, and discarding it would forget a real in-flight send.");
        Assert.False(RgbNativeSendLease.Exists(walletDir),
            "the refusal must leave no worker marker behind. Base never published one on this path, "
            + "and a retained one blocks balances, asset lists and deletion with nothing able to "
            + "release it — a permanent false reject built on top of a recoverable one.");
    }

    [IntegrationFact]
    public async Task Reconcile_DiscardsAnUnparseableJournalOnlyOnAnEmptyOrphanSweep()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        CreateStockFiles(walletDir);
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 8, status: 3, incoming: false);
        await InsertBatchAsync(walletDir, batch: 9, status: 5, incoming: true);
        WriteCorruptJournal(walletDir);

        await ReconcileAsync(service, wallet);

        Assert.False(File.Exists(JournalPath(walletDir)),
            "with no orphaned outbound batch there is nothing left to replay, and an unparseable "
            + "journal carries no transfer data of its own, so it must be discarded rather than left "
            + "to refuse every future refresh, send and deletion.");
        Assert.False(RgbNativeSendLease.Exists(walletDir));
        await using var ctx = harness.Factory.CreateContext();
        var row = await ctx.RGBWallets.AsNoTracking().SingleAsync(w => w.Id == wallet.Id);
        Assert.False(row.NeedsRecovery,
            "the quarantine must be discharged too: a cleared journal with NeedsRecovery still set "
            + "leaves the wallet refusing sends with nothing left on disk to reconcile.");
    }

    [IntegrationFact]
    public async Task Reconcile_FailsAStagedOrphanUnderAnUnparseableJournalThenDischarges()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        CreateStockFiles(walletDir);
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 6, status: 5, incoming: false);
        WriteCorruptJournal(walletDir);

        await ReconcileAsync(service, wallet);

        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath(walletDir)));
        Assert.False(File.Exists(JournalPath(walletDir)));
        Assert.False(RgbNativeSendLease.Exists(walletDir));
    }

    [IntegrationFact]
    public async Task UnparseableJournalOverBothStatuses_DrainsTheOrphanAndStillPermitsDeletion()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        CreateStockFiles(walletDir);
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 11, status: 1, incoming: false);
        await InsertBatchAsync(walletDir, batch: 12, status: 5, incoming: false);
        WriteCorruptJournal(walletDir);

        AssertQuarantined(await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => ReconcileAsync(service, wallet)));

        Assert.True((await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(
                DbPath(walletDir))).Count == 0,
            "POSITION RULE: the unparseable-journal refusal must sit AFTER the bounded sweep and its "
            + "empty-page barrier, at the same point the phase-only journal refuses from. Refusing "
            + "before the sweep leaves this status-5 row undrained on every refresh, and "
            + "DeleteWalletAsync's own orphan probe then refuses too — no in-product escape at all. "
            + "Draining first is safe on its own terms: an Initiated row proves send_end never "
            + "committed and, donation transfers being refused, never broadcast.");
        AssertJournalRetainedWithoutANewDurableBlocker(walletDir);
        await using (var quarantineStanding = harness.Factory.CreateContext())
            Assert.True((await quarantineStanding.RGBWallets.AsNoTracking()
                .SingleAsync(w => w.Id == wallet.Id)).NeedsRecovery);

        await service.DeleteWalletAsync(wallet.Id);

        await using var ctx = harness.Factory.CreateContext();
        Assert.False(await ctx.RGBWallets.AsNoTracking().AnyAsync(w => w.Id == wallet.Id),
            "the status-1 hold keeps the journal, so deletion is the only escape left and it must "
            + "work: the sweep already proved nothing outbound is staged.");
    }

    [Fact]
    public async Task WaitingCounterpartyProbe_ThrowsRatherThanReportingAbsenceItCannotSee()
    {
        var missing = Path.Combine(_baseDir, "absent", "rgb_lib_db");

        var unreadable = await Record.ExceptionAsync(
            () => RGBWalletService.HasOutgoingBatchStatusAsync(missing, 1));

        Assert.True(unreadable != null,
            "INVARIANT — unknown is never absent: this probe is the evidence a discharge rests on, so "
            + "a database it cannot read must throw into the caller's refusal rather than report "
            + "'no such row'. Returning false here discharges a quarantine on the strength of a file "
            + "nobody could open.");
        Assert.True((await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(missing)).Count == 0,
            "the sweep keeps its empty-on-missing answer on purpose — it only decides what to fail — "
            + "which is exactly why the deletion guard tests the file itself before trusting it.");
    }

    [IntegrationFact]
    public async Task DeleteWallet_LeavesNoWorkerMarkerWhenItRefusesAfterPublishingOne()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 13, status: 5, incoming: false);
        WriteCorruptJournal(walletDir);

        var refusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.DeleteWalletAsync(wallet.Id));

        AssertQuarantined(refusal);
        Assert.Contains("staged outbound transfers", refusal.Message, StringComparison.Ordinal);
        Assert.False(RgbNativeSendLease.Exists(walletDir),
            "this refusal happens after the deletion marker is published, and base published none "
            + "before refusing. A retained marker would refuse every rgb-lib wallet construction "
            + "until an unrelated recovery adopted it.");
    }

    [IntegrationFact]
    public async Task DeleteWallet_RefusesAnUnparseableJournalWhenTheProbeDatabaseIsMissing()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        WriteCorruptJournal(walletDir);
        Assert.False(File.Exists(DbPath(walletDir)));

        AssertQuarantined(await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.DeleteWalletAsync(wallet.Id)));

        await using var ctx = harness.Factory.CreateContext();
        Assert.True(await ctx.RGBWallets.AsNoTracking().AnyAsync(w => w.Id == wallet.Id),
            "with no rgb_lib_db the orphan probe answers 'empty' from silence rather than from "
            + "evidence, so it cannot authorise removing the only row a recovery sweep can discover.");
    }

    [IntegrationFact]
    public async Task DeleteWallet_SucceedsOnAnUnparseableJournalWithAnEmptyOrphanProbe()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        await CreateSchemaAsync(walletDir);
        await InsertBatchAsync(walletDir, batch: 2, status: 3, incoming: false);
        WriteCorruptJournal(walletDir);

        await service.DeleteWalletAsync(wallet.Id);

        await using var ctx = harness.Factory.CreateContext();
        Assert.False(await ctx.RGBWallets.AsNoTracking().AnyAsync(w => w.Id == wallet.Id),
            "deletion is the operator's last escape. Without it a hosted merchant whose journal is "
            + "corrupt can neither use the wallet nor register a replacement for the store.");
    }

    [IntegrationFact]
    public async Task DeleteWallet_StillRefusesAJournalThatParsesAndAnOrphanedBatch()
    {
        await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
        await harness.RunPluginMigrationsAsync();
        var (service, wallet, walletDir) = await BuildAsync(harness, quarantined: true);
        await CreateSchemaAsync(walletDir);
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath(walletDir))!);
        RgbSendRecoveryJournal.Write(JournalPath(walletDir), RgbSendRecoveryPhase.Staged);

        AssertQuarantined(await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.DeleteWalletAsync(wallet.Id)));

        RgbSendRecoveryJournal.Delete(JournalPath(walletDir));
        await InsertBatchAsync(walletDir, batch: 3, status: 5, incoming: false);
        WriteCorruptJournal(walletDir);

        var orphanRefusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => service.DeleteWalletAsync(wallet.Id));
        AssertQuarantined(orphanRefusal);
        Assert.Contains("staged outbound transfers", orphanRefusal.Message, StringComparison.Ordinal);
    }

    const string QuarantineExceptionName =
        "BTCPayServer.Plugins.RgbUtexo.Services.RgbWalletQuarantinedException";

    static void AssertQuarantined(Exception refusal) =>
        Assert.True(QuarantineExceptionName == refusal.GetType().FullName,
            $"expected a typed quarantine refusal, got {refusal.GetType().FullName}: "
            + $"{refusal.Message}. The type is compared by assembly-qualified NAME because the plugin "
            + "and the RgbRestoreHelper both declare it in this namespace, so the test assembly cannot "
            + "reference either one directly.");

    static Task ReconcileAsync(RGBWalletService service, RGBWallet wallet)
    {
        var method = typeof(RGBWalletService).GetMethod("ReconcileWalletRecoveryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, [wallet, CancellationToken.None, null, false])!;
    }

    async Task<(RGBWalletService Service, RGBWallet Wallet, string WalletDir)> BuildAsync(
        RgbPluginDatabaseHarness? harness = null, bool quarantined = false,
        bool refreshRecreatesTheDatabase = false)
    {
        var cfg = harness?.Configuration ?? new RGBConfiguration(_baseDir);
        var db = harness?.Factory ?? new RGBPluginDbContextFactory(
            Microsoft.Extensions.Options.Options.Create(new BTCPayServer.Abstractions.Models.DatabaseOptions
            {
                ConnectionString = "Host=127.0.0.1;Database=unused;Username=u;Password=p"
            }));
        var mnemonicProtection = new MnemonicProtectionService(
            new EphemeralDataProtectionProvider(), NullLogger<MnemonicProtectionService>.Instance);
        var wallet = new RGBWallet
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = "store-under-test",
            Network = WalletNetwork,
            XpubVanilla = "xpub-vanilla",
            XpubColored = "xpub-colored",
            MasterFingerprint = Fingerprint,
            IsActive = true,
            NeedsRecovery = quarantined
        };
        if (harness != null)
        {
            await using var ctx = harness.Factory.CreateContext();
            ctx.RGBWallets.Add(wallet);
            await ctx.SaveChangesAsync();
        }

        var service = new RGBWalletService(
            new RecoveryStubRgbLib(cfg, refreshRecreatesTheDatabase), db, cfg, mnemonicProtection,
            new RgbWalletSignerProvider(db, mnemonicProtection,
                NullLogger<RgbWalletSignerProvider>.Instance),
            null!, null!, NullLogger<RGBWalletService>.Instance, null!, null!);
        var walletDir = Path.Combine(cfg.GetWalletDataDir(wallet.Id, WalletNetwork), Fingerprint);
        Directory.CreateDirectory(walletDir);
        return (service, wallet, walletDir);
    }

    static string DbPath(string walletDir) => Path.Combine(walletDir, "rgb_lib_db");

    static string JournalPath(string walletDir) =>
        Path.Combine(walletDir, RgbSendRecoveryJournal.FileName);

    static void WriteCorruptJournal(string walletDir)
    {
        Directory.CreateDirectory(walletDir);
        File.WriteAllText(JournalPath(walletDir), CorruptJournalContents);
    }

    static void CreateStockFiles(string walletDir)
    {
        var stockDir = Path.Combine(walletDir, "rgb");
        Directory.CreateDirectory(stockDir);
        foreach (var name in new[] { "index.dat", "stash.dat", "state.dat" })
            File.WriteAllBytes(Path.Combine(stockDir, name), [0]);
    }

    static async Task CreateSchemaAsync(string walletDir)
    {
        Directory.CreateDirectory(walletDir);
        await ExecuteAsync(walletDir, """
            CREATE TABLE batch_transfer (idx INTEGER PRIMARY KEY, status INTEGER NOT NULL, txid TEXT NULL);
            CREATE TABLE asset_transfer (idx INTEGER PRIMARY KEY, batch_transfer_idx INTEGER NOT NULL);
            CREATE TABLE transfer (idx INTEGER PRIMARY KEY, asset_transfer_idx INTEGER NOT NULL, incoming INTEGER NOT NULL);
            """);
    }

    static async Task InsertBatchAsync(string walletDir, int batch, int status, bool incoming)
    {
        await ExecuteAsync(walletDir,
            $"INSERT INTO batch_transfer(idx,status,txid) VALUES({batch},{status},NULL);"
            + $"INSERT INTO asset_transfer(idx,batch_transfer_idx) VALUES({batch},{batch});"
            + $"INSERT INTO transfer(idx,asset_transfer_idx,incoming) VALUES({batch},{batch},{(incoming ? 1 : 0)})");
    }

    static async Task ExecuteAsync(string walletDir, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath(walletDir)}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_baseDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    sealed class RecoveryStubRgbLib : IRgbLibService
    {
        readonly RGBConfiguration _cfg;
        readonly bool _refreshRecreatesTheDatabase;

        internal RecoveryStubRgbLib(RGBConfiguration cfg, bool refreshRecreatesTheDatabase = false)
        {
            _cfg = cfg;
            _refreshRecreatesTheDatabase = refreshRecreatesTheDatabase;
        }

        public string GetWalletDataDir(string walletId, string walletNetwork)
            => _cfg.GetWalletDataDir(walletId, walletNetwork);

        public async Task RefreshAsync(string walletId, CancellationToken ct = default)
        {
            if (!_refreshRecreatesTheDatabase) return;
            await CreateSchemaAsync(
                Path.Combine(GetWalletDataDir(walletId, WalletNetwork), Fingerprint));
        }
        public bool UnloadWallet(string walletId) => true;
        public void Dispose() { }

        public RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network) => throw new NotImplementedException();
        public Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbMatchedTransfer>> ListIncomingTransfersForRecipientsAsync(string walletId, IReadOnlyCollection<string> recipientIds, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SnapshotStockAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbVerificationSnapshot> SnapshotVerificationStateAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SendBeginAsync(string walletId, string recipientMapJson, float feeRate, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SendEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateConsignmentsAsync(string walletId, string psbt, CancellationToken ct = default) => throw new NotImplementedException();
        public async Task FailTransfersAsync(string walletId, int batchTransferIdx, bool noAssetOnly, bool skipSync, CancellationToken ct = default)
        {
            var dbPath = Path.Combine(GetWalletDataDir(walletId, WalletNetwork), Fingerprint, "rgb_lib_db");
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"UPDATE batch_transfer SET status = 4 WHERE idx = {batchTransferIdx} AND status = 5";
            await command.ExecuteNonQueryAsync(ct);
        }
        public RgbInvoiceData DecodeInvoice(string invoiceString) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public RgbKeys GenerateKeys(string network) => throw new NotImplementedException();
    }
}
