using System.Text.Json;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public record BtcBalance(BalanceInfo Vanilla, BalanceInfo Colored);

public class BalanceInfo
{
    [JsonPropertyName("settled")] public long Settled { get; set; }
    [JsonPropertyName("future")] public long Future { get; set; }
    [JsonPropertyName("spendable")] public long Spendable { get; set; }
}

public record UnspentOutput(UtxoInfo Utxo, List<RgbAllocation> RgbAllocations);

public class UtxoInfo
{
    [JsonPropertyName("outpoint")] public Outpoint Outpoint { get; set; } = null!;
    [JsonPropertyName("btc_amount")] public long BtcAmount { get; set; }
    [JsonPropertyName("colorable")] public bool Colorable { get; set; }
}

public record Outpoint(string Txid, int Vout);

public sealed record UnspentWithConfirmation(Outpoint Outpoint, bool ConfirmedInABlock);

public class RgbAllocation
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    public ulong Amount { get; set; }
    [JsonPropertyName("settled")] public bool Settled { get; set; }
}

public static class RgbAssignmentJson
{
    public static ulong FungibleValueOrZeroForEveryOtherVariant(JsonElement assignment)
    {
        if (assignment.ValueKind != JsonValueKind.Object) return 0;
        if (!assignment.TryGetProperty("Fungible", out var fungible)) return 0;
        if (fungible.ValueKind != JsonValueKind.Number) return 0;
        return fungible.TryGetUInt64(out var value) ? value : 0;
    }

    public static ulong SumFungibleSaturatingRatherThanWrapping(string? assignmentArrayJson)
    {
        if (string.IsNullOrWhiteSpace(assignmentArrayJson)) return 0;
        JsonDocument document;
        try { document = JsonDocument.Parse(assignmentArrayJson); }
        catch (JsonException) { return 0; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return 0;
            var total = 0UL;
            foreach (var assignment in document.RootElement.EnumerateArray())
            {
                var value = FungibleValueOrZeroForEveryOtherVariant(assignment);
                total = value > ulong.MaxValue - total ? ulong.MaxValue : total + value;
            }
            return total;
        }
    }

    public static long ToSignedByUnderReportingNeverOverReporting(ulong total)
        => total > long.MaxValue ? long.MaxValue : (long)total;
}

public class RgbAsset
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("issued_supply")] public ulong IssuedSupply { get; set; }
    public ulong Balance { get; set; }
    public ulong FutureBalance { get; set; }
    public ulong SpendableBalance { get; set; }
}

public class InvoiceResponse
{
    [JsonPropertyName("invoice")] public string Invoice { get; set; } = "";
    [JsonPropertyName("recipient_id")] public string RecipientId { get; set; } = "";
    [JsonPropertyName("expiration_timestamp")] public long? ExpirationTimestamp { get; set; }
    [JsonPropertyName("batch_transfer_idx")] public int? BatchTransferIdx { get; set; }
}

public class RgbTransfer
{
    [JsonPropertyName("idx")] public int Idx { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("txid")] public string? Txid { get; set; }
    [JsonPropertyName("recipient_id")] public string? RecipientId { get; set; }
    [JsonPropertyName("receive_utxo")] public Outpoint? ReceiveUtxo { get; set; }
    public string? AssetId { get; set; }
    public string AssetTicker { get; set; } = "";
}

public sealed record RgbMatchedTransfer(string AssetId, RgbAsset Asset, RgbTransfer Transfer);

public class BtcTransaction
{
    [JsonPropertyName("txid")] public string Txid { get; set; } = "";
    [JsonPropertyName("transaction_type")] public JsonElement TransactionType { get; set; }
    [JsonPropertyName("received")] public long Received { get; set; }
    [JsonPropertyName("sent")] public long Sent { get; set; }
    [JsonPropertyName("fee")] public long Fee { get; set; }
    [JsonPropertyName("confirmation_time")] public BtcTxConfirmationTime? ConfirmationTime { get; set; }

    public int GetTransactionTypeInt() => TransactionType.ValueKind switch
    {
        JsonValueKind.Number => TransactionType.GetInt32(),
        JsonValueKind.String => ParseTxType(TransactionType.GetString()),
        _ => -1
    };

    static int ParseTxType(string? s) => s?.ToLowerInvariant() switch
    {
        "user" => 0,
        "createutxos" or "create_utxos" => 1,
        "rgbsend" or "rgb_send" => 2,
        "drain" => 3,
        "incoming" => 4,
        "sendbtc" or "send_btc" => 5,
        _ => int.TryParse(s, out var n) ? n : -1
    };
}

public class BtcTxConfirmationTime
{
    [JsonPropertyName("height")] public long Height { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
}

public class RgbInvoiceData
{
    [JsonPropertyName("recipient_id")] public string RecipientId { get; set; } = "";
    [JsonPropertyName("asset_id")] public string? AssetId { get; set; }
    [JsonPropertyName("amount")] public long? Amount { get; set; }
    [JsonPropertyName("network")] public string Network { get; set; } = "";
    [JsonPropertyName("expiration_timestamp")] public long ExpirationTimestamp { get; set; }
    [JsonPropertyName("transport_endpoints")] public List<string> TransportEndpoints { get; set; } = [];
}
