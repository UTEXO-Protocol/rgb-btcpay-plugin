using BTCPayServer.Plugins.RgbUtexo.Data.Entities;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRGBWalletService
{
    Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default);
    Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default);
    Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default);
    Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default);
    Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default);
    Task<string> GetAddressAsync(string walletId, CancellationToken ct = default);
    Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false);
    Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(string walletId, CancellationToken ct = default);
    Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default);
    Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default);
    Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default);
    Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default);
    Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default);
    Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default);
    Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default);
    Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default);
    Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default);
    Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default);
    Task DeleteWalletAsync(string walletId, CancellationToken ct = default);
    Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default);
    Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default);
}
