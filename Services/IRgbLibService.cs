namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRgbLibService : IDisposable
{
    Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default);
    bool UnloadWallet(string walletId);
    string GetWalletDataDir(string walletId, string walletNetwork);
    
    Task<string> GetAddressAsync(string walletId, CancellationToken ct = default);
    Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false);
    Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default);
    
    Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, int minConfirmations = 1, CancellationToken ct = default);
    
    Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default);
    Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default);
    Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate, CancellationToken ct = default);
    Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default);
    
    Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default);
    Task<List<RgbMatchedTransfer>> ListIncomingTransfersForRecipientsAsync(
        string walletId, IReadOnlyCollection<string> recipientIds, string? assetId = null,
        CancellationToken ct = default);
    Task RefreshAsync(string walletId, CancellationToken ct = default);
    Task<string> SnapshotStockAsync(string walletId, CancellationToken ct = default);
    Task<RgbVerificationSnapshot> SnapshotVerificationStateAsync(string walletId, CancellationToken ct = default);

    Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default);

    Task<string> SendBeginAsync(string walletId, string recipientMapJson, float feeRate, int minConfirmations = 1, CancellationToken ct = default);
    Task<string> SendEndAsync(string walletId, string signedPsbt, CancellationToken ct = default);
    Task<string> CreateConsignmentsAsync(string walletId, string psbt, CancellationToken ct = default);
    Task FailTransfersAsync(string walletId, int batchTransferIdx, bool noAssetOnly, bool skipSync, CancellationToken ct = default);
    RgbInvoiceData DecodeInvoice(string invoiceString);

    Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default);

    RgbKeys GenerateKeys(string network);
    RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network);
}

public class RgbKeys
{
    public string Mnemonic { get; set; } = "";
    public string Xpub { get; set; } = "";
    public string AccountXpubVanilla { get; set; } = "";
    public string AccountXpubColored { get; set; } = "";
    public string MasterFingerprint { get; set; } = "";
}
