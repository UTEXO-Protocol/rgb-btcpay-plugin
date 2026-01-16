using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RGB.Services;

public class RgbNodeClient
{
    readonly HttpClient _http;
    readonly Uri _baseUri;
    readonly ILogger<RgbNodeClient> _log;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public RgbNodeClient(IHttpClientFactory factory, RGBConfiguration cfg, ILogger<RgbNodeClient> log)
    {
        _log = log;
        _http = factory.CreateClient("RgbNode");
        _baseUri = new Uri(cfg.RgbNodeUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public Task<RegisterResponse> RegisterAsync(RGBWalletCredentials c) => PostAsync<RegisterResponse>("/wallet/register", c);
    public async Task<string> GetAddressAsync(RGBWalletCredentials c) => (await PostRawAsync("/wallet/address", c)).Trim('"');
    public Task<BtcBalance> GetBtcBalanceAsync(RGBWalletCredentials c) => PostAsync<BtcBalance>("/wallet/btcbalance", c);
    public Task RefreshAsync(RGBWalletCredentials c) => PostAsync("/wallet/refresh", c);
    public Task SyncAsync(RGBWalletCredentials c) => PostAsync("/wallet/sync", c);
    public Task<GenerateKeysResponse> GenerateKeysAsync() => PostAsync<GenerateKeysResponse>("/wallet/generate_keys", creds: null, body: null);

    public Task<List<UnspentOutput>> ListUnspentsAsync(RGBWalletCredentials c) =>
        PostAsync<List<UnspentOutput>>("/wallet/listunspents", c) ?? Task.FromResult(new List<UnspentOutput>());

    public Task<string> CreateUtxosBeginAsync(RGBWalletCredentials c, int num = 5, int size = 10000, int feeRate = 2) =>
        PostRawAsync("/wallet/createutxosbegin", c, new { up_to = true, num, size, fee_rate = feeRate });

    public Task<string> CreateUtxosEndAsync(RGBWalletCredentials c, string signedPsbt) =>
        PostRawAsync("/wallet/createutxosend", c, new { signed_psbt = signedPsbt.Trim('"') });

    public async Task<List<RgbAsset>> ListAssetsAsync(RGBWalletCredentials c)
    {
        var r = await PostAsync<ListAssetsResponse>("/wallet/listassets", c);
        return r?.Nia ?? [];
    }

    public Task<AssetBalance> GetAssetBalanceAsync(RGBWalletCredentials creds, string assetId) =>
        PostAsync<AssetBalance>("/wallet/assetbalance", creds, new { asset_id = assetId });

    public Task<RgbAsset> IssueAssetNiaAsync(RGBWalletCredentials c, string ticker, string name, List<long> amounts, int prec = 0)
    {
        _log.LogInformation("Issuing asset {Ticker}", ticker);
        return PostAsync<RgbAsset>("/wallet/issueassetnia", c, new { ticker, name, amounts, precision = prec });
    }

    public Task<InvoiceResponse> BlindReceiveAsync(RGBWalletCredentials c, string? assetId = null, long? amount = null, long? expTs = null)
    {
        return PostAsync<InvoiceResponse>("/wallet/blindreceive", c, 
            MakeBody(("asset_id", assetId), ("amount", amount), ("expiration_timestamp", expTs)));
    }

    public Task<InvoiceResponse> WitnessReceiveAsync(RGBWalletCredentials c, string? assetId = null, long? amt = null, long? exp = null) =>
        PostAsync<InvoiceResponse>("/wallet/witnessreceive", c, MakeBody(("asset_id", assetId), ("amount", amt), ("expiration_timestamp", exp)));

    public Task<DecodedInvoice> DecodeRgbInvoiceAsync(string inv) =>
        PostAsync<DecodedInvoice>("/wallet/decodergbinvoice", creds: null, body: new { invoice = inv });

    public async Task<List<RgbTransfer>> ListTransfersAsync(RGBWalletCredentials c, string? assetId = null)
    {
        object? body = assetId != null ? new { asset_id = assetId } : null;
        return await PostAsync<List<RgbTransfer>>("/wallet/listtransfers", c, body) ?? [];
    }

    public Task FailTransfersAsync(RGBWalletCredentials c, bool noAssetOnly = true) =>
        PostAsync("/wallet/failtransfers", c, new { no_asset_only = noAssetOnly });

    public Task<string> SendBeginAsync(RGBWalletCredentials c, string invoice, string assetId, long amount, int feeRate = 5) =>
        PostRawAsync("/wallet/sendbegin", c, new { invoice, asset_id = assetId, amount, fee_rate = feeRate });

    public Task<SendEndResponse> SendEndAsync(RGBWalletCredentials c, string signedPsbt) =>
        PostAsync<SendEndResponse>("/wallet/sendend", c, new { signed_psbt = signedPsbt });

    public Task<string> SendBtcBeginAsync(RGBWalletCredentials c, string addr, long amount, int feeRate = 2) =>
        PostRawAsync("/wallet/sendbtcbegin", c, new { address = addr, amount, fee_rate = feeRate });

    public async Task<string> SendBtcEndAsync(RGBWalletCredentials c, string signedPsbt)
    {
        var r = await PostAsync<SendBtcEndResponse>("/wallet/sendbtcend", c, new { signed_psbt = signedPsbt });
        return r?.Txid ?? throw new RgbNodeException("sendbtcend did not return txid");
    }

    public Task<BackupResponse> BackupAsync(RGBWalletCredentials c, string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Backup password is required for security", nameof(password));
        
        return PostAsync<BackupResponse>("/wallet/backup", c, new { password });
    }

    HttpRequestMessage CreateRequest(string path, RGBWalletCredentials? creds, object? body)
    {
        var uri = new Uri(_baseUri, path.TrimStart('/'));
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        
        if (creds != null)
        {
            request.Headers.Add("xpub-van", creds.XpubVanilla);
            request.Headers.Add("xpub-col", creds.XpubColored);
            request.Headers.Add("master-fingerprint", creds.MasterFingerprint);
        }
        
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        
        return request;
    }

    async Task<T> PostAsync<T>(string path, RGBWalletCredentials? creds = null, object? body = null)
    {
        using var request = CreateRequest(path, creds, body);
        using var resp = await _http.SendAsync(request);
        await ThrowIfBad(resp, path);
        return await resp.Content.ReadFromJsonAsync<T>() ?? throw new RgbNodeException($"Null response from {path}");
    }

    async Task PostAsync(string path, RGBWalletCredentials? creds = null, object? body = null)
    {
        using var request = CreateRequest(path, creds, body);
        using var resp = await _http.SendAsync(request);
        await ThrowIfBad(resp, path);
    }

    async Task<string> PostRawAsync(string path, RGBWalletCredentials? creds = null, object? body = null)
    {
        using var request = CreateRequest(path, creds, body);
        using var resp = await _http.SendAsync(request);
        await ThrowIfBad(resp, path);
        return await resp.Content.ReadAsStringAsync();
    }

    async Task ThrowIfBad(HttpResponseMessage resp, string op)
    {
        if (resp.IsSuccessStatusCode) return;
        var err = await resp.Content.ReadAsStringAsync();
        _log.LogError("RGB node {Op} failed ({Code}): {Err}", op, (int)resp.StatusCode, err);
        throw new RgbNodeException($"{op}: {err}");
    }

    static object MakeBody(params (string k, object? v)[] fields)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in fields) if (v != null) d[k] = v;
        return d.Count > 0 ? d : new { };
    }
}

