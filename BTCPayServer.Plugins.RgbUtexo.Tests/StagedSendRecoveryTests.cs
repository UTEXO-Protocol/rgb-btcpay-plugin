using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Data.Sqlite;
using NBitcoin;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class StagedSendRecoveryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"rgb-staged-send-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_dir, "rgb_lib_db");

    [Fact]
    public async Task Discovery_ReturnsOnlyOutboundInitiatedBatches()
    {
        await CreateSchema();
        await InsertBatch(batch: 10, status: 5, incoming: false);
        await InsertBatch(batch: 11, status: 5, incoming: true);
        await InsertBatch(batch: 12, status: 1, incoming: false);
        await InsertBatch(batch: 13, status: 4, incoming: false);
        await InsertBatch(batch: 14, status: 0, incoming: false);
        await InsertBatch(batch: 15, status: 2, incoming: false);
        await InsertBatch(batch: 16, status: 3, incoming: false);
        await InsertBatch(batch: 17, status: 6, incoming: false);

        var found = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);

        Assert.Equal(new[] { 10 }, found);
    }

    [Fact]
    public async Task Discovery_DeduplicatesMultiAssetBatchAndIsDeterministicallyOrdered()
    {
        await CreateSchema();
        await InsertBatch(batch: 20, status: 5, incoming: false);
        await InsertTransferForExistingBatch(batch: 20, incoming: false);
        await InsertBatch(batch: 3, status: 5, incoming: false);

        var found = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);

        Assert.Equal(new[] { 3, 20 }, found);
    }

    [Fact]
    public async Task Discovery_IsIdempotentAfterBatchWasFailed()
    {
        await CreateSchema();
        await InsertBatch(batch: 7, status: 5, incoming: false);
        Assert.Equal(new[] { 7 }, await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));

        await Execute("UPDATE batch_transfer SET status = 4 WHERE idx = 7");

        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task ExactOutgoingBatchStatus_DistinguishesInitiatedCommittedAndMissing()
    {
        await CreateSchema();
        await InsertBatch(batch: 7, status: 5, incoming: false);
        await InsertBatch(batch: 8, status: 1, incoming: false);
        await InsertBatch(batch: 9, status: 2, incoming: true);

        Assert.Equal(5, await RGBWalletService.FindOutgoingBatchStatusAsync(DbPath, 7));
        Assert.Equal(1, await RGBWalletService.FindOutgoingBatchStatusAsync(DbPath, 8));
        Assert.Null(await RGBWalletService.FindOutgoingBatchStatusAsync(DbPath, 9));
        Assert.Null(await RGBWalletService.FindOutgoingBatchStatusAsync(DbPath, 10));
    }

    [Fact]
    public async Task ExactOutgoingBatchRow_ReturnsStatusAndTxidForOutgoingRows()
    {
        await CreateSchema();
        await InsertBatch(batch: 20, status: 1, incoming: false, txid: "outgoing-two");
        await InsertTransferForExistingBatch(batch: 20, incoming: false);
        await InsertBatch(batch: 21, status: 3, incoming: false, txid: "outgoing-three");
        await InsertTransferForExistingBatch(batch: 21, incoming: false);
        await InsertTransferForExistingBatch(batch: 21, incoming: false);
        await InsertBatch(batch: 22, status: 2, incoming: true, txid: "outgoing-two");
        await InsertBatch(batch: 23, status: 1, incoming: false, txid: null);

        Assert.Equal((1, "outgoing-two"),
            await RGBWalletService.FindOutgoingBatchRowAsync(DbPath, 20));
        Assert.Equal((3, "outgoing-three"),
            await RGBWalletService.FindOutgoingBatchRowAsync(DbPath, 21));
        Assert.Null(await RGBWalletService.FindOutgoingBatchRowAsync(DbPath, 22));
        var nullTxid = await RGBWalletService.FindOutgoingBatchRowAsync(DbPath, 23);
        Assert.NotNull(nullTxid);
        Assert.Equal(1, nullTxid.Value.Status);
        Assert.Null(nullTxid.Value.Txid);
        Assert.Null(await RGBWalletService.FindOutgoingBatchRowAsync(DbPath, 24));
    }

    [Fact]
    public async Task OutgoingStatusProbeIsExactAndIgnoresIncomingRows()
    {
        await CreateSchema();
        await InsertBatch(batch: 7, status: 1, incoming: true);
        await InsertBatch(batch: 8, status: 5, incoming: false);

        Assert.False(await RGBWalletService.HasOutgoingBatchStatusAsync(DbPath, 1));
        Assert.True(await RGBWalletService.HasOutgoingBatchStatusAsync(DbPath, 5));

        await InsertBatch(batch: 9, status: 1, incoming: false);
        Assert.True(await RGBWalletService.HasOutgoingBatchStatusAsync(DbPath, 1));
    }

    [Fact]
    public async Task MissingDatabase_HasNoOrphans()
    {
        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task Discovery_IsMemoryBoundedAndAdvancesByDurableStatus()
    {
        await CreateSchema();
        for (var i = 1; i <= RGBWalletService.StagedRecoveryBatchSize + 5; i++)
            await InsertBatch(i, status: 5, incoming: false);

        var first = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);
        Assert.Equal(RGBWalletService.StagedRecoveryBatchSize, first.Count);
        Assert.Equal(Enumerable.Range(1, RGBWalletService.StagedRecoveryBatchSize), first);
        await Execute($"UPDATE batch_transfer SET status = 4 WHERE idx <= {RGBWalletService.StagedRecoveryBatchSize}");

        Assert.Equal(Enumerable.Range(RGBWalletService.StagedRecoveryBatchSize + 1, 5),
            await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task ProductionDrainProcessesMoreThanOneBoundedPage()
    {
        await CreateSchema();
        var total = RGBWalletService.StagedRecoveryBatchSize + 5;
        for (var i = 1; i <= total; i++)
            await InsertBatch(i, status: 5, incoming: false);

        var first = await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath);
        var failed = new List<int>();
        await RGBWalletService.DrainOrphanedOutgoingBatchesAsync(
            first,
            () => RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath),
            async idx =>
            {
                failed.Add(idx);
                await Execute($"UPDATE batch_transfer SET status = 4 WHERE idx = {idx}");
            });

        Assert.Equal(Enumerable.Range(1, total), failed);
        Assert.Empty(await RGBWalletService.FindOrphanedOutgoingBatchIndicesAsync(DbPath));
    }

    [Fact]
    public async Task ProductionDrainFailsClosedWhenAReportedSuccessMakesNoProgress()
    {
        IReadOnlyList<int> page = [7];
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            RGBWalletService.DrainOrphanedOutgoingBatchesAsync(
                page, () => Task.FromResult(page), _ => Task.CompletedTask));
        Assert.Contains("no durable progress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryJournal_IsDurableOverwriteableAndIdempotentlyDeletable()
    {
        var path = Path.Combine(_dir, "fingerprint", RgbSendRecoveryJournal.FileName);

        RgbSendRecoveryJournal.Write(path, RgbSendRecoveryPhase.Staged);
        Assert.Equal(RgbSendRecoveryPhase.Staged, RgbSendRecoveryJournal.Read(path)!.Phase);

        RgbSendRecoveryJournal.WriteSendEnd(
            path, 42, "00aabb", new string('1', 64), "cHNidP8BAAoCAAAAAA==");
        var sendEnd = RgbSendRecoveryJournal.Read(path)!;
        Assert.Equal(RgbSendRecoveryPhase.SendEndIndeterminate, sendEnd.Phase);
        Assert.Equal(42, sendEnd.BatchTransferIdx);
        Assert.Equal("00aabb", sendEnd.RawTransaction);
        Assert.Equal(new string('1', 64), sendEnd.TransactionId);
        Assert.Equal("cHNidP8BAAoCAAAAAA==", sendEnd.SignedPsbt);
        Assert.True(sendEnd.HasExactTransactionRecovery);
        Assert.True(sendEnd.HasSendEndReplay);

        RgbSendRecoveryJournal.Delete(path);
        RgbSendRecoveryJournal.Delete(path);
        Assert.Null(RgbSendRecoveryJournal.Read(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void CorruptRecoveryJournal_FailsClosed()
    {
        var path = Path.Combine(_dir, "fingerprint", RgbSendRecoveryJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unknown");

        Assert.Throws<InvalidDataException>(() => RgbSendRecoveryJournal.Read(path));
    }

    [Fact]
    public void LegacyIndeterminateJournalIsRecognizedButCannotClaimBroadcastRecovery()
    {
        var path = Path.Combine(_dir, "fingerprint", RgbSendRecoveryJournal.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "send-end-indeterminate\n");

        var record = RgbSendRecoveryJournal.Read(path)!;
        Assert.Equal(RgbSendRecoveryPhase.SendEndIndeterminate, record.Phase);
        Assert.False(record.HasExactTransactionRecovery);
    }

    [Fact]
    public async Task BroadcastRecoveryIsIdempotentWhenTransactionIsAlreadyKnown()
    {
        var (raw, txid) = RecoveryTransaction();
        var chain = new FakeChainClient();
        chain.Lookups.Enqueue(() => Task.FromResult(raw));

        await RGBWalletService.EnsureTransactionBroadcastAsync(
            chain, Network.RegTest, raw, txid);

        Assert.Equal(1, chain.LookupCalls);
        Assert.Equal(0, chain.BroadcastCalls);
    }

    [Fact]
    public async Task BroadcastRecoveryPublishesUnknownTransaction()
    {
        var (raw, txid) = RecoveryTransaction();
        var chain = new FakeChainClient { BroadcastResult = txid };
        chain.Lookups.Enqueue(() => Task.FromException<string>(new InvalidOperationException("missing")));

        await RGBWalletService.EnsureTransactionBroadcastAsync(
            chain, Network.RegTest, raw, txid);

        Assert.Equal(1, chain.LookupCalls);
        Assert.Equal(1, chain.BroadcastCalls);
    }

    [Theory]
    [InlineData(1, false)] // WaitingCounterparty: recipient ACK has not authorized broadcast.
    [InlineData(2, true)]  // WaitingConfirmations: native ACK processing already broadcast.
    [InlineData(3, false)] // Settled: already confirmed.
    [InlineData(4, false)] // Failed: must never broadcast.
    [InlineData(5, false)] // Initiated: send_end has not committed.
    [InlineData(6, true)]  // WaitingSafeHeight: native broadcast transition has completed.
    public void RecoveryRebroadcastRequiresProofOfTheNativeAckTransition(int status, bool expected)
    {
        Assert.Equal(expected, RGBWalletService.ShouldRebroadcastRecoveredTransaction(status));
    }

    [Theory]
    [InlineData(1, true)]  // WaitingCounterparty: send_end completed; wait for recipient ACK.
    [InlineData(2, false)] // WaitingConfirmations: handled by the exact rebroadcast path.
    [InlineData(3, true)]  // Settled: already confirmed.
    [InlineData(4, true)]  // Failed: recipient/native protocol rejected the transfer.
    [InlineData(5, false)] // Initiated: exact send_end replay must move it forward first.
    [InlineData(6, false)] // WaitingSafeHeight: handled by the exact rebroadcast path.
    public void RecoveryAcceptsKnownNonBroadcastStatesWithoutPermanentQuarantine(int status, bool expected)
    {
        Assert.Equal(expected, RGBWalletService.IsRecoveredTransactionSafeWithoutBroadcast(status));
    }

    [Theory]
    [InlineData(true, true, 1, false, false)]
    [InlineData(false, true, 1, false, true)]
    [InlineData(false, true, 2, true, false)]
    [InlineData(false, true, 5, true, false)]
    [InlineData(false, false, null, true, true)]
    [InlineData(false, false, null, false, false)]
    public void IncompleteJournalNeverDischargesAStatus1ThatMayNeedItsPsbt(
        bool hasReplay, bool hasExact, int? exactStatus, bool anyStatus1, bool expected)
    {
        Assert.Equal(expected,
            RGBWalletService.ShouldQuarantineIncompleteAckRecovery(
                hasReplay, hasExact, exactStatus, anyStatus1));
    }

    [Theory]
    [InlineData(5, true, true)]
    [InlineData(5, false, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    [InlineData(6, true, false)]
    public void OnlyAConfirmedReapedReplayFailureMayFallBackToFailingInitiated(
        int status, bool replayFailedAfterReap, bool expected)
    {
        Assert.Equal(expected,
            RGBWalletService.ShouldFailInitiatedAfterReapedReplayFailure(
                status, replayFailedAfterReap));
    }

    [Fact]
    public void AckBroadcastArtifactsAreRestoredFromTheDurableExactJournal()
    {
        var txid = new string('a', 64);
        var walletDir = Path.Combine(_dir, "artifact-wallet");
        var transferDir = Path.Combine(walletDir, "transfers", txid);
        Directory.CreateDirectory(transferDir);
        File.WriteAllText(Path.Combine(transferDir, "fascia"), "exact fascia");
        File.WriteAllText(Path.Combine(transferDir, "signed.psbt"), "truncated");

        RgbSendRecoveryJournal.FsyncPreSendEndArtifacts(walletDir, txid);
        RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
            walletDir, txid, "durable signed psbt");

        Assert.Equal("durable signed psbt",
            File.ReadAllText(Path.Combine(transferDir, "signed.psbt")));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(transferDir, "signed.psbt"));
            var publicBits = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, mode & publicBits);
        }
    }

    [Fact]
    public void MissingAckBroadcastFasciaFailsClosedBeforeStatusOneIsAccepted()
    {
        var walletDir = Path.Combine(_dir, "missing-artifact-wallet");
        var txid = new string('b', 64);
        Directory.CreateDirectory(Path.Combine(walletDir, "transfers", txid));

        Assert.Throws<FileNotFoundException>(() =>
            RgbSendRecoveryJournal.RestoreAndFsyncAckBroadcastArtifacts(
                walletDir, txid, "durable signed psbt"));
    }

    [Fact]
    public void RecoveryPsbtMustBindTheExactDurableTransaction()
    {
        var (psbt, raw, txid) = RecoveryPsbt();

        RGBWalletService.ValidateRecoveryPsbt(psbt, raw, txid, "regtest");

        var other = Network.RegTest.CreateTransaction();
        other.Inputs.Add(new TxIn(new OutPoint(uint256.One, 1)));
        other.Outputs.Add(new TxOut(Money.Satoshis(2_000), Script.Empty));
        Assert.Throws<InvalidDataException>(() =>
            RGBWalletService.ValidateRecoveryPsbt(psbt, other.ToHex(), txid, "regtest"));
    }

    [Fact]
    public void ReplayedSendEndMustReturnTheExactDurableTransactionId()
    {
        var txid = new string('1', 64);
        RGBWalletService.ValidateSendEndTransactionId($"{{\"txid\":\"{txid}\"}}", txid);

        Assert.Throws<InvalidDataException>(() =>
            RGBWalletService.ValidateSendEndTransactionId(
                $"{{\"txid\":\"{new string('2', 64)}\"}}", txid));
    }

    [Fact]
    public async Task BroadcastErrorIsAcceptedOnlyAfterExactTransactionVerification()
    {
        var (raw, txid) = RecoveryTransaction();
        var chain = new FakeChainClient { BroadcastError = new InvalidOperationException("already known") };
        chain.Lookups.Enqueue(() => Task.FromException<string>(new InvalidOperationException("not indexed yet")));
        chain.Lookups.Enqueue(() => Task.FromResult(raw));

        await RGBWalletService.EnsureTransactionBroadcastAsync(
            chain, Network.RegTest, raw, txid);

        Assert.Equal(2, chain.LookupCalls);
        Assert.Equal(1, chain.BroadcastCalls);
    }

    [Theory]
    [InlineData("WaitingCounterparty", 1)]
    [InlineData("WaitingConfirmations", 2)]
    [InlineData("Settled", 3)]
    [InlineData("Failed", 4)]
    [InlineData("Initiated", 5)]
    [InlineData("WaitingSafeHeight", 6)]
    public void TransferResponseStringStatusesMatchPackagedRgbLib(string status, int expected)
    {
        using var json = JsonDocument.Parse($"\"{status}\"");
        var response = new TransferResponse { Status = json.RootElement.Clone() };
        Assert.Equal(expected, response.GetStatusInt());
    }

    static (string Raw, string Txid) RecoveryTransaction()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(1_000), Script.Empty));
        return (transaction.ToHex(), transaction.GetHash().ToString());
    }

    static (string Psbt, string Raw, string Txid) RecoveryPsbt()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(1_000), Script.Empty));
        var psbt = PSBT.FromTransaction(transaction, Network.RegTest);
        psbt.Inputs[0].FinalScriptSig = Script.Empty;
        Assert.True(psbt.TryFinalize(out var errors),
            errors == null ? "PSBT finalization failed" : string.Join("; ", errors));
        var finalized = psbt.ExtractTransaction();
        return (psbt.ToBase64(), finalized.ToHex(), finalized.GetHash().ToString());
    }

    sealed class FakeChainClient : IBitcoinChainClient
    {
        internal readonly Queue<Func<Task<string>>> Lookups = new();
        internal string? BroadcastResult;
        internal Exception? BroadcastError;
        internal int LookupCalls;
        internal int BroadcastCalls;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
        {
            LookupCalls++;
            return Lookups.Dequeue()();
        }

        public Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
        {
            BroadcastCalls++;
            return BroadcastError != null
                ? Task.FromException<string>(BroadcastError)
                : Task.FromResult(BroadcastResult!);
        }

        public Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
            Script script, CancellationToken ct = default) => throw new NotSupportedException();

        public void Dispose() { }
    }

    async Task CreateSchema()
    {
        Directory.CreateDirectory(_dir);
        await Execute("""
            CREATE TABLE batch_transfer (idx INTEGER PRIMARY KEY, status INTEGER NOT NULL, txid TEXT NULL);
            CREATE TABLE asset_transfer (idx INTEGER PRIMARY KEY, batch_transfer_idx INTEGER NOT NULL);
            CREATE TABLE transfer (idx INTEGER PRIMARY KEY, asset_transfer_idx INTEGER NOT NULL, incoming INTEGER NOT NULL);
            """);
    }

    async Task InsertBatch(int batch, int status, bool incoming, string? txid = null)
    {
        var sqlTxid = txid == null ? "NULL" : $"'{txid.Replace("'", "''")}'";
        await Execute($"INSERT INTO batch_transfer(idx,status,txid) VALUES({batch},{status},{sqlTxid})");
        await InsertTransferForExistingBatch(batch, incoming);
    }

    async Task InsertTransferForExistingBatch(int batch, bool incoming)
    {
        var asset = await ScalarLong("SELECT COALESCE(MAX(idx),0)+1 FROM asset_transfer");
        var transfer = await ScalarLong("SELECT COALESCE(MAX(idx),0)+1 FROM transfer");
        await Execute($"INSERT INTO asset_transfer(idx,batch_transfer_idx) VALUES({asset},{batch})");
        await Execute($"INSERT INTO transfer(idx,asset_transfer_idx,incoming) VALUES({transfer},{asset},{(incoming ? 1 : 0)})");
    }

    async Task Execute(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={DbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    async Task<long> ScalarLong(string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={DbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
