using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class EsploraHttpClient : IBitcoinChainClient
{
    const int MaxResponseBytes = 10 * 1024 * 1024;
    static readonly HttpClient _shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly Regex _txidShape = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    readonly HttpClient _http;
    readonly string _baseUrl;

    public EsploraHttpClient(string baseUrl, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("Esplora baseUrl is empty");
        Uri uri;
        try { uri = new Uri(baseUrl); }
        catch (UriFormatException) { throw new InvalidOperationException($"Malformed Esplora URL '{baseUrl}'"); }
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Esplora URL scheme '{uri.Scheme}' is not allowed. Use http:// or https://.");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"Esplora URL must not contain query string or fragment: '{baseUrl}'");

        var normalized = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
        _baseUrl = normalized.TrimEnd('/');
        _http = http ?? _shared;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/blocks/tip/height",
            HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureContentLengthWithinLimit(resp);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default)
    {
        if (!_txidShape.IsMatch(txid))
            throw new InvalidOperationException($"Invalid txid '{txid}': expected 64-char hex");
        using var resp = await _http.GetAsync($"{_baseUrl}/tx/{txid}/hex",
            HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureContentLengthWithinLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await ReadCappedAsync(resp, ct);
            throw new InvalidOperationException($"Esplora raw-tx fetch failed ({(int)resp.StatusCode}): {detail}");
        }
        return (await ReadCappedAsync(resp, ct)).Trim();
    }

    public async Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default)
    {
        using var body = new StringContent(rawTxHex, Encoding.ASCII, "text/plain");
        using var resp = await _http.PostAsync($"{_baseUrl}/tx", body, ct);
        EnsureContentLengthWithinLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await ReadCappedAsync(resp, ct);
            throw new InvalidOperationException($"Esplora broadcast failed ({(int)resp.StatusCode}): {detail}");
        }
        var txid = (await ReadCappedAsync(resp, ct)).Trim();
        if (!_txidShape.IsMatch(txid))
            throw new InvalidOperationException($"Esplora broadcast returned malformed txid '{txid}'");
        return txid;
    }

    public async Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
        Script script, CancellationToken ct = default)
    {
        var scriptHash = EsploraScriptHash(script);
        using var resp = await _http.GetAsync($"{_baseUrl}/scripthash/{scriptHash}/utxo",
            HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureContentLengthWithinLimit(resp);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await ReadCappedAsync(resp, ct);
            throw new InvalidOperationException($"Esplora utxo fetch failed ({(int)resp.StatusCode}): {detail}");
        }

        var body = await ReadCappedAsync(resp, ct);
        using var doc = JsonDocument.Parse(body);
        return ReadUnspentRows(doc.RootElement);
    }

    internal static IReadOnlyList<UnspentWithConfirmation> ReadUnspentRows(JsonElement root)
    {
        var rows = new List<UnspentWithConfirmation>();
        foreach (var item in root.EnumerateArray())
        {
            var confirmed = item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("confirmed", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            rows.Add(new UnspentWithConfirmation(
                new Outpoint(
                    item.GetProperty("txid").GetString()!,
                    item.GetProperty("vout").GetInt32()),
                confirmed));
        }
        return rows;
    }

    internal static string EsploraScriptHash(Script script) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(script.ToBytes())).ToLowerInvariant();

    static void EnsureContentLengthWithinLimit(HttpResponseMessage resp)
    {
        if (resp.Content.Headers.ContentLength is long cl && cl > MaxResponseBytes)
            throw new InvalidOperationException(
                $"Esplora response exceeds {MaxResponseBytes} bytes (Content-Length: {cl})");
    }

    static async Task<string> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + read > MaxResponseBytes)
                throw new InvalidOperationException(
                    $"Esplora response exceeds {MaxResponseBytes} bytes (stream)");
            ms.Write(buf, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() { }
}
