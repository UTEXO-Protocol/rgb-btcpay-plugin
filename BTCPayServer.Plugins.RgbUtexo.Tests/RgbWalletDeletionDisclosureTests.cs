using System.Globalization;
using System.Reflection;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbWalletDeletionDisclosureTests
{
    sealed class EmptyStoreContextFactory : ApplicationDbContextFactory
    {
        readonly string _databaseName = Guid.NewGuid().ToString();

        public EmptyStoreContextFactory()
            : base(Options.Create(new DatabaseOptions { ConnectionString = "Host=unused" }),
                NullLoggerFactory.Instance)
        {
        }

        public override ApplicationDbContext CreateContext(
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null) =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options);
    }

    sealed class DeletionWalletService : IRGBWalletService
    {
        public RGBWallet Wallet { get; } = new()
        {
            Id = "wallet-1",
            StoreId = "store-1",
            Name = "Wallet",
            Network = "regtest",
            EncryptedMnemonic = "encrypted"
        };

        public Func<BtcBalance> Balance { get; set; } = () => new BtcBalance(new BalanceInfo(), new BalanceInfo());
        public bool AssetReadFails { get; set; }
        public int BalanceCalls { get; private set; }
        public bool? ReceivedSync { get; private set; }
        public CancellationToken ReceivedToken { get; private set; }
        public bool DeleteCalled { get; private set; }

        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default) =>
            Task.FromResult<RGBWallet?>(Wallet);

        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) =>
            AssetReadFails
                ? throw new InvalidOperationException("asset list unavailable")
                : Task.FromResult<List<RgbAsset>>([]);

        public Task<BtcBalance> GetBtcBalanceAsync(
            string walletId, CancellationToken ct = default, bool sync = false)
        {
            BalanceCalls++;
            ReceivedSync = sync;
            ReceivedToken = ct;
            return Task.FromResult(Balance());
        }

        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default) =>
            Task.FromResult(RgbVanillaReservationInspector.Clean);

        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default)
        {
            DeleteCalled = true;
            return Task.CompletedTask;
        }

        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController BuildController(
        DeletionWalletService wallets, StoreRepository? stores = null)
    {
        var controller = new RGBController(
            wallets: wallets,
            stores: stores!,
            handlers: null!,
            db: null!,
            log: NullLogger<RGBController>.Instance,
            userManager: null!,
            events: null!,
            cache: null!,
            btcPayOptions: Options.Create(new BTCPayServerOptions()),
            rateSource: null!,
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-deletion-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static StoreRepository EmptyStoreRepository() =>
        new(new EmptyStoreContextFactory(), new Newtonsoft.Json.JsonSerializerSettings(), null!, null!);

    static async Task<RGBSettingsViewModel> Populate(
        DeletionWalletService wallets, DateTimeOffset? lastSyncAt)
    {
        wallets.Wallet.LastSyncAt = lastSyncAt;
        var controller = BuildController(wallets);
        var vm = new RGBSettingsViewModel();
        var populate = typeof(RGBController).GetMethod(
            "PopulateSettingsViewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(populate);

        await (Task)populate!.Invoke(
            controller, new object?[] { vm, wallets.Wallet, "store-1", false })!;
        return vm;
    }

    static BtcBalance DistinctBalance() => new(
        new BalanceInfo { Settled = 200, Future = 300, Spendable = 100 },
        new BalanceInfo { Settled = 20, Future = 30, Spendable = 10 });

    [Fact]
    public async Task DeleteWalletWithoutAcknowledgementShowsVisibleRefusalRedirectsToSettingsAndDoesNotCallDeleteWalletAsync()
    {
        var wallets = new DeletionWalletService();
        var controller = BuildController(wallets);

        var result = await controller.DeleteWallet("store-1", acknowledgedRecoveryPhrase: false);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(RGBController.Settings), redirect.ActionName);
        Assert.Equal("store-1", redirect.RouteValues!["storeId"]);
        Assert.False(string.IsNullOrWhiteSpace(controller.TempData["ErrorMessage"] as string),
            "the server-side refusal must be visible through the settings status message");
        Assert.False(wallets.DeleteCalled, "an unacknowledged request must not call DeleteWalletAsync");
    }

    [Fact]
    public async Task DeleteWalletWithAcknowledgementCallsDeleteWalletAsync()
    {
        var wallets = new DeletionWalletService();
        var controller = BuildController(wallets, EmptyStoreRepository());

        await controller.DeleteWallet("store-1", acknowledgedRecoveryPhrase: true);

        Assert.True(wallets.DeleteCalled,
            "an acknowledged wallet deletion must remain reachable and call DeleteWalletAsync");
    }

    [Fact]
    public void DeleteWalletAcknowledgementIsBooleanAndPrecedesEveryMutation()
    {
        var tree = PluginCompilation.Shared.Tree("Controllers/RGBController.cs");
        var method = RoslynPins.Method(tree, "RGBController", "DeleteWallet");
        var body = RoslynPins.BodyOf(method);
        var acknowledgement = Assert.Single(method.ParameterList.Parameters,
            p => p.Identifier.ValueText == "acknowledgedRecoveryPhrase");
        Assert.Equal("bool", acknowledgement.Type!.ToString());
        var refusal = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "!acknowledgedRecoveryPhrase");
        Assert.Contains("TempData[\"ErrorMessage\"]", refusal.Statement.ToString(), StringComparison.Ordinal);
        Assert.Contains("nameof(Settings)", refusal.Statement.ToString(), StringComparison.Ordinal);

        var mutationNames = new[]
        {
            "FindStore", "SetPaymentMethodConfig", "SetExcluded", "UpdateStore", "DeleteWalletAsync"
        };
        var mutations = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => mutationNames.Contains(CallName(i), StringComparer.Ordinal))
            .ToList();
        Assert.NotEmpty(mutations);
        Assert.All(mutations, mutation => Assert.True(refusal.SpanStart < mutation.SpanStart,
            $"the acknowledgement refusal must precede {CallName(mutation)}"));
        var deleteCall = Assert.Single(mutations, mutation => CallName(mutation) == "DeleteWalletAsync");
        var walletMissingGuard = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString() == "wallet == null");
        var allowedReturns = new[]
        {
            Assert.Single(walletMissingGuard.DescendantNodes().OfType<ReturnStatementSyntax>()),
            Assert.Single(refusal.DescendantNodes().OfType<ReturnStatementSyntax>())
        };
        var returnsBeforeDelete = body.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(statement => statement.SpanStart < deleteCall.SpanStart)
            .ToList();
        Assert.True(allowedReturns.Length == returnsBeforeDelete.Count,
            "wallet deletion must remain reachable; add any intentional early return before DeleteWalletAsync "
            + "to the allowed set deliberately");
        Assert.Equal(allowedReturns.Length, returnsBeforeDelete.Count);
        Assert.All(returnsBeforeDelete, statement => Assert.Contains(statement, allowedReturns));
        var allowedThrows = Array.Empty<Microsoft.CodeAnalysis.SyntaxNode>();
        var throwsBeforeDelete = body.DescendantNodes()
            .Where(node => node.SpanStart < deleteCall.SpanStart
                && node is ThrowStatementSyntax or ThrowExpressionSyntax)
            .ToList();
        Assert.True(allowedThrows.Length == throwsBeforeDelete.Count,
            "acknowledged wallet deletion must remain reachable; add any intentional throw statement or "
            + "expression before DeleteWalletAsync to the allowed set deliberately");
        Assert.Equal(allowedThrows.Length, throwsBeforeDelete.Count);
        Assert.All(throwsBeforeDelete, node => Assert.Contains(node, allowedThrows));
    }

    [Fact]
    public async Task DeleteBalancePostCannotAuthorDisclosureAndPopulateClearsReusedValues()
    {
        var wallets = new DeletionWalletService { AssetReadFails = true };
        var controller = BuildController(wallets);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore();
        controller.HttpContext.RequestServices = services.BuildServiceProvider();
        controller.HttpContext.Request.Form = new FormCollection(
            new Dictionary<string, StringValues>
            {
                ["DeleteBalance.Vanilla.Future"] = "999"
            });
        var metadataProvider = controller.HttpContext.RequestServices
            .GetRequiredService<IModelMetadataProvider>();
        var binderFactory = controller.HttpContext.RequestServices
            .GetRequiredService<IModelBinderFactory>();
        var metadata = metadataProvider.GetMetadataForType(typeof(RGBSettingsViewModel));
        var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = metadata,
            BindingInfo = new BindingInfo(),
            CacheToken = metadata
        });
        var bindingContext = DefaultModelBindingContext.CreateBindingContext(
            controller.ControllerContext,
            new FormValueProvider(
                BindingSource.Form, controller.HttpContext.Request.Form, CultureInfo.InvariantCulture),
            metadata,
            bindingInfo: null,
            modelName: "");

        await binder.BindModelAsync(bindingContext);
        var vm = Assert.IsType<RGBSettingsViewModel>(bindingContext.Result.Model);
        Assert.Null(vm.DeleteBalance);
        var property = typeof(RGBSettingsViewModel).GetProperty(nameof(RGBSettingsViewModel.DeleteBalance));
        Assert.NotNull(property);
        Assert.NotNull(property!.GetCustomAttribute<BindNeverAttribute>());
        Assert.NotNull(property.GetCustomAttribute<ValidateNeverAttribute>());

        vm.DeleteBalance = DistinctBalance();
        var populate = typeof(RGBController).GetMethod(
            "PopulateSettingsViewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(populate);
        await (Task)populate!.Invoke(
            controller, new object?[] { vm, wallets.Wallet, "store-1", true })!;

        Assert.Null(vm.DeleteBalance);
    }

    [Fact]
    public void AcknowledgedDeleteStillUsesQuarantineJournalAndStagedTransferGuards()
    {
        var tree = PluginCompilation.Shared.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "DeleteWalletAsync");
        var body = RoslynPins.BodyOf(method);
        var rowRemoval = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => CallName(i) == "Remove");
        var quarantineGuards = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(i => i.Statement.ToString().Contains("RgbWalletQuarantinedException", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(quarantineGuards, guard =>
            guard.Condition.ToString().Contains("IsNeedsRecoveryAsync", StringComparison.Ordinal));
        Assert.Contains(quarantineGuards, guard =>
            guard.Condition.ToString().Contains("File.Exists(journalPath)", StringComparison.Ordinal));
        Assert.Contains(quarantineGuards, guard =>
            guard.Condition.ToString().Contains("RgbNativeSendLease.Exists", StringComparison.Ordinal));
        Assert.Contains(quarantineGuards, guard =>
            guard.Condition.ToString().Contains("FindOrphanedOutgoingBatchIndicesAsync", StringComparison.Ordinal));
        Assert.All(quarantineGuards, guard => Assert.True(guard.SpanStart < rowRemoval.SpanStart,
            "wallet quarantine, recovery journal, native lease and staged-transfer guards must refuse before row deletion"));
    }

    [Fact]
    public async Task PopulateCarriesAllDisplayedBalanceFieldsFromOneCachedSnapshot()
    {
        var wallets = new DeletionWalletService { Balance = DistinctBalance };

        var vm = await Populate(wallets, DateTimeOffset.UtcNow);

        Assert.NotNull(vm.DeleteBalance);
        Assert.Equal(300, vm.DeleteBalance!.Vanilla.Future);
        Assert.Equal(200, vm.DeleteBalance.Vanilla.Settled);
        Assert.Equal(30, vm.DeleteBalance.Colored.Future);
        Assert.Equal(20, vm.DeleteBalance.Colored.Settled);
        Assert.False(wallets.ReceivedSync);
        Assert.True(wallets.ReceivedToken.CanBeCanceled,
            "the cached balance read must receive the five-second cancellation source's token");
    }

    [Fact]
    public async Task FailedAssetReadSkipsBalanceReadAndLeavesDeletionBalanceUnknown()
    {
        var wallets = new DeletionWalletService { AssetReadFails = true, Balance = DistinctBalance };

        var vm = await Populate(wallets, DateTimeOffset.UtcNow);

        Assert.NotNull(vm.ConnectionError);
        Assert.Equal(0, wallets.BalanceCalls);
        Assert.Null(vm.DeleteBalance);
    }

    [Fact]
    public async Task FailedBalanceReadDoesNotEscapeAndLeavesDeletionBalanceUnknown()
    {
        var wallets = new DeletionWalletService
        {
            Balance = () => throw new InvalidOperationException("balance unavailable")
        };

        var vm = await Populate(wallets, DateTimeOffset.UtcNow);

        Assert.Equal(1, wallets.BalanceCalls);
        Assert.Null(vm.DeleteBalance);
    }

    public static TheoryData<BtcBalance, DateTimeOffset?> UntrustedSnapshots => new()
    {
        { new BtcBalance(new BalanceInfo(), new BalanceInfo()), DateTimeOffset.UtcNow },
        { DistinctBalance(), null },
        { new BtcBalance(new BalanceInfo(), new BalanceInfo()), null }
    };

    [Theory]
    [MemberData(nameof(UntrustedSnapshots))]
    public async Task ZeroOrNeverSyncedSnapshotsLeaveDeletionBalanceUnknown(
        BtcBalance balance, DateTimeOffset? lastSyncAt)
    {
        var wallets = new DeletionWalletService { Balance = () => balance };

        var vm = await Populate(wallets, lastSyncAt);

        Assert.Equal(1, wallets.BalanceCalls);
        Assert.Null(vm.DeleteBalance);
    }

    [Fact]
    public void BalanceInvocationPinsExplicitCachedModeFiveSecondTokenAndRequiredOrdering()
    {
        var tree = PluginCompilation.Shared.Tree("Controllers/RGBController.cs");
        var method = RoslynPins.Method(tree, "RGBController", "PopulateSettingsViewModel");
        var body = RoslynPins.BodyOf(method);
        var balanceCall = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => CallName(i) == "GetBtcBalanceAsync");
        Assert.Equal(3, balanceCall.ArgumentList.Arguments.Count);
        var syncArgument = balanceCall.ArgumentList.Arguments[2];
        Assert.Equal("sync", syncArgument.NameColon?.Name.Identifier.ValueText);
        Assert.True(syncArgument.Expression.RawKind == (int)SyntaxKind.FalseLiteralExpression,
            "the deletion disclosure must explicitly request sync: false");

        var cancellationSource = body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "CancellationTokenSource");
        var timeout = Assert.IsType<InvocationExpressionSyntax>(
            Assert.Single(cancellationSource.ArgumentList!.Arguments).Expression);
        Assert.Equal("TimeSpan.FromSeconds", timeout.Expression.ToString());
        Assert.Equal("5", Assert.Single(timeout.ArgumentList.Arguments).Expression.ToString());
        var sourceName = Assert.IsType<VariableDeclaratorSyntax>(cancellationSource.Parent!.Parent)
            .Identifier.ValueText;
        Assert.Equal($"{sourceName}.Token", balanceCall.ArgumentList.Arguments[1].Expression.ToString());

        var reservationCall = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => CallName(i) == "GetVanillaReservationReportAsync");
        var reservationTry = reservationCall.Ancestors().OfType<TryStatementSyntax>().Single();
        Assert.True(reservationTry.Span.End < balanceCall.SpanStart,
            "the cached balance read must remain after the reservation report block");
    }

    [Fact]
    public void SettingsViewPinsBalanceAssetAndSpendAuthorityDisclosures()
    {
        var view = RgbSettingsReadOnlyTests.ReadRepoFile(Path.Combine("Views", "RGB", "Settings.cshtml"));
        var danger = view[view.IndexOf("<div class=\"card border-danger\">", StringComparison.Ordinal)..];

        Assert.Contains("Future: @deleteBalance.Vanilla.Future", danger, StringComparison.Ordinal);
        Assert.Contains("Settled: @deleteBalance.Vanilla.Settled", danger, StringComparison.Ordinal);
        Assert.Contains("Future: @deleteBalance.Colored.Future", danger, StringComparison.Ordinal);
        Assert.Contains("Settled: @deleteBalance.Colored.Settled", danger, StringComparison.Ordinal);
        Assert.DoesNotContain("Spendable", danger, StringComparison.Ordinal);
        Assert.Contains("as of the last successful sync", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may be stale and non-exhaustive", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assume this wallet holds additional value not shown", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("status-1 or status-2 outbound transfers may not appear in any balance figure", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("watch-only and cannot spend", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restoring from a backup file", danger, StringComparison.Ordinal);
        Assert.Contains("requires the seed phrase", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wallet row holding the encrypted mnemonic", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only stored copy of the wallet's", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spend authority", danger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Balances could not be read — assume this wallet holds value", danger, StringComparison.Ordinal);
        Assert.Contains("name=\"acknowledgedRecoveryPhrase\" value=\"true\" required", danger, StringComparison.Ordinal);
        Assert.DoesNotContain("onsubmit=\"return confirm", danger, StringComparison.Ordinal);

        var unavailableStart = danger.IndexOf("Balances could not be read", StringComparison.Ordinal);
        var unavailableEnd = danger.IndexOf("The NIA asset list", unavailableStart, StringComparison.Ordinal);
        var unavailable = danger[unavailableStart..unavailableEnd];
        Assert.DoesNotContain("@deleteBalance", unavailable, StringComparison.Ordinal);
        Assert.DoesNotContain("0 sats", unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsViewNeverClaimsTheAssetInventoryIsComplete()
    {
        var view = RgbSettingsReadOnlyTests.ReadRepoFile(Path.Combine("Views", "RGB", "Settings.cshtml"));
        var danger = view[view.IndexOf("<div class=\"card border-danger\">", StringComparison.Ordinal)..];

        Assert.Contains("The NIA asset list could not be read", view, StringComparison.Ordinal);
        Assert.Contains("No NIA assets were found", view, StringComparison.Ordinal);
        Assert.Contains("Other asset types may still exist", view, StringComparison.Ordinal);
        Assert.Contains("No NIA assets were found", danger, StringComparison.Ordinal);
        Assert.Contains("other asset types not shown", danger, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No assets found.", view, StringComparison.Ordinal);
    }

    static string CallName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => ""
    };
}
