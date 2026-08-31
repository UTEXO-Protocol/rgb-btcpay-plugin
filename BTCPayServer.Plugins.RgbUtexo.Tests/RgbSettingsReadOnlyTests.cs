using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSettingsReadOnlyTests
{
    static RGBSettingsViewModel Submitted() => new()
    {
        UtxoCount = 7,
        UtxoSize = 2_500,
        MinConfirmations = 3,
        DefaultAssetId = "rgb:contract-abc"
    };

    // WHY assert the submitted values and not just "no exception": deleting the whole object initialiser
    // instead of only its dead ternaries compiles fine and silently persists the property initialiser
    // defaults, discarding everything the operator typed.
    [Fact]
    public void BuildSettingsConfig_PreservesSubmittedValues()
    {
        var config = RGBController.BuildSettingsConfig("wallet-1", Submitted());

        Assert.Equal(7, config.UtxoCount);
        Assert.Equal(2_500, config.UtxoSize);
        Assert.Equal(3, config.MinConfirmations);
        Assert.Equal("wallet-1", config.WalletId);
        Assert.Equal("rgb:contract-abc", config.DefaultAssetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildSettingsConfig_MapsEmptyDefaultAssetIdToNull(string? submitted)
    {
        var model = Submitted();
        model.DefaultAssetId = submitted;

        Assert.Null(RGBController.BuildSettingsConfig("wallet-1", model).DefaultAssetId);
    }

    // WHY a fake local to this class: Stubs/FakeRGBWalletService returns NULL from
    // GetWalletForStoreAsync and SetupConsentGateTests depends on that — the wallet-creation actions
    // redirect when a wallet already exists, while those tests assert a ViewResult. Changing the shared
    // stub would break them. RgbPricingHandlerTests and RgbSendBtcDisplayTests set the same precedent.
    class SettingsWalletService : IRGBWalletService
    {
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(
            string walletId, CancellationToken ct = default)
            => Task.FromResult(RgbVanillaReservationInspector.Clean);

        public int MaxAllocationsPerUtxo { get; init; }

        // Network is not optional: PopulateSettingsViewModel resolves the network settings before either
        // try block, so a wallet with no Network throws before anything is asserted.
        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
            => Task.FromResult<RGBWallet?>(new RGBWallet
            {
                Id = "w1",
                StoreId = storeId,
                Network = "regtest",
                MaxAllocationsPerUtxo = MaxAllocationsPerUtxo
            });

        // The degraded path: this is exactly why the allocation assignment must precede the first try.
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default)
            => throw new InvalidOperationException("wallet offline");

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
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
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw new NotImplementedException();
    }

    static RGBController BuildController(IRGBWalletService wallets)
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

    // WHY reachable with stores: null! — RequireWallet touches only the wallet service, and the only
    // FindStore in PopulateSettingsViewModel sits inside a try whose catch just logs.
    [Fact]
    public async Task SaveSettings_InvalidModelState_ShowsWalletRowAllocations()
    {
        var wallets = new SettingsWalletService { MaxAllocationsPerUtxo = 17 };
        var controller = BuildController(wallets);
        controller.ModelState.AddModelError("DefaultAssetId", "forced invalid");

        var result = await controller.SaveSettings("store-1", Submitted());

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<RGBSettingsViewModel>(view.Model);
        Assert.Equal(17, model.MaxAllocationsPerUtxo);
    }

    // WHY this case exists ALONGSIDE the invalid-ModelState one above: that path reaches
    // PopulateSettingsViewModel with preferSubmitted: TRUE, so it cannot see the allocation assignment
    // being moved behind that flag — and the normal Settings GET, which omits the flag, would then
    // render the property initialiser 10 instead of the wallet's stored value. This is the merchant's
    // ordinary read path, so it needs its own case.
    // WHY the private method is invoked directly rather than through the GET action: Settings(storeId)
    // awaits _stores.FindStore OUTSIDE both try blocks, so it NullReferences on the `stores: null!`
    // controller before PopulateSettingsViewModel is ever entered, and StoreRepository is concrete with
    // no virtual FindStore to substitute. Reaching the method itself is what makes the flag's false
    // branch observable at all; inside it, the only FindStore sits in a try whose catch just logs.
    // WHY behavioural and not a source pin asserting the assignment is unconditional: this observes the
    // value the view would render, so it reddens for ANY conditionalisation — a `if (preferSubmitted)`
    // guard, a move into either try, an early return — not only the shapes a predicate thought to list.
    [Fact]
    public async Task PopulateSettingsViewModel_WithoutPreferSubmitted_ShowsWalletRowAllocations()
    {
        var wallets = new SettingsWalletService { MaxAllocationsPerUtxo = 17 };
        var controller = BuildController(wallets);
        var wallet = await wallets.GetWalletForStoreAsync("store-1");

        var populate = typeof(RGBController).GetMethod(
            "PopulateSettingsViewModel", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(populate);

        // A fresh view model carries the property initialiser 10, which is exactly the wrong value the
        // GET would display; asserting 17 therefore proves the assignment ran on this path.
        var vm = new RGBSettingsViewModel();
        await (Task)populate!.Invoke(controller, new object?[] { vm, wallet, "store-1", false })!;

        Assert.Equal(17, vm.MaxAllocationsPerUtxo);
    }

    // WHY: once the form stops posting the field, binding leaves it 0. A surviving [Range(1,50)]
    // would then invalidate every settings save.
    [Fact]
    public void SettingsViewModel_ValidatesWithMaxAllocationsUnset()
    {
        var model = Submitted();
        model.MaxAllocationsPerUtxo = 0;

        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(
            model, new ValidationContext(model), results, validateAllProperties: true);

        Assert.True(ok, string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    // WHY a NEGATIVE model-validation test, and WHY the out-of-range values are literals rather than
    // RgbConfigBounds references: every other guard on these limits is a source pin, and a source pin
    // identifies the bounds type by name. A `static class RgbConfigBounds` shadowing the real one from
    // the Models namespace — or a widened bound — changes what [Range] enforces here, and only a case
    // that states the limit independently of the constant can see it. Reading the constants would move
    // this test's data along with the mutation and stay green.
    // This is the path the Settings UI takes: ModelState.IsValid is the only thing standing between the
    // posted form and SaveSettings persisting the value.
    [Theory]
    [InlineData(nameof(RGBSettingsViewModel.UtxoCount), 0)]
    [InlineData(nameof(RGBSettingsViewModel.UtxoCount), 21)]
    [InlineData(nameof(RGBSettingsViewModel.UtxoSize), 545)]
    [InlineData(nameof(RGBSettingsViewModel.UtxoSize), 100_001)]
    [InlineData(nameof(RGBSettingsViewModel.MinConfirmations), 0)]
    [InlineData(nameof(RGBSettingsViewModel.MinConfirmations), 101)]
    public void SettingsViewModel_RejectsOutOfRangeValues(string member, int value)
    {
        var model = Submitted();
        var property = typeof(RGBSettingsViewModel).GetProperty(member);
        Assert.NotNull(property);
        property!.SetValue(model, value);

        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(
            model, new ValidationContext(model), results, validateAllProperties: true);

        Assert.False(ok, $"{member}={value} was accepted by model validation");

        // WHY the member name and not just "invalid": an unrelated failure elsewhere on the model would
        // otherwise satisfy the assertion above without this property being validated at all.
        Assert.Contains(member, results.SelectMany(r => r.MemberNames));
    }

    // WHY a text assertion: .cshtml cannot be Roslyn-parsed (precedent SetupViewContentTests).
    // This is recorded as text coverage, not semantic coverage — it can be defeated by reformatting.
    // WHY the path dance: this is the precedent from SetupViewContentTests — the test assembly runs
    // from bin/<cfg>/<tfm>, four levels below the repo root.
    internal static string ReadRepoFile(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        Assert.True(File.Exists(path), $"Could not locate {relativePath} at {path}");
        return File.ReadAllText(path);
    }

    // WHY whole-file assertions anchored on MARKUP rather than a regex scoped by the field's label: the
    // prose "Max Allocations per UTXO" occurs twice in this view — once as the label, once in the info
    // alert below the row. An anchor on it is therefore not just fragile but actively misleading:
    // recasing the label re-anchors the match on the alert, whose block runs forward into the
    // MinConfirmations row, and the guard then fails reporting an <input> that has nothing to do with
    // this field. The two structural invariants below say the same thing without depending on any prose:
    // the value is RENDERED (display-only element) and it is NOT POSTED (no form binding anywhere in the
    // file). Absence is asserted over the whole file because a resurrected editable input placed in some
    // other row would be just as harmful as one placed here.
    [Fact]
    public void SettingsView_MaxAllocationsIsNotAnEditableInput()
    {
        var view = ReadRepoFile(Path.Combine("Views", "RGB", "Settings.cshtml"));

        // Present: the display-only element that shows the wallet's stored value. Tolerant of attribute
        // reflow and of extra classes, because neither changes whether the element can be edited.
        Assert.Matches(
            new Regex(@"form-control-plaintext[^>]*>\s*@Model\.MaxAllocationsPerUtxo"), view);

        // Absent: any form binding for the field. Both spellings are covered — a literal name attribute
        // and the tag-helper form, which renders one.
        Assert.DoesNotContain("name=\"MaxAllocationsPerUtxo\"", view);
        Assert.DoesNotContain("asp-for=\"MaxAllocationsPerUtxo\"", view);

        Assert.DoesNotContain("applies to newly created wallets only", view);
    }

    [Fact]
    public void ConfigType_HasNoMaxAllocationsPerUtxo() =>
        Assert.Null(typeof(RGBPaymentMethodConfig).GetProperty("MaxAllocationsPerUtxo"));

    // ---- local Roslyn helpers ----
    // WHY local statics rather than additions to RoslynPins: that is this repo's established shape —
    // RgbListenerSourcePinTests declares Single, NamedArgument and AssertArgumentBindsTo as private
    // statics in the test class, using only RoslynPins.BoundSymbol as the binding primitive. RoslynPins
    // itself has no argument- or initialiser-assertion members.

    static InvocationExpressionSyntax SingleCall(SyntaxNode scope, string calleeName) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as IdentifierNameSyntax)?.Identifier.ValueText == calleeName
                      || (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText == calleeName);

    static string[] InitialiserMembers(MethodDeclarationSyntax method) =>
        RoslynPins.BodyOf(method).DescendantNodes()
            .OfType<InitializerExpressionSyntax>()
            .SelectMany(i => i.Expressions.OfType<AssignmentExpressionSyntax>())
            .Select(a => ((IdentifierNameSyntax)a.Left).Identifier.ValueText)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    // WHY bind the SYMBOL and assert its kind, not the argument's text: a text comparison is satisfied
    // by any expression that spells the same thing, so introducing a field named `maxAlloc` (or `model`)
    // would replace the intended local/parameter while the pin stayed green.
    static void AssertArgumentBindsTo(
        PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax call,
        int index, string expectedName, SymbolKind expectedKind)
    {
        var expression = call.ArgumentList.Arguments[index].Expression;
        var symbol = RoslynPins.BoundSymbol(plugin, tree, expression);
        Assert.True(symbol.Name == expectedName && symbol.Kind == expectedKind,
            $"argument {index} of '{call.Expression}' must bind to {expectedKind} '{expectedName}', "
            + $"found {symbol.Kind} '{symbol.Name}'");
    }

    // WHY the CALLEE is bound and not merely spelled: SingleCall selects its target by name, so a local
    // function declared inside the action under the same name satisfies a selection-only pin verbatim
    // while the real static helper never runs and its result is never persisted. A local function binds
    // as MethodKind.LocalFunction and an instance member is not static, so both shadows fail here.
    // WHY the containing type is a FULL display name: a short name cannot tell BTCPayServer.Data's
    // StoreDataExtensions apart from a static class of the same name in the plugin's own namespace
    // declaring a SetPaymentMethodConfig(this StoreData, ...) that wins extension lookup and swallows
    // the config — the same shadowing vector the migration and bounds pins close.
    static IMethodSymbol AssertCalleeIs(
        PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax call,
        string containingType, MethodKind kind, bool isStatic)
    {
        var symbol = Assert.IsAssignableFrom<IMethodSymbol>(
            RoslynPins.BoundSymbol(plugin, tree, call.Expression));
        Assert.True(symbol.ContainingType?.ToDisplayString() == containingType,
            $"'{call.Expression}' must resolve to a member of {containingType}, found "
            + $"{symbol.ContainingType?.ToDisplayString()}");
        Assert.True(symbol.MethodKind == kind,
            $"'{call.Expression}' must resolve to a {kind} method, found {symbol.MethodKind}");
        Assert.True(symbol.IsStatic == isStatic,
            $"'{call.Expression}' must{(isStatic ? "" : " not")} be static");
        return symbol;
    }

    // WHY the receiver is BOUND and not just spelled: binding the leaf alone pins nothing — the repo's
    // own precedent in RgbListenerSourcePinTests says so explicitly. `BuildSettingsConfig(new
    // RGBWallet().Id, model)` binds the same RGBWallet.Id symbol while writing an empty WalletId, and a
    // text-compared receiver is satisfied by anything that merely spells the same name, so deleting the
    // local and adding a field or property called `wallet` would read a different object's Id with the
    // pin still green. Asserting the receiver's SymbolKind is what closes that.
    // WHY the receiver's NAME is deliberately NOT pinned: the property is "argument 0 is RGBWallet.Id
    // read off a LOCAL", and the kind alone carries it. Both shadows the paragraph above describes are
    // caught by the kind — a field or property named `wallet` binds as Field/Property, and
    // `new RGBWallet().Id` has an ObjectCreationExpression receiver that fails the IdentifierNameSyntax
    // check. The name adds nothing beyond that, and pinning it would redden the guard on a pure rename
    // of the local — the sort of false alarm that gets a guard deleted.
    static void AssertArgumentBindsToMember(
        PluginCompilation plugin, SyntaxTree tree, InvocationExpressionSyntax call,
        int index, string containingType, string member, SymbolKind receiverKind)
    {
        var access = Assert.IsType<MemberAccessExpressionSyntax>(
            call.ArgumentList.Arguments[index].Expression);
        var symbol = RoslynPins.BoundSymbol(plugin, tree, access);
        Assert.True(symbol.Name == member && symbol.ContainingType?.ToDisplayString() == containingType,
            $"argument {index} of '{call.Expression}' must bind to {containingType}.{member}, "
            + $"found {symbol.ContainingType?.ToDisplayString()}.{symbol.Name}");

        var receiverExpression = Assert.IsType<IdentifierNameSyntax>(access.Expression);
        var receiverSymbol = RoslynPins.BoundSymbol(plugin, tree, receiverExpression);
        Assert.True(receiverSymbol.Kind == receiverKind,
            $"argument {index} must be read from a {receiverKind}, "
            + $"found {receiverSymbol.Kind} '{receiverSymbol.Name}'");
    }

    // WHY separate from RoslynPins.AssertNeverReassigned: that helper matches only assignments whose LEFT
    // side is a bare identifier, so `config.UtxoSize = 5_000_000;` placed after the helper call rewrites
    // the very object that gets persisted and slips past it untouched. The receiver is bound rather than
    // text-compared so a same-named field is not mistaken for the local this pin is guarding.
    // WHY this pin is the ONLY coverage of the post-build mutation path, and therefore has to be tight:
    // SaveSettings reaches the concrete, non-virtual StoreRepository and so cannot be exercised in a unit
    // test, and a member write applied to the config AFTER BuildSettingsConfig returns passes model
    // validation and never enters the helper — so no behavioural test in the suite can observe it.
    // WHY the receiver is unwrapped and the object is forbidden from ESCAPING: matching only a bare
    // identifier receiver left four ways to write to the same object — `(config).X = …`, `config!.X = …`,
    // an alias (`var c2 = config; c2.X = …`) and a helper that takes the config by value and mutates it
    // (the config is a class, so by-value means by-reference to the same instance). Aliasing and handing
    // the config to anything other than the persist site are both refused outright rather than chased,
    // because a rule about where the object may travel is finite where a rule about assignment shapes is
    // not.
    static void AssertNoMemberWriteTo(
        PluginCompilation plugin, SyntaxTree tree, SyntaxNode body, string local,
        InvocationExpressionSyntax persistSite)
    {
        bool IsTheLocal(ExpressionSyntax? expression) =>
            Unwrap(expression) is IdentifierNameSyntax id
            && id.Identifier.ValueText == local
            && RoslynPins.BoundSymbol(plugin, tree, id).Kind == SymbolKind.Local;

        var writes = body.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is MemberAccessExpressionSyntax access && IsTheLocal(access.Expression))
            .Select(a => a.ToString())
            .ToList();
        Assert.True(writes.Count == 0,
            $"'{local}' is mutated after it is built: {string.Join("; ", writes)}");

        var aliases = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => IsTheLocal(v.Initializer?.Value))
            .Select(v => v.Identifier.ValueText)
            .ToList();
        Assert.True(aliases.Count == 0,
            $"'{local}' is aliased after it is built, and a member write through the alias would not be "
            + $"visible to this pin: {string.Join("; ", aliases)}");

        var escapes = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i != persistSite && i.ArgumentList.Arguments.Any(a => IsTheLocal(a.Expression)))
            .Select(i => i.ToString())
            .ToList();
        Assert.True(escapes.Count == 0,
            $"'{local}' is handed to something other than the persist call, which could mutate it before "
            + $"it is stored: {string.Join("; ", escapes)}");
    }

    // Parentheses and the null-forgiving operator change a receiver's SYNTAX without changing which
    // object is written to, so both are stripped before the identifier is compared.
    static ExpressionSyntax? Unwrap(ExpressionSyntax? expression) => expression switch
    {
        ParenthesizedExpressionSyntax parenthesised => Unwrap(parenthesised.Expression),
        PostfixUnaryExpressionSyntax suppressed when suppressed.OperatorToken.ValueText == "!"
            => Unwrap(suppressed.Operand),
        _ => expression
    };

    [Fact]
    public void BuildSettingsConfig_AssignsExactlyTheFiveMembers()
    {
        var tree = PluginCompilation.Shared.Tree("Controllers/RGBController.cs");
        var method = RoslynPins.Method(tree, "RGBController", "BuildSettingsConfig");

        Assert.Equal(
            new[] { "DefaultAssetId", "MinConfirmations", "UtxoCount", "UtxoSize", "WalletId" },
            InitialiserMembers(method));
    }

    [Fact]
    public void SaveSettings_CallsBuildSettingsConfigAndPersistsItsResult()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Controllers/RGBController.cs");
        var method = RoslynPins.Method(tree, "RGBController", "SaveSettings");
        var body = RoslynPins.BodyOf(method);

        var call = SingleCall(body, "BuildSettingsConfig");

        // Bind the CALLEE first: SingleCall only selected it by spelling, and a local function of the
        // same name returning a config of its own choosing would otherwise satisfy every assertion
        // below while the audited helper is bypassed entirely.
        AssertCalleeIs(plugin, tree, call, "BTCPayServer.Plugins.RgbUtexo.Controllers.RGBController",
            MethodKind.Ordinary, isStatic: true);

        // Bind the ARGUMENTS semantically, not just the call: a literal — or a same-named field —
        // would satisfy a call-only or text-only pin.
        AssertArgumentBindsToMember(plugin, tree, call, 0,
            "BTCPayServer.Plugins.RgbUtexo.Data.Entities.RGBWallet", "Id",
            receiverKind: SymbolKind.Local);
        AssertArgumentBindsTo(plugin, tree, call, 1, "model", SymbolKind.Parameter);

        // ...and prove the call's RESULT is what gets persisted. Without this, an implementer could
        // invoke the helper, discard the return value, and keep an inline initialiser beside it —
        // severing the extraction's only link to production while every other guard in this task stays
        // green.
        var declarator = Assert.IsType<VariableDeclaratorSyntax>(call.Parent?.Parent);
        var configLocal = declarator.Identifier.ValueText;

        var persist = SingleCall(body, "SetPaymentMethodConfig");

        // Same class of hole on the persistence side: a local function named SetPaymentMethodConfig
        // that swallows the config would be selected by spelling and keep this pin green while nothing
        // reaches the store.
        AssertCalleeIs(plugin, tree, persist, "BTCPayServer.Data.StoreDataExtensions",
            MethodKind.ReducedExtension, isStatic: false);

        var persisted = Assert.IsType<IdentifierNameSyntax>(persist.ArgumentList.Arguments[1].Expression);
        Assert.Equal(configLocal, persisted.Identifier.ValueText);
        Assert.Equal(SymbolKind.Local, RoslynPins.BoundSymbol(plugin, tree, persisted).Kind);

        // Rule 3, which everything above still needs: the declarator and the persisted identifier are
        // both left intact by `config = new RGBPaymentMethodConfig { UtxoSize = 5_000_000, ... };`
        // inserted between them, and BuildSettingsConfig_AssignsExactlyTheFiveMembers is scoped to the
        // helper's own body so it cannot see the second initialiser.
        // WHY not AssertSingleAssignmentTo: `config` is introduced by a declarator, not by an
        // AssignmentExpressionSyntax, so this pin has no assignment node to hand it — the correct count
        // here is zero, which is what AssertNeverReassigned asserts.
        RoslynPins.AssertNeverReassigned(method, configLocal);
        AssertNoMemberWriteTo(plugin, tree, body, configLocal, persistSite: persist);
    }

    // WHY this pin exists: maxAllocationsPerUtxo is an OPTIONAL parameter on IRGBWalletService, so
    // dropping the argument alongside the config assignment compiles silently and quietly defaults
    // every new wallet row to 10.
    // WHY the coupling to the local's NAME is deliberate and must not be loosened: the property being
    // pinned is "that specific resolved value still reaches the optional parameter", and a syntax pin
    // has no other handle on which value that is — every argument here binds to something, so only the
    // identity of the one carrying ResolveAllocationsPerUtxo's result distinguishes a passing call from
    // one that dropped it. 'maxAlloc' is therefore a PINNED name: renaming the local in RGBController
    // is expected to redden this test, and the correct response is to update the expected name below,
    // never to delete the test.
    [Theory]
    [InlineData("SetupWallet", "CreateWalletAsync")]
    [InlineData("RestoreWallet", "RestoreWalletAsync")]
    [InlineData("RestoreFromBackup", "RestoreFromBackupAsync")]
    public void WalletCreationPathsStillPassMaxAlloc(string action, string callee)
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Controllers/RGBController.cs");
        var method = RoslynPins.Method(tree, "RGBController", action);
        var call = SingleCall(RoslynPins.BodyOf(method), callee);

        // The callee was selected by spelling, so bind it: a local function of the same name accepting a
        // maxAllocationsPerUtxo it then ignores would satisfy the argument check below without the wallet
        // service ever seeing the value.
        AssertCalleeIs(plugin, tree, call, "BTCPayServer.Plugins.RgbUtexo.Services.IRGBWalletService",
            MethodKind.Ordinary, isStatic: false);

        // Locate the argument that BINDS to the local `maxAlloc` — not one that merely spells it.
        // `maxAlloc` is declared with `var`, so the bound symbol is a Local; a field of the same name
        // would bind as a Field and fail here.
        // WHY filter to bare identifiers before binding: RoslynPins.BoundSymbol ASSERTS non-null, and two
        // of these three calls pass `model.Mnemonic!.Trim()` — a BCL member that does not bind under the
        // harness's reference set, which is the reason RoslynPins.NamesBclMember exists. Binding every
        // argument would fail those two cases on arrival.
        var bound = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<IdentifierNameSyntax>()
            .Select(id => RoslynPins.BoundSymbol(plugin, tree, id))
            .OfType<ILocalSymbol>()
            .FirstOrDefault(sym => sym.Name == "maxAlloc");

        Assert.True(bound != null,
            $"'{callee}' in {action} must still be passed the local 'maxAlloc'; arguments were: "
            + string.Join(", ", call.ArgumentList.Arguments.Select(a => a.Expression.ToString())));

        // ...and the local must still carry the RESOLVED value. Passing `maxAlloc` proves only that some
        // local reaches the optional parameter: `var maxAlloc = model.MaxAllocationsPerUtxo;` — the raw,
        // unclamped model value — satisfies everything above. Live impact of that swap is nil today
        // because CreateWalletAsync clamps again, so this closes coverage drift rather than a hole: the
        // controller is the tier that must not hand an out-of-range number to the service in the first
        // place.
        var declarator = Assert.IsType<VariableDeclaratorSyntax>(
            bound!.DeclaringSyntaxReferences.Single().GetSyntax());
        // WHY the shape is checked with a message rather than Assert.IsType: the mutation this closes
        // leaves a MemberAccessExpression here, and "value is not the exact type" tells the next reader
        // nothing about which value or why it matters.
        Assert.NotNull(declarator.Initializer);
        var resolve = declarator.Initializer!.Value as InvocationExpressionSyntax;
        Assert.True(resolve != null,
            $"the local 'maxAlloc' passed to '{callee}' in {action} must be initialised by a call to "
            + $"RGBWalletService.ResolveAllocationsPerUtxo, not by '{declarator.Initializer!.Value}'");

        // The initialiser's callee is bound rather than spelled for the reason AssertCalleeIs exists: a
        // local function or an instance member named ResolveAllocationsPerUtxo would otherwise satisfy a
        // text check while clamping nothing.
        var resolver = AssertCalleeIs(plugin, tree, resolve!,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService", MethodKind.Ordinary, isStatic: true);
        Assert.Equal("ResolveAllocationsPerUtxo", resolver.Name);

        // ...and its INPUT, because everything above binds only the callee: ResolveAllocationsPerUtxo(null)
        // resolves the same static helper, returns the default 10, and passes a `maxAlloc` local that is
        // initialised by the right call — while the number the operator typed on the setup form is thrown
        // away and every wallet is created with 10 allocations regardless of what was requested.
        // The receiver is the ACTION'S OWN parameter, so a same-named field holding some other model
        // cannot stand in for it — the same reason AssertArgumentBindsToMember binds receivers at all.
        // WHY the shape carries its own message, as with the declarator above: the mutation this closes
        // leaves a `null` literal here, and Assert.IsType's "expected MemberAccessExpressionSyntax" tells
        // the next reader nothing about which value went missing.
        Assert.True(
            resolve!.ArgumentList.Arguments.Count == 1
            && resolve.ArgumentList.Arguments[0].Expression is MemberAccessExpressionSyntax,
            $"ResolveAllocationsPerUtxo in {action} must be passed the allocation value read off the "
            + $"posted model, found: {resolve.ArgumentList}");
        AssertArgumentBindsToMember(plugin, tree, resolve, 0,
            "BTCPayServer.Plugins.RgbUtexo.Models.RGBSetupViewModel", "MaxAllocationsPerUtxo",
            receiverKind: SymbolKind.Parameter);
    }
}
