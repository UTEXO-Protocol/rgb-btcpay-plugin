using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSendAssetRefusalDisclosureTests
{
    const string SendFallback = "Failed to send asset. Check server logs for details.";

    sealed class RefusingWalletService(Exception refusal) : IRGBWalletService
    {
        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
            => Task.FromResult<RGBWallet?>(new RGBWallet
            {
                Id = "wallet",
                StoreId = storeId,
                Network = "regtest"
            });

        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker,
            string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice,
            string assetId, long amount, float feeRate, CancellationToken ct = default)
            => Task.FromException<(string, long, string, string, string?)>(refusal);

        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
            => Task.FromResult(new List<RgbAsset>());

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController BuildController(Exception refusal)
    {
        var controller = new RGBController(
            new RefusingWalletService(refusal), null!, null!, null!,
            NullLogger<RGBController>.Instance, null!, null!, null!,
            Options.Create(new BTCPayServerOptions()), null!,
            new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-send-refusal-tests")), null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static RGBSendAssetViewModel ValidModel() => new()
    {
        AssetId = "asset",
        RgbInvoice = "rgb:invoice",
        Amount = 1,
        FeeRate = 2
    };

    static async Task<string> ShownToTheStoreOwner(Exception refusal)
    {
        var controller = BuildController(refusal);

        var result = await controller.SendAsset("store", ValidModel());

        Assert.IsType<ViewResult>(result);
        return Assert.Single(controller.ModelState[""]!.Errors).ErrorMessage;
    }

    [Theory]
    [InlineData("Invalid RGB invoice")]
    [InlineData("Insufficient spendable assets")]
    [InlineData("list_unspents failed: Indexer error: connection refused")]
    public async Task RgbLibRefusalOnTheSendPath_ReachesTheStoreOwnerWordForWord_BecauseItIsHisOnlyDiagnosis(
        string nativeDetail)
    {
        var shown = await ShownToTheStoreOwner(new RgbLibException(nativeDetail));

        Assert.Equal(nativeDetail, shown);
        Assert.NotEqual(SendFallback, shown);
    }

    [Theory]
    [InlineData("Insufficient SETL spendable balance: have 3, need 9")]
    [InlineData("RGB invoice network 'testnet' does not match wallet network 'signet'.")]
    public async Task PluginAuthoredSendRefusal_KeepsReachingTheStoreOwner(string refusal)
    {
        Assert.Equal(refusal, await ShownToTheStoreOwner(new InvalidOperationException(refusal)));
    }

    [Fact]
    public async Task MissingWalletLookupFailureOnTheSendPath_KeepsReachingTheStoreOwner()
    {
        Assert.Equal("wallet w1 not found",
            await ShownToTheStoreOwner(new KeyNotFoundException("wallet w1 not found")));
    }

    [Theory]
    [MemberData(nameof(HostPathBearingSendFailures))]
    public async Task HostPathBearingSendFailure_IsStillReplacedByTheFallback(Exception failure)
    {
        var shown = await ShownToTheStoreOwner(failure);

        Assert.Equal($"Failed to send asset. {RgbOperatorFacingFailure.EscalateToServerLogs}", shown);
        Assert.DoesNotContain("/Users/", shown);
    }

    public static TheoryData<Exception> HostPathBearingSendFailures() => new()
    {
        new IOException(
            "The process cannot access the file "
            + "'/Users/someone/.btcpayserver/Main/rgb-wallets/w1/rgb_runtime.lock' because it is in use."),
        new UnauthorizedAccessException(
            "Access to the path '/Users/someone/.btcpayserver/Main/rgb-wallets/w1' is denied.")
    };

    [Fact]
    public async Task IntentGateRefusalOnTheSendPath_IsStillReplacedByTheFallback()
    {
        var shown = await ShownToTheStoreOwner(new RgbIntentVerificationException(
            "staged output set /Users/someone/.btcpayserver/Main/rgb-wallets/w1 disagrees"));

        Assert.Equal($"Failed to send asset. {RgbOperatorFacingFailure.EscalateToServerLogs}", shown);
        Assert.DoesNotContain("/Users/", shown);
    }
}
