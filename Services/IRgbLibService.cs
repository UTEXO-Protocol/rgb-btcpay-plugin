namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRgbLibService : IDisposable
{
    Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default);
    void UnloadWallet(string walletId);
    
    Task<string> GetAddressAsync(string walletId, CancellationToken ct = default);
    Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default);
    Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default);
    
    Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, CancellationToken ct = default);
    
    Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default);
    Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default);
    Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default);
    
    Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default);
    Task RefreshAsync(string walletId, CancellationToken ct = default);
    
    Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default);
    
    RgbKeys GenerateKeys(string network);
}

public class RgbKeys
{
    public string Mnemonic { get; set; } = "";
    public string Xpub { get; set; } = "";
    public string AccountXpubVanilla { get; set; } = "";
    public string AccountXpubColored { get; set; } = "";
    public string MasterFingerprint { get; set; } = "";
}
