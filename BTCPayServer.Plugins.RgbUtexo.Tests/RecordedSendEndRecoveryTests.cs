using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RecordedSendEndRecoveryTests
{
    const string ExpectedTxid = "AABBCC";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task VerifyRecordedSendEnd_AcceptsExactTxidAtEveryAllowedStatus(int status)
    {
        var accepted = await RGBWalletService.VerifyRecordedSendEndAsync(
            (_, _, _) => Task.FromResult<(int Status, string? Txid)?>((status, "aabbcc")),
            "db", 7, ExpectedTxid, new InvalidOperationException("exit code 139"),
            NullLogger.Instance);

        Assert.True(accepted);
    }

    [Theory]
    [InlineData(4, ExpectedTxid)]
    [InlineData(5, ExpectedTxid)]
    [InlineData(6, ExpectedTxid)]
    [InlineData(1, null)]
    [InlineData(1, "different")]
    [InlineData(null, null)]
    public async Task VerifyRecordedSendEnd_RejectsEveryUntrustedRow(int? status, string? txid)
    {
        var logger = new ExceptionCapturingLogger();
        var accepted = await RGBWalletService.VerifyRecordedSendEndAsync(
            (_, _, _) => Task.FromResult(status.HasValue
                ? ((int Status, string? Txid)?)(status.Value, txid)
                : null),
            "db", 7, ExpectedTxid, new InvalidOperationException("exit code 139"),
            logger);

        Assert.False(accepted);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task VerifyRecordedSendEnd_PropagatesTheReaderExceptionUnchanged()
    {
        var readerException = new InvalidDataException("database locked");

        var thrown = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RGBWalletService.VerifyRecordedSendEndAsync(
                (_, _, _) => Task.FromException<(int Status, string? Txid)?>(readerException),
                "db", 7, ExpectedTxid, new InvalidOperationException("exit code 139"),
                NullLogger.Instance));

        Assert.Same(readerException, thrown);
    }

    [Fact]
    public async Task VerifyRecordedSendEnd_LogsTheOriginalAcceptedExceptionAtErrorLevel()
    {
        var logger = new ExceptionCapturingLogger();
        var sendException = new InvalidOperationException("send-end helper failed with exit code 139");

        var accepted = await RGBWalletService.VerifyRecordedSendEndAsync(
            (_, _, _) => Task.FromResult<(int Status, string? Txid)?>((1, ExpectedTxid)),
            "db", 7, ExpectedTxid, sendException, logger);

        Assert.True(accepted);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(sendException, entry.Exception);
        Assert.Contains("exit code 139", entry.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptedRecordedSendEnd_UsesOnlyTheControllerSuccessBanner()
    {
        var controller = BuildController(new SendResultWalletService(
            (ExpectedTxid, 1, "asset", "TICK", RGBWalletService.SendRecoveryAdvisory)));

        var result = await controller.SendAsset("store", ValidModel());

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Null(controller.TempData[WellKnownTempData.ErrorMessage]);
        AssertRecoverySuccessMessage(controller);
    }

    [Fact]
    public async Task PostSendRefreshFailure_UsesTheSameControllerRecoverySuccessBanner()
    {
        var controller = BuildController(new SendResultWalletService(
            (ExpectedTxid, 1, "asset", "TICK", RGBWalletService.SendRecoveryAdvisory)));

        await controller.SendAsset("store", ValidModel());

        Assert.Null(controller.TempData[WellKnownTempData.ErrorMessage]);
        AssertRecoverySuccessMessage(controller);
    }

    [Fact]
    public async Task NormalSendWithoutRecoveryAdvisory_UsesOrdinaryControllerSuccessBanner()
    {
        var controller = BuildController(new SendResultWalletService(
            (ExpectedTxid, 1, "asset", "TICK", null)));

        await controller.SendAsset("store", ValidModel());

        var message = Assert.IsType<string>(
            controller.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Equal(
            $"Initiated 1 TICK transfer — Txid: {ExpectedTxid}. The transaction broadcasts after the recipient acknowledges the consignment",
            message);
        Assert.DoesNotContain("rgb-lib recorded transfer initiation", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending automatic recovery", message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutgoingBatchRowQueryUsesCorrelatedExistsWithoutOuterJoinFanOut()
    {
        var tree = PluginCompilation.Shared.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "FindOutgoingBatchRowAsync");
        var commandText = RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "cmd.CommandText").Right.ToString();

        Assert.Contains("SELECT bt.status, bt.txid", commandText, StringComparison.Ordinal);
        Assert.Contains("WHERE bt.idx = @idx", commandText, StringComparison.Ordinal);
        Assert.Contains("WHERE atx.batch_transfer_idx = bt.idx AND t.incoming = 0", commandText,
            StringComparison.Ordinal);
        Assert.Contains("EXISTS", commandText, StringComparison.Ordinal);
        Assert.Contains(RoslynPins.BodyOf(method).DescendantNodes()
                .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "DefaultTimeout" && a.Right.ToString() == "2");
    }

    [Fact]
    public void SendEndStartedBranchWiresAuthoritativeReaderAndRetainsRecoveryState()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync");
        RoslynPins.AssertNoLocalShadow(method,
            "FindOutgoingBatchRowAsync", "VerifyRecordedSendEndAsync");
        var body = RoslynPins.BodyOf(method);
        var outerCatch = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Identifier.ValueText == "sendException");
        var sendEndBranch = outerCatch.Block.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "sendEndStarted");
        var verify = sendEndBranch.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "VerifyRecordedSendEndAsync",
                ContainingType.Name: "RGBWalletService"
            });
        var reader = verify.ArgumentList.Arguments[0].Expression;

        Assert.Equal("FindOutgoingBatchRowAsync", reader.ToString());
        RoslynPins.AssertBindsToMemberOf(plugin, tree, reader, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService",
            "FindOutgoingBatchRowAsync", nameof(SendEndStartedBranchWiresAuthoritativeReaderAndRetainsRecoveryState));
        RoslynPins.AssertBindsToMemberOf(plugin, tree, verify.Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService",
            "VerifyRecordedSendEndAsync", nameof(SendEndStartedBranchWiresAuthoritativeReaderAndRetainsRecoveryState));
        Assert.Contains(verify.ArgumentList.Arguments,
            a => a.Expression.ToString() == "sendException");
        var accepted = sendEndBranch.Statement.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "acceptRecordedSendEnd");
        Assert.Contains(accepted.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "sentTxid" && a.Right.ToString() == "sendEndTxid");
        Assert.Contains(accepted.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "recoveryAdvisory"
                 && a.Right.ToString() == "SendRecoveryAdvisory");
        Assert.DoesNotContain(accepted.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "quarantineDischargeEarned");
        Assert.DoesNotContain(accepted.Statement.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => model.GetSymbolInfo(i).Symbol?.Name is "Delete" or "ClearNeedsRecoveryAsync");
        var advisoryAssignments = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "recoveryAdvisory").ToList();
        Assert.Equal(3, advisoryAssignments.Count);
        Assert.All(advisoryAssignments,
            a => Assert.Equal("SendRecoveryAdvisory", a.Right.ToString()));
    }

    [Fact]
    public void VerificationFailureReachesOriginalBareRethrowFromTheOuterCatch()
    {
        var tree = PluginCompilation.Shared.Tree("Services/RGBWalletService.cs");
        var body = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));
        var outerCatch = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Identifier.ValueText == "sendException");
        var acceptDeclaration = outerCatch.Block.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "acceptRecordedSendEnd");
        var verificationCatch = outerCatch.Block.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Identifier.ValueText == "verificationException");
        var falseAssignment = verificationCatch.Block.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "acceptRecordedSendEnd");
        var rejection = outerCatch.Block.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!acceptRecordedSendEnd");
        var rethrow = Assert.IsType<ThrowStatementSyntax>(rejection.Statement);

        Assert.Equal("false", acceptDeclaration.Initializer?.Value.ToString());
        Assert.Equal("false", falseAssignment.Right.ToString());
        Assert.Null(rethrow.Expression);
        Assert.Same(outerCatch, rethrow.Ancestors().OfType<CatchClauseSyntax>().First());
    }

    [Fact]
    public void SendEndStartedVerificationAddsNoNativeBroadcastOrReconcileCall()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var model = plugin.Model(tree);
        var body = RoslynPins.BodyOf(
            RoslynPins.Method(tree, "RGBWalletService", "SendAssetInternalAsync"));
        var outerCatch = body.DescendantNodes().OfType<CatchClauseSyntax>()
            .Single(c => c.Declaration?.Identifier.ValueText == "sendException");
        var sendEndBranch = outerCatch.Block.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "sendEndStarted");
        var invocations = sendEndBranch.Statement.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().ToList();

        Assert.DoesNotContain(invocations, i => model.GetSymbolInfo(i).Symbol?.Name is
            "ReconcileWalletRecoveryAsync" or "RunNativeSendIsolatedAsync"
            or "EnsureRecoveryTransactionBroadcastAsync");
        Assert.DoesNotContain(invocations,
            i => model.GetSymbolInfo(i).Symbol?.Name.Contains("Broadcast", StringComparison.Ordinal) == true);
        var unload = Assert.Single(invocations,
            i => model.GetSymbolInfo(i).Symbol?.Name == "UnloadWallet");
        Assert.DoesNotContain(unload.Ancestors().OfType<IfStatementSyntax>(),
            i => i.Condition.Span.Contains(unload.Span));
    }

    static RGBSendAssetViewModel ValidModel() => new()
    {
        AssetId = "asset",
        RgbInvoice = "rgb:invoice",
        Amount = 1,
        FeeRate = 2
    };

    static RGBController BuildController(IRGBWalletService wallets)
    {
        var controller = new RGBController(
            wallets, null!, null!, null!, NullLogger<RGBController>.Instance, null!, null!, null!,
            Options.Create(new BTCPayServerOptions()), null!,
            new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-controller-tests")), null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static void AssertRecoverySuccessMessage(RGBController controller)
    {
        var message = Assert.IsType<string>(
            controller.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Contains("rgb-lib recorded transfer initiation", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pending automatic recovery", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not retry", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broadcasts after the recipient acknowledges", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("completed", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settled", message, StringComparison.OrdinalIgnoreCase);
    }

    sealed class ExceptionCapturingLogger : ILogger
    {
        internal List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    sealed class SendResultWalletService(
        (string Txid, long AmountSent, string AssetId, string AssetTicker,
            string? RecoveryAdvisory) result) : IRGBWalletService
    {
        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId,
            CancellationToken ct = default) => Task.FromResult<RGBWallet?>(new RGBWallet
        {
            Id = "wallet",
            StoreId = storeId,
            Network = "regtest"
        });

        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker,
            string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice,
            string assetId, long amount, float feeRate, CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
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
}
