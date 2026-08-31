using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IBitcoinChainClient : IDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default);
    Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default);

    Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
        Script script, CancellationToken ct = default);

    async Task<IReadOnlyList<Outpoint>> ListUnspentByScriptAsync(
        Script script, CancellationToken ct = default)
    {
        var rows = await ListUnspentWithConfirmationByScriptAsync(script, ct);
        return rows.Select(r => r.Outpoint).ToList();
    }
}
