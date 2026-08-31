using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSendBtcDisplayTests
{
    // Stubs/FakeRGBWalletService throws on every member it is asked for, so it cannot serve the
    // success arm; this one answers the two calls the balance helper makes and nothing else.
    class BalanceWalletService : IRGBWalletService
    {
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

        public Func<BtcBalance>? Balance;
        public Func<List<UnspentOutput>>? Unspents;

        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false)
            => Task.FromResult(Balance!());

        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
            => Task.FromResult(Unspents!());

        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController Build(BalanceWalletService wallets)
    {
        var controller = new RGBController(
            wallets: wallets, stores: null!, handlers: null!, db: null!,
            log: NullLogger<RGBController>.Instance, userManager: null!, events: null!, cache: null!,
            btcPayOptions: Options.Create(new BTCPayServerOptions()), rateSource: null!,
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-controller-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new Stubs.TestTempDataProvider());
        return controller;
    }

    static UnspentOutput Utxo(bool colorable) =>
        new(new UtxoInfo { Outpoint = new Outpoint("t", 0), BtcAmount = 1000, Colorable = colorable }, []);

    [Fact] // G1-T11 — success arm
    public async Task PopulateSendBtcBalance_OnSuccess_ReturnsNullAndPopulatesTheModel()
    {
        var controller = Build(new BalanceWalletService
        {
            Balance = () => new BtcBalance(
                new BalanceInfo { Settled = 12_345, Future = 20_000, Spendable = 19_000 },
                new BalanceInfo { Spendable = 678 }),
            Unspents = () => [Utxo(false), Utxo(false), Utxo(true)]
        });
        var model = new RGBSendBtcViewModel();

        var failure = await controller.PopulateSendBtcBalance(new RGBWallet { Id = "w" }, model);

        Assert.Null(failure);
        Assert.False(model.BalanceUnavailable);
        Assert.Equal(12_345, model.VanillaBalance);
        Assert.Equal(7_655, model.PendingVanillaBalance);
        Assert.Equal(678, model.ColoredBalance);
        Assert.Equal(2, model.VanillaUtxoCount);
    }

    [Fact]
    public async Task TheSendFormNeverOffersMoreThanTheSendPathWillActuallySpend()
    {
        var controller = Build(new BalanceWalletService
        {
            Balance = () => new BtcBalance(
                new BalanceInfo { Settled = 1_000, Future = 500_000, Spendable = 499_000 },
                new BalanceInfo()),
            Unspents = () => [Utxo(false)]
        });
        var model = new RGBSendBtcViewModel();

        await controller.PopulateSendBtcBalance(new RGBWallet { Id = "w" }, model);

        Assert.Equal(1_000, model.VanillaBalance);
        Assert.Equal(499_000, model.PendingVanillaBalance);
    }

    [Fact] // G1-T11 — failure arm; the returned message is the only thing preserving the native detail
    public async Task PopulateSendBtcBalance_OnFailure_FlagsUnavailableAndReturnsTheDetail()
    {
        var controller = Build(new BalanceWalletService
        {
            Balance = () => throw new RgbLibException("list_unspents failed: Indexer unreachable"),
            Unspents = () => throw new RgbLibException("unreached")
        });
        var model = new RGBSendBtcViewModel();

        var failure = await controller.PopulateSendBtcBalance(new RGBWallet { Id = "w" }, model);

        Assert.True(model.BalanceUnavailable);
        Assert.NotNull(failure);
        Assert.Contains("Indexer unreachable", failure);
    }

    [Fact] // G1-T13(a) — the flag must actually change what renders
    public void SendBtcView_RendersTheBalancesOnlyWhenTheyAreKnown()
    {
        var content = ViewSource();
        var block = Between(content, "Available Balance</h5>", "<div class=\"card\">");

        var guard = block.IndexOf("Model.BalanceUnavailable", StringComparison.Ordinal);
        Assert.True(guard >= 0, "the balance card must be guarded by BalanceUnavailable");

        var branch = block.IndexOf("else", guard, StringComparison.Ordinal);
        var figure = block.IndexOf("Model.VanillaBalance.ToString", StringComparison.Ordinal);
        Assert.True(branch >= 0 && figure > branch,
            "the balance figures must render in the else branch; a warning added ABOVE figures that "
            + "still render unconditionally satisfies the guard while showing 0 sats for a funded wallet");
    }

    [Fact] // G1-T13(b) — the max attribute, scoped to the amount input
    public void SendBtcView_EmitsMaxOnlyWhenTheBalanceCanSatisfyTheMinimum()
    {
        var content = ViewSource();
        // Scoped deliberately: the "Send max" link below this region legitimately uses
        // `VanillaBalance > 0`, so a file-wide assertion would be red against a correct view.
        var block = Between(content, "<label asp-for=\"Amount\"", "<span asp-validation-for=\"Amount\"");

        Assert.Contains("max=\"@Model.VanillaBalance\"", block);
        Assert.Contains("Model.VanillaBalance >= 546", block);

        // Both rejected guards leave a real unsatisfiable form — a lookup failure, and any balance
        // between 1 and 545 sats against the hardcoded min="546".
        Assert.DoesNotContain("Model.VanillaBalance > 0", block);
        Assert.DoesNotContain("BalanceUnavailable", block);
    }

    [Fact] // G1-T15 — the GET handler shares the helper instead of duplicating it
    public void SendBtcGetHandler_PopulatesThroughTheSharedHelper()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Controllers/RGBController.cs");

        // Two SendBtc declarations exist (GET takes storeId, POST also takes the model), and
        // RoslynPins.Method asserts exactly one match — so the lookup must be predicated.
        var get = RoslynPins.Method(tree, "RGBController", "SendBtc",
            m => m.ParameterList.Parameters.Count == 1);
        var body = RoslynPins.BodyOf(get);
        RoslynPins.AssertNoLocalShadow(get, "PopulateSendBtcBalance");

        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "PopulateSendBtcBalance" })
            .ToList();
        Assert.True(calls.Count == 1,
            $"the GET handler must populate through the shared helper, found {calls.Count} call(s)");

        var catches = body.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        Assert.True(catches.Count == 0,
            "the GET handler must not keep its own inline try/catch — that is the duplicate copy of "
            + $"site (iii) that fabricated zeros on page load, found {catches.Count}");
    }

    static string ViewSource()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Views", "RGB", "SendBtc.cshtml"));
        Assert.True(File.Exists(path), $"Could not locate SendBtc.cshtml at {path}");
        return File.ReadAllText(path);
    }

    static string Between(string content, string from, string to)
    {
        var start = content.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found in SendBtc.cshtml");
        var end = content.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"'{to}' not found after '{from}' in SendBtc.cshtml");
        return content[start..end];
    }
}
