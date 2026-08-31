using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;

public sealed class FakeRGBWalletService : IRGBWalletService
{
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

    static InvalidOperationException Reg() =>
        new("regression: consent gate was bypassed and a wallet-service method was called");

    public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default) =>
        Task.FromResult<RGBWallet?>(null);

    public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw Reg();

    public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Reg();
    public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Reg();
    public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw Reg();
    public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw Reg();
    public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw Reg();
    public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw Reg();
    public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw Reg();
    public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw Reg();
    public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Reg();
    public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw Reg();
    public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw Reg();
    public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw Reg();
}