public class RgbNodeException(string msg) : Exception(msg);

public record RegisterResponse(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("btc_balance")] BtcBalance? BtcBalance);

public record BtcBalance(
    [property: JsonPropertyName("vanilla")] BalanceInfo Vanilla,
    [property: JsonPropertyName("colored")] BalanceInfo Colored);

public class BalanceInfo
{
    [JsonPropertyName("settled")] public long Settled { get; set; }
    [JsonPropertyName("future")] public long Future { get; set; }
    [JsonPropertyName("spendable")] public long Spendable { get; set; }
}

public record UnspentOutput(
    [property: JsonPropertyName("utxo")] UtxoInfo Utxo,
    [property: JsonPropertyName("rgb_allocations")] List<RgbAllocation> RgbAllocations);

public class UtxoInfo
{
    [JsonPropertyName("outpoint")] public Outpoint Outpoint { get; set; } = null!;
    [JsonPropertyName("btc_amount")] public long BtcAmount { get; set; }
    [JsonPropertyName("colorable")] public bool Colorable { get; set; }
}

public record Outpoint([property: JsonPropertyName("txid")] string Txid, [property: JsonPropertyName("vout")] int Vout);

public class RgbAllocation
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("settled")] public bool Settled { get; set; }
}

public class ListAssetsResponse
{
    [JsonPropertyName("nia")] public List<RgbAsset> Nia { get; set; } = [];
    [JsonPropertyName("cfa")] public List<RgbAsset> Cfa { get; set; } = [];
}

public class RgbAsset
{
    [JsonPropertyName("asset_id")] public string AssetId { get; set; } = "";
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("precision")] public int Precision { get; set; }
    [JsonPropertyName("issued_supply")] public long IssuedSupply { get; set; }
}

public record AssetBalance(
    [property: JsonPropertyName("settled")] long Settled,
    [property: JsonPropertyName("future")] long Future,
    [property: JsonPropertyName("spendable")] long Spendable);

public class InvoiceResponse
{
    [JsonPropertyName("invoice")] public string Invoice { get; set; } = "";
    [JsonPropertyName("recipient_id")] public string RecipientId { get; set; } = "";
    [JsonPropertyName("expiration_timestamp")] public long? ExpirationTimestamp { get; set; }
    [JsonPropertyName("batch_transfer_idx")] public int? BatchTransferIdx { get; set; }
}

public record DecodedInvoice(
    [property: JsonPropertyName("recipient_id")] string RecipientId,
    [property: JsonPropertyName("asset_id")] string? AssetId,
    [property: JsonPropertyName("assignment")] InvoiceAssignment? Assignment,
    [property: JsonPropertyName("expiration_timestamp")] long? ExpirationTimestamp,
    [property: JsonPropertyName("network")] int Network);

public record InvoiceAssignment([property: JsonPropertyName("amount")] long? Amount);

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

public record SendEndResponse([property: JsonPropertyName("txid")] string Txid, [property: JsonPropertyName("batch_transfer_idx")] int BatchTransferIdx);
public record SendBtcEndResponse([property: JsonPropertyName("txid")] string Txid);

public class BackupResponse
{
    [JsonPropertyName("backup")] public string? Backup { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
}

public class GenerateKeysResponse
{
    [JsonPropertyName("mnemonic")] public string Mnemonic { get; set; } = "";
    [JsonPropertyName("xpub")] public string Xpub { get; set; } = "";
    [JsonPropertyName("account_xpub_vanilla")] public string AccountXpubVanilla { get; set; } = "";
    [JsonPropertyName("account_xpub_colored")] public string AccountXpubColored { get; set; } = "";
    [JsonPropertyName("master_fingerprint")] public string MasterFingerprint { get; set; } = "";
}
