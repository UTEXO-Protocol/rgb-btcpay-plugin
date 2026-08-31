namespace BTCPayServer.Plugins.RgbUtexo.Services;

public enum RgbVanillaReservationState
{
    Clean,
    LiveAndConstraining,
    InertAlreadyRecovered,
    Unknown
}

public sealed record RgbVanillaReservedOutpoint(string Txid, int Vout, string? ReservedForTxid);

public sealed record RgbVanillaReservationReport(
    RgbVanillaReservationState State,
    IReadOnlyList<RgbVanillaReservedOutpoint> Reserved,
    IReadOnlyList<RgbVanillaReservedOutpoint> StillUnspent);

public static class RgbVanillaReservationInspector
{
    internal const string ReservedTxoTable = "reserved_txo";
    internal const string WalletTransactionTable = "wallet_transaction";

    public static readonly RgbVanillaReservationReport Clean =
        new(RgbVanillaReservationState.Clean, [], []);

    public static async Task<IReadOnlyList<RgbVanillaReservedOutpoint>> ReadReservedOutpointsAsync(
        string dbPath, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath)) return [];

        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
        };
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);

        if (!await TablesPresentAsync(conn, ct)) return [];

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT rt.txid, rt.vout, wt.txid
            FROM {ReservedTxoTable} AS rt
            LEFT JOIN {WalletTransactionTable} AS wt ON wt.idx = rt.reserved_for
            ORDER BY rt.txid, rt.vout
            """;
        var reserved = new List<RgbVanillaReservedOutpoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            reserved.Add(new RgbVanillaReservedOutpoint(
                reader.GetString(0),
                (int)reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return reserved;
    }

    static async Task<bool> TablesPresentAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ($reserved, $wallet)";
        cmd.Parameters.AddWithValue("$reserved", ReservedTxoTable);
        cmd.Parameters.AddWithValue("$wallet", WalletTransactionTable);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 2;
    }

    public static RgbVanillaReservationReport Classify(
        IReadOnlyList<RgbVanillaReservedOutpoint> reserved,
        IReadOnlyCollection<Outpoint>? bdkUnspentOutpoints)
    {
        if (reserved.Count == 0) return Clean;
        if (bdkUnspentOutpoints == null)
            return new RgbVanillaReservationReport(RgbVanillaReservationState.Unknown, reserved, []);

        var unspent = new HashSet<(string, int)>(
            bdkUnspentOutpoints.Select(o => (o.Txid, o.Vout)));
        var stillUnspent = reserved
            .Where(r => unspent.Contains((r.Txid, r.Vout)))
            .ToList();

        return new RgbVanillaReservationReport(
            stillUnspent.Count > 0
                ? RgbVanillaReservationState.LiveAndConstraining
                : RgbVanillaReservationState.InertAlreadyRecovered,
            reserved,
            stillUnspent);
    }
}
