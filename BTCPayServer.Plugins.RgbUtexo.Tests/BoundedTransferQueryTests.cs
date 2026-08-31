using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Data.Sqlite;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class BoundedTransferQueryTests : IDisposable
{
    readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"rgb-bounded-transfer-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(_dir, "rgb_lib_db");

    [Fact]
    public async Task QueryIgnoresUnrelatedHistoryAndReturnsOnlyRequestedRecipients()
    {
        await CreateSchema();
        for (var i = 1; i <= 200; i++)
            await Insert(i, $"asset-{i}", $"unrelated-{i}", status: 3, amount: i);
        await Insert(1001, "wanted-a", "recipient-a", status: 2, amount: 42);
        await Insert(1002, "wanted-b", "recipient-b", status: 3, amount: 84);

        var matches = await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient-a", "recipient-b"]);

        Assert.Collection(matches,
            first =>
            {
                Assert.Equal("wanted-a", first.AssetId);
                Assert.Equal(42, first.Transfer.Amount);
                Assert.Equal(2, first.Transfer.Status);
            },
            second =>
            {
                Assert.Equal("wanted-b", second.AssetId);
                Assert.Equal(84, second.Transfer.Amount);
                Assert.Equal(3, second.Transfer.Status);
            });
    }

    [Fact]
    public async Task QueryReturnsAtMostOneAuthoritativeRowPerRecipient()
    {
        await CreateSchema();
        await Insert(1, "first-asset", "recipient", status: 2, amount: 10);
        await Insert(2, "duplicate-asset", "recipient", status: 3, amount: 20);

        var matches = await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]);

        var match = Assert.Single(matches);
        Assert.Equal(1, match.Transfer.Idx);
        Assert.Equal("first-asset", match.AssetId);
    }

    [Fact]
    public async Task QueryAppliesAssetFilterBeforeRanking()
    {
        await CreateSchema();
        await Insert(1, "other", "recipient", status: 2, amount: 10);
        await Insert(2, "wanted", "recipient", status: 3, amount: 20);

        var match = Assert.Single(
            await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
                DbPath, ["recipient"], "wanted"));

        Assert.Equal("wanted", match.AssetId);
        Assert.Equal(2, match.Transfer.Idx);
    }

    [Fact]
    public async Task QueryRejectsMoreThanOneDurableInvoicePage()
    {
        var recipients = Enumerable.Range(0, RGBInvoiceListener.DurableInvoicePageSize + 1)
            .Select(i => $"recipient-{i}").ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RgbLibService.QueryIncomingTransfersForRecipientsAsync(DbPath, recipients));
    }

    [Fact]
    public void GeneralTransferListUsedByTheUiHasAnExplicitSqlLimit()
    {
        var source = RgbConfigBoundsTests.ReadRepoFile(
            Path.Combine("Services", "RgbLibService.cs"));
        var start = source.IndexOf(
            "public async Task<List<RgbTransfer>> ListTransfersAsync", StringComparison.Ordinal);
        var end = source.IndexOf(
            "public async Task<List<RgbMatchedTransfer>>", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];

        Assert.Contains("LIMIT @limit", method, StringComparison.Ordinal);
        Assert.Contains("MaxTransferListRows", method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UiTransferQueryHasOneGlobalLimitAcrossAllAssets()
    {
        await CreateSchema();
        await InsertHistory(1001);

        var transfers = await RgbLibService.QueryRecentTransfersAsync(DbPath);

        Assert.Equal(1000, transfers.Count);
        Assert.Equal(1001, transfers[0].Idx);
        Assert.Equal(2, transfers[^1].Idx);
        Assert.All(transfers, t => Assert.Equal("TOK", t.AssetTicker));
    }

    [Fact]
    public async Task UiTransferQueryAppliesOptionalAssetFilterInsideTheBoundedSql()
    {
        await CreateSchema();
        await Insert(1, "other", "recipient-1", status: 3, amount: 10);
        await Insert(2, "wanted", "recipient-2", status: 3, amount: 20);

        var transfer = Assert.Single(
            await RgbLibService.QueryRecentTransfersAsync(DbPath, "wanted"));

        Assert.Equal("wanted", transfer.AssetId);
        Assert.Equal("TOK", transfer.AssetTicker);
    }

    [Fact]
    public void TransfersControllerUsesOneGloballyBoundedQuery()
    {
        var source = RgbConfigBoundsTests.ReadRepoFile(
            Path.Combine("Controllers", "RGBController.cs"));
        var start = source.IndexOf(
            "public async Task<IActionResult> Transfers", StringComparison.Ordinal);
        var end = source.IndexOf(
            "[HttpPost(\"receive-any-asset\")]", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];

        Assert.Equal(1, method.Split("GetTransfersAsync(", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ListAssetsAsync(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach", method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitPaymentCreditsEveryFungibleAssignmentNotJustTheFirstRow()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":300}"),
            (1, 1, "{\"Fungible\":700}"));

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        Assert.Equal(1000, match.Transfer.Amount);
    }

    [Fact]
    public async Task InflationRightAndNonFungibleSiblingsNeitherHideNorInflateTheFungibleCredit()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"InflationRight\":5000}"),
            (1, 1, "\"NonFungible\""),
            (1, 1, "\"Any\""),
            (1, 1, "{\"Fungible\":42}"));

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        Assert.Equal(42, match.Transfer.Amount);
    }

    [Fact]
    public async Task CreditIsIndependentOfTheOrderRowsWereInserted()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient-forward", status: 3,
            (1, 1, "{\"Fungible\":11}"),
            (1, 1, "{\"InflationRight\":9}"),
            (1, 1, "\"NonFungible\""),
            (1, 1, "{\"Fungible\":22}"));
        await InsertWithColorings(2, "asset", "recipient-reverse", status: 3,
            (1, 1, "{\"Fungible\":22}"),
            (1, 1, "\"NonFungible\""),
            (1, 1, "{\"InflationRight\":9}"),
            (1, 1, "{\"Fungible\":11}"));

        var matches = await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient-forward", "recipient-reverse"]);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(33, m.Transfer.Amount));
    }

    [Fact]
    public async Task AReplayedConsignmentDoesNotCreditTheSameAssignmentTwice()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":300}"),
            (1, 1, "{\"Fungible\":700}"),
            (1, 1, "{\"Fungible\":300}"),
            (1, 1, "{\"Fungible\":700}"));

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        Assert.Equal(1000, match.Transfer.Amount);
    }

    [Fact]
    public async Task InputAndChangeColoringsAreNeverCreditedToAnIncomingTransfer()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":10}"),
            (1, 3, "{\"Fungible\":999999}"),
            (1, 4, "{\"Fungible\":888888}"));

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        Assert.Equal(10, match.Transfer.Amount);
    }

    [Fact]
    public async Task ASupplyBeyondSignedRangeSaturatesInsteadOfWrappingNegativeOrToZero()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient-pair", status: 3,
            (1, 1, "{\"Fungible\":9223372036854775808}"),
            (2, 1, "{\"Fungible\":9223372036854775808}"));
        await InsertWithColorings(2, "asset", "recipient-single", status: 3,
            (1, 1, "{\"Fungible\":18446744073709551615}"));

        var matches = await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient-pair", "recipient-single"]);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal(long.MaxValue, m.Transfer.Amount));
    }

    [Fact]
    public async Task AssignmentsBeyondTheWorkBoundAreDroppedRatherThanCreditedOrWrapped()
    {
        await CreateSchema();
        var overBound = Enumerable
            .Range(1, RgbLibService.MaxCreditedAssignmentsPerAssetTransfer + 1)
            .Select(i => (TxoIdx: i, Type: 1, Assignment: $"{{\"Fungible\":{i}}}"))
            .ToArray();
        await InsertWithColorings(1, "asset", "recipient", status: 3, overBound);

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        var boundedTotal = (long)RgbLibService.MaxCreditedAssignmentsPerAssetTransfer
            * (RgbLibService.MaxCreditedAssignmentsPerAssetTransfer + 1) / 2;
        Assert.Equal(boundedTotal, match.Transfer.Amount);
    }

    [Fact]
    public async Task AMalformedAssignmentIsSkippedRatherThanFailingTheWholeSweep()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "not json at all"),
            (1, 1, "{\"Fungible\":7}"));

        var match = Assert.Single(await RgbLibService.QueryIncomingTransfersForRecipientsAsync(
            DbPath, ["recipient"]));

        Assert.Equal(7, match.Transfer.Amount);
    }

    [Fact]
    public async Task UiTransferListSumsEveryFungibleAssignmentInsteadOfWhicheverRowSqliteReachesFirst()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":300}"),
            (2, 1, "{\"Fungible\":700}"));

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        Assert.True(transfer.Amount == 1000,
            $"the Transfers page showed {transfer.Amount} of 1000. rgb-lib writes one coloring row per "
            + "assignment and iterates a randomised HashMap doing it, so a LIMIT 1 with no ORDER BY "
            + "reads whichever row SQLite reaches first: a short money figure that changes between "
            + "refreshes over identical data.");
    }

    [Fact]
    public async Task UiTransferListDoesNotCreditAReplayedAssignmentTwice()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":300}"),
            (1, 1, "{\"Fungible\":700}"),
            (1, 1, "{\"Fungible\":300}"),
            (1, 1, "{\"Fungible\":700}"));

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        Assert.True(transfer.Amount == 1000,
            $"the Transfers page showed {transfer.Amount} of 1000. set_coloring is a bare INSERT with "
            + "no conflict clause and refresh replays the coloring write when the consignment ACK "
            + "fails, so a plain SUM reports one payment twice; deduplicating on (txo_idx, assignment) "
            + "is what keeps the displayed figure from over-reporting.");
    }

    [Fact]
    public async Task UiTransferListReadsTheSameCreditWhicheverOrderTheColoringRowsWereWritten()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient-forward", status: 3,
            (1, 1, "{\"Fungible\":11}"),
            (2, 1, "{\"InflationRight\":9}"),
            (3, 1, "\"NonFungible\""),
            (4, 1, "{\"Fungible\":22}"));
        await InsertWithColorings(2, "asset", "recipient-reverse", status: 3,
            (1, 1, "{\"Fungible\":22}"),
            (2, 1, "\"NonFungible\""),
            (3, 1, "{\"InflationRight\":9}"),
            (4, 1, "{\"Fungible\":11}"));

        var transfers = await RgbLibService.QueryRecentTransfersAsync(DbPath);

        Assert.Equal(2, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(33, t.Amount));
    }

    [Fact]
    public async Task UiTransferListCreditsNoInputOrChangeColoringToAnIncomingTransfer()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":10}"),
            (2, 1, "{\"Fungible\":20}"),
            (3, 3, "{\"Fungible\":999999}"),
            (4, 4, "{\"Fungible\":888888}"));

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        Assert.Equal(30, transfer.Amount);
    }

    [Fact]
    public async Task UiTransferListDropsAssignmentsBeyondItsWorkBoundRatherThanCreditingOrWrappingThem()
    {
        await CreateSchema();
        var overBound = Enumerable
            .Range(1, RgbLibService.MaxCreditedAssignmentsPerAssetTransfer + 1)
            .Select(i => (TxoIdx: i, Type: 1, Assignment: $"{{\"Fungible\":{i}}}"))
            .ToArray();
        await InsertWithColorings(1, "asset", "recipient", status: 3, overBound);

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        var boundedTotal = (long)RgbLibService.MaxCreditedAssignmentsPerAssetTransfer
            * (RgbLibService.MaxCreditedAssignmentsPerAssetTransfer + 1) / 2;
        Assert.Equal(boundedTotal, transfer.Amount);
    }

    [Fact]
    public async Task UiTransferListStillReadsAnOutgoingTransferFromItsRequestedAssignment()
    {
        await CreateSchema();
        await InsertOutgoing(1, "asset", "{\"Fungible\":250}");

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        Assert.Equal(250, transfer.Amount);
        Assert.Equal(3, transfer.Kind);
    }

    [Fact]
    public async Task UiTransferListTreatsANonFungibleOutgoingAssignmentAsZeroRatherThanFailingThePage()
    {
        await CreateSchema();
        await InsertOutgoing(1, "asset", "\"NonFungible\"");
        await InsertOutgoing(2, "asset", "{\"InflationRight\":5000}");
        await InsertOutgoing(3, "asset", "not json at all");
        await InsertOutgoing(4, "asset", null);

        var transfers = await RgbLibService.QueryRecentTransfersAsync(DbPath);

        Assert.Equal(4, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(0, t.Amount));
    }

    [Fact]
    public async Task UiTransferListSaturatesAnOutgoingAssignmentBeyondSignedRangeInsteadOfOverReporting()
    {
        await CreateSchema();
        await InsertOutgoing(1, "asset", "{\"Fungible\":18446744073709551615}");

        var transfer = Assert.Single(await RgbLibService.QueryRecentTransfersAsync(DbPath));

        Assert.True(transfer.Amount == long.MaxValue,
            $"a u64 assignment past the signed range read as {transfer.Amount}. json_extract hands a "
            + "value that does not fit an i64 back as a float, so reading the column as an integer "
            + "either throws and takes out the whole Transfers page or lands on a rounded figure; "
            + "clamping down to long.MaxValue never over-reports.");
    }

    [Fact]
    public async Task UiTransferListReadsAnIncomingAndAnOutgoingRowInTheSameResultSet()
    {
        await CreateSchema();
        await InsertWithColorings(1, "asset", "recipient", status: 3,
            (1, 1, "{\"Fungible\":40}"),
            (2, 1, "{\"Fungible\":2}"));
        await InsertOutgoing(2, "asset", "{\"Fungible\":7}");

        var transfers = await RgbLibService.QueryRecentTransfersAsync(DbPath);

        Assert.Equal(2, transfers.Count);
        Assert.Equal(7, transfers.Single(t => t.Idx == 2).Amount);
        Assert.Equal(42, transfers.Single(t => t.Idx == 1).Amount);
    }

    async Task InsertOutgoing(int idx, string assetId, string? requestedAssignment)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO asset(id,ticker,name,precision,initial_supply)
                VALUES(@asset,'TOK','Token',2,'1000');
            INSERT INTO batch_transfer(idx,status,txid) VALUES(@idx,3,@txid);
            INSERT INTO asset_transfer(idx,batch_transfer_idx,asset_id) VALUES(@idx,@idx,@asset);
            INSERT INTO transfer(idx,asset_transfer_idx,incoming,recipient_type,recipient_id,requested_assignment)
                VALUES(@idx,@idx,0,NULL,NULL,@requested);
            """;
        command.Parameters.AddWithValue("@asset", assetId);
        command.Parameters.AddWithValue("@idx", idx);
        command.Parameters.AddWithValue("@txid", idx.ToString("x64"));
        command.Parameters.AddWithValue("@requested", (object?)requestedAssignment ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    async Task InsertWithColorings(
        int idx, string assetId, string recipientId, int status,
        params (int TxoIdx, int Type, string Assignment)[] colorings)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var rows = string.Join("", colorings.Select((_, i) =>
            $"INSERT INTO coloring(txo_idx,asset_transfer_idx,type,assignment)"
            + $" VALUES(@txo{i},@idx,@type{i},@assignment{i});"));
        command.CommandText = """
            INSERT OR IGNORE INTO asset(id,ticker,name,precision,initial_supply)
                VALUES(@asset,'TOK','Token',2,'1000');
            INSERT INTO batch_transfer(idx,status,txid) VALUES(@idx,@status,@txid);
            INSERT INTO asset_transfer(idx,batch_transfer_idx,asset_id) VALUES(@idx,@idx,@asset);
            INSERT INTO transfer(idx,asset_transfer_idx,incoming,recipient_type,recipient_id)
                VALUES(@idx,@idx,1,'"Blind"',@recipient);
            """ + rows;
        command.Parameters.AddWithValue("@asset", assetId);
        command.Parameters.AddWithValue("@recipient", recipientId);
        command.Parameters.AddWithValue("@idx", idx);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@txid", idx.ToString("x64"));
        for (var i = 0; i < colorings.Length; i++)
        {
            command.Parameters.AddWithValue($"@txo{i}", colorings[i].TxoIdx);
            command.Parameters.AddWithValue($"@type{i}", colorings[i].Type);
            command.Parameters.AddWithValue($"@assignment{i}", colorings[i].Assignment);
        }
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    async Task CreateSchema()
    {
        Directory.CreateDirectory(_dir);
        await Execute("""
            CREATE TABLE asset (
                id TEXT PRIMARY KEY, ticker TEXT, name TEXT NOT NULL,
                precision INTEGER NOT NULL, initial_supply TEXT NOT NULL);
            CREATE TABLE batch_transfer (
                idx INTEGER PRIMARY KEY, status INTEGER NOT NULL, txid TEXT);
            CREATE TABLE asset_transfer (
                idx INTEGER PRIMARY KEY, batch_transfer_idx INTEGER NOT NULL, asset_id TEXT);
            CREATE TABLE transfer (
                idx INTEGER PRIMARY KEY, asset_transfer_idx INTEGER NOT NULL,
                incoming INTEGER NOT NULL, recipient_type TEXT, recipient_id TEXT,
                requested_assignment TEXT);
            CREATE TABLE coloring (
                idx INTEGER PRIMARY KEY, txo_idx INTEGER NOT NULL,
                asset_transfer_idx INTEGER NOT NULL,
                type INTEGER NOT NULL, assignment TEXT);
            """);
    }

    async Task Insert(int idx, string assetId, string recipientId, int status, long amount)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO asset(id,ticker,name,precision,initial_supply)
                VALUES(@asset,'TOK','Token',2,'1000');
            INSERT INTO batch_transfer(idx,status,txid) VALUES(@idx,@status,@txid);
            INSERT INTO asset_transfer(idx,batch_transfer_idx,asset_id) VALUES(@idx,@idx,@asset);
            INSERT INTO transfer(idx,asset_transfer_idx,incoming,recipient_type,recipient_id)
                VALUES(@idx,@idx,1,'"Blind"',@recipient);
            INSERT INTO coloring(idx,txo_idx,asset_transfer_idx,type,assignment)
                VALUES(@idx,1,@idx,1,@assignment);
            """;
        command.Parameters.AddWithValue("@asset", assetId);
        command.Parameters.AddWithValue("@recipient", recipientId);
        command.Parameters.AddWithValue("@idx", idx);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@txid", idx.ToString("x64"));
        command.Parameters.AddWithValue("@assignment", $"{{\"Fungible\":{amount}}}");
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    async Task InsertHistory(int count)
    {
        await Execute($$"""
            INSERT INTO asset(id,ticker,name,precision,initial_supply)
                VALUES('history','TOK','Token',2,'1000');
            WITH RECURSIVE seq(i) AS (
                SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {{count}}
            ) INSERT INTO batch_transfer(idx,status,txid)
                SELECT i,3,printf('%064x',i) FROM seq;
            WITH RECURSIVE seq(i) AS (
                SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {{count}}
            ) INSERT INTO asset_transfer(idx,batch_transfer_idx,asset_id)
                SELECT i,i,'history' FROM seq;
            WITH RECURSIVE seq(i) AS (
                SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {{count}}
            ) INSERT INTO transfer(idx,asset_transfer_idx,incoming,recipient_type,recipient_id)
                SELECT i,i,1,'"Blind"','recipient-' || i FROM seq;
            WITH RECURSIVE seq(i) AS (
                SELECT 1 UNION ALL SELECT i + 1 FROM seq WHERE i < {{count}}
            ) INSERT INTO coloring(idx,txo_idx,asset_transfer_idx,type,assignment)
                SELECT i,1,i,1,'{"Fungible":1}' FROM seq;
            """);
    }

    async Task Execute(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { }
    }
}
