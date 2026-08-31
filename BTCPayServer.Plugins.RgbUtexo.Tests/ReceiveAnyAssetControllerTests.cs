using BTCPayServer;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ReceiveAnyAssetControllerTests
{
    class RecordingWalletService : IRGBWalletService
    {
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

        public Func<string, Task<RGBWallet?>>? GetWalletForStoreImpl;
        public Func<string, string?, long?, TimeSpan?, string?, int, CancellationToken, Task<RGBInvoice>>? CreateInvoiceImpl;

        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
            => GetWalletForStoreImpl?.Invoke(storeId) ?? Task.FromResult<RGBWallet?>(null);

        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default)
            => CreateInvoiceImpl?.Invoke(walletId, assetId, amount, expiration, btcPayInvoiceId, minConfirmations, ct)
               ?? throw new NotImplementedException();

        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController BuildController(RecordingWalletService wallets)
    {
        var controller = new RGBController(
            wallets: wallets,
            stores: null!,
            handlers: null!,
            db: null!,
            log: NullLogger<RGBController>.Instance,
            userManager: null!,
            events: null!,
            cache: null!,
            btcPayOptions: Options.Create(new BTCPayServerOptions()),
            rateSource: null!,
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-controller-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    [Fact]
    public async Task Post_redirects_to_setup_when_no_wallet()
    {
        var wallets = new RecordingWalletService();
        var controller = BuildController(wallets);

        var result = await controller.CreateReceiveAnyAsset("store-1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Setup", redirect.ActionName);
    }

    [Fact]
    public async Task Post_calls_CreateInvoiceAsync_with_null_asset_and_null_amount_and_two_hour_expiration()
    {
        var wallet = new RGBWallet { Id = "w1", StoreId = "store-1", Network = "regtest" };
        string? capturedAssetId = "INITIAL";
        long? capturedAmount = -1;
        TimeSpan? capturedExpiration = null;
        string? capturedBtcPayInvoiceId = "INITIAL";

        var wallets = new RecordingWalletService
        {
            GetWalletForStoreImpl = _ => Task.FromResult<RGBWallet?>(wallet),
            CreateInvoiceImpl = (wid, assetId, amount, expiration, btcPay, minConf, ct) =>
            {
                capturedAssetId = assetId;
                capturedAmount = amount;
                capturedExpiration = expiration;
                capturedBtcPayInvoiceId = btcPay;
                return Task.FromResult(new RGBInvoice { Id = "inv-1", WalletId = wid, Invoice = "rgb:test", RecipientId = "utxob:x" });
            }
        };
        var controller = BuildController(wallets);

        await controller.CreateReceiveAnyAsset("store-1");

        Assert.Null(capturedAssetId);
        Assert.Null(capturedAmount);
        Assert.Null(capturedBtcPayInvoiceId);
        Assert.Equal(TimeSpan.FromHours(2), capturedExpiration);
    }

    [Fact]
    public async Task Post_redirects_to_GET_on_success()
    {
        var wallet = new RGBWallet { Id = "w1", StoreId = "store-1", Network = "regtest" };
        var wallets = new RecordingWalletService
        {
            GetWalletForStoreImpl = _ => Task.FromResult<RGBWallet?>(wallet),
            CreateInvoiceImpl = (wid, _, _, _, _, _, _) =>
                Task.FromResult(new RGBInvoice { Id = "inv-42", WalletId = wid, Invoice = "rgb:test", RecipientId = "utxob:x" })
        };
        var controller = BuildController(wallets);

        var result = await controller.CreateReceiveAnyAsset("store-1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RGBController.ReceiveAnyAsset), redirect.ActionName);
        Assert.Equal("inv-42", redirect.RouteValues!["rgbInvoiceId"]);
    }

    [Fact]
    public async Task Post_sets_ErrorMessage_and_redirects_to_Index_on_throw()
    {
        var wallet = new RGBWallet { Id = "w1", StoreId = "store-1", Network = "regtest" };
        var wallets = new RecordingWalletService
        {
            GetWalletForStoreImpl = _ => Task.FromResult<RGBWallet?>(wallet),
            CreateInvoiceImpl = (_, _, _, _, _, _, _) => throw new InvalidOperationException("no slots")
        };
        var controller = BuildController(wallets);

        var result = await controller.CreateReceiveAnyAsset("store-1");

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RGBController.Index), redirect.ActionName);
        Assert.Equal("no slots", controller.TempData["ErrorMessage"]);
    }
}
