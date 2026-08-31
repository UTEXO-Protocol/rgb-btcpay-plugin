using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbIssueAssetSuccessReportingTests
{
    const string ControllerFile = "Controllers/RGBController.cs";

    class IssuingWalletService : IRGBWalletService
    {
        public Func<RgbAsset>? IssueAssetImpl;
        public int IssueCallCount;

        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
            => Task.FromResult<RGBWallet?>(new RGBWallet { Id = "w1", StoreId = storeId, Network = "regtest" });

        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt,
            int precision = 0, CancellationToken ct = default)
        {
            IssueCallCount++;
            return Task.FromResult(IssueAssetImpl!());
        }

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
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController BuildController(IssuingWalletService wallets)
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
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-issue-asset-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static RGBIssueAssetViewModel Model() =>
        new() { StoreId = "store-1", Ticker = "TCK", Name = "Token", Amount = 100, Precision = 0 };

    [Theory]
    [InlineData("rgb:AAAA-BBBB")]
    [InlineData("")]
    [InlineData("rgb:")]
    public async Task ShortContractId_StillReportsTheIssuanceAsSucceeded_BecauseItDid(string assetId)
    {
        var wallets = new IssuingWalletService
        {
            IssueAssetImpl = () => new RgbAsset { AssetId = assetId, Ticker = "TCK", Name = "Token" }
        };
        var controller = BuildController(wallets);

        var result = await controller.IssueAsset("store-1", Model());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RGBController.Assets), redirect.ActionName);
        Assert.Equal($"Issued TCK ({assetId})", controller.TempData["SuccessMessage"]);
        Assert.True(controller.ModelState.ErrorCount == 0,
            "IssueAssetAsync already mutated the RGB Stock under the send lock and committed an RGBAssets "
            + "row before the success message was ever formatted. A contract id shorter than the slice "
            + "width made formatting throw, the catch turned that into 'Failed to issue asset', and the "
            + "operator retries an issuance that already happened — a second unintended issuance that "
            + "consumes another allocation slot on a colorable UTXO.");
    }

    [Fact]
    public async Task LongContractId_IsAbbreviatedThroughTheSharedHelper_KeepingHeadAndTail()
    {
        const string assetId = "rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-TMsGMSA";
        var wallets = new IssuingWalletService
        {
            IssueAssetImpl = () => new RgbAsset { AssetId = assetId, Ticker = "TCK", Name = "Token" }
        };
        var controller = BuildController(wallets);

        await controller.IssueAsset("store-1", Model());

        Assert.Equal(
            $"Issued TCK ({RGBAssetViewModel.AbbreviateContractIdKeepingHeadAndTail(assetId)})",
            controller.TempData["SuccessMessage"]);
        Assert.Contains(assetId[^RGBAssetViewModel.ContractIdTailCharsShown..],
            (string)controller.TempData["SuccessMessage"]!);
    }

    [Fact]
    public async Task FailureAfterTheStockWasMutated_IsNotReportedToTheOperatorAsAFailedIssuance()
    {
        var wallets = new IssuingWalletService { IssueAssetImpl = () => null! };
        var controller = BuildController(wallets);

        var thrown = await Record.ExceptionAsync(() => controller.IssueAsset("store-1", Model()));

        Assert.True(thrown != null,
            "A null return stands in for any failure of the presentation code that runs after the "
            + "issuance succeeded. Such a failure must escape, not be caught by the handler that reports "
            + "the ISSUANCE as failed.");
        Assert.True(controller.ModelState.ErrorCount == 0,
            "The issuance succeeded and its on-chain-affecting mutation is already committed. Telling the "
            + "operator 'Failed to issue asset' makes them retry and issue a second asset they never "
            + "wanted, so nothing that runs after the mutation may reach that catch.");
        Assert.Equal(1, wallets.IssueCallCount);
    }

    [Fact]
    public void IssueAssetPost_WrapsOnlyTheMutatingCall_SoNoPostSuccessCodeCanReachTheFailureHandler()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(ControllerFile);
        var method = RoslynPins.Method(tree, "RGBController", "IssueAsset",
            m => m.ParameterList.Parameters.Count == 2);

        var tries = RoslynPins.BodyOf(method).DescendantNodes().OfType<TryStatementSyntax>().ToList();
        Assert.True(tries.Count == 1,
            $"expected exactly one try statement in the IssueAsset POST action, found {tries.Count}");

        var guarded = tries[0].Block.Statements;
        Assert.True(guarded.Count == 1,
            "the try must guard the mutating IssueAssetAsync call and nothing else; it guards "
            + $"{guarded.Count} statement(s), and every extra one is code that runs AFTER the RGB Stock "
            + "was mutated yet is still reported to the operator as a failed issuance");

        var invocations = guarded[0].DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        Assert.True(invocations.Count == 1,
            $"the guarded statement must be a single call, found {invocations.Count} invocation(s)");
        RoslynPins.AssertBindsToMemberOf(plugin, tree, invocations[0].Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.IRGBWalletService", "IssueAssetAsync",
            $"{ControllerFile} IssueAsset POST");

        Assert.True(
            tries[0].Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .All(i => i.Expression is not IdentifierNameSyntax { Identifier.ValueText: "RedirectToAction" }),
            "the success redirect must sit outside the try, or a formatting or routing failure on the "
            + "success path is reported as a failed issuance");
        Assert.True(
            tries[0].Block.DescendantNodes().OfType<ElementAccessExpressionSyntax>()
                .All(e => e.Expression is not IdentifierNameSyntax { Identifier.ValueText: "TempData" }),
            "the success message must be built outside the try for the same reason");
    }

    [Fact]
    public void IssueAssetPost_SlicesNoContractId_BecauseTheBoundsSafeHelperExists()
    {
        var method = RoslynPins.Method(PluginCompilation.Shared.Tree(ControllerFile),
            "RGBController", "IssueAsset", m => m.ParameterList.Parameters.Count == 2);

        var ranges = RoslynPins.BodyOf(method).DescendantNodes().OfType<RangeExpressionSyntax>().ToList();

        Assert.True(ranges.Count == 0,
            "a raw range slice of a contract id throws on any id shorter than the slice width; "
            + $"RGBAssetViewModel.AbbreviateContractIdKeepingHeadAndTail is the bounds-safe form, found: "
            + string.Join(", ", ranges.Select(r => r.ToString())));
    }
}
