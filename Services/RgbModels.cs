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

public class RgbAllocation
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("settled")] public bool Settled { get; set; }
}

public class RgbAsset
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("issued_supply")] public long IssuedSupply { get; set; }
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
}

