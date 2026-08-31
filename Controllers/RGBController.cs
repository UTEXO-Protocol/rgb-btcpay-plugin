using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[AutoValidateAntiforgeryToken]
[RefuseUnlessRouteStoreIsTheAuthorizedStore]
[Route("stores/{storeId}/rgb")]
public class RGBController : Controller
{
    static readonly Newtonsoft.Json.JsonSerializer _blobSerializer = BlobSerializer.CreateSerializer().Serializer;
    internal const string AutoReplenishmentNotAuthorizedDisclosure =
        "Automatic colorable-UTXO creation is NOT authorized for this wallet — authorize it on the RGB settings page if you want RGB payments to keep working unattended once the current pool is exhausted.";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _viewSeedLocks = new();
    readonly IRGBWalletService _wallets;
    readonly StoreRepository _stores;
    readonly PaymentMethodHandlerDictionary _handlers;
    readonly RGBPluginDbContextFactory _db;
    readonly ILogger<RGBController> _log;
    readonly UserManager<ApplicationUser> _userManager;
    readonly EventAggregator _events;
    readonly IMemoryCache _cache;
    readonly BTCPayServerOptions _btcPayOptions;
    readonly IRgbRateSource _rateSource;
    readonly RGBConfiguration _cfg;
    readonly RgbAutoReplenishmentAuthorizationStore _authorizations;

    public RGBController(IRGBWalletService wallets, StoreRepository stores,
        PaymentMethodHandlerDictionary handlers, RGBPluginDbContextFactory db, ILogger<RGBController> log,
        UserManager<ApplicationUser> userManager, EventAggregator events, IMemoryCache cache,
        IOptions<BTCPayServerOptions> btcPayOptions, IRgbRateSource rateSource,
        RGBConfiguration cfg, RgbAutoReplenishmentAuthorizationStore authorizations)
    {
        _wallets = wallets; _stores = stores; _handlers = handlers; _db = db; _log = log;
        _userManager = userManager; _events = events; _cache = cache;
        _btcPayOptions = btcPayOptions.Value; _rateSource = rateSource;
        _cfg = cfg; _authorizations = authorizations;
    }

    [HttpGet]
    public async Task<IActionResult> Index([FromRoute] string storeId, bool sync = false)
    {
        var wallet = await _wallets.GetWalletForStoreAsync(storeId);
        if (wallet == null)
        {
            var defaultNetwork = MapChainNameToRgbNetwork(_btcPayOptions.NetworkType);
            var networkSettings = NetworkSettings.GetForNetwork(defaultNetwork);
            return View("Setup", new RGBSetupViewModel
            {
                StoreId = storeId,
                SelectedNetwork = defaultNetwork,
                AvailableNetworks = NetworkSettings.AvailableNetworks,
                ElectrumUrl = networkSettings.ElectrumUrl,
                ProxyEndpoint = networkSettings.ProxyEndpoint,
                Network = defaultNetwork,
                AllNetworkSettings = BuildAllNetworkSettings()
            });
        }

        var vm = new RGBIndexViewModel
        {
            StoreId = storeId,
            WalletId = wallet.Id,
            WalletName = wallet.Name,
            ColorableUtxoCount = -1
        };

        try
        {
            if (sync)
            {
                try { await _wallets.RefreshWalletAsync(wallet.Id); }
                catch (Exception ex) { _log.LogWarning(ex, "Post-restore sync failed"); }
            }

            var (balance, assets, address) = await FetchWalletOverview(wallet.Id);

            vm.BtcBalance = balance.Vanilla.Spendable + balance.Colored.Spendable;
            vm.ColoredBalance = balance.Colored.Spendable;
            vm.Assets = assets.Select(a => a.ToViewModel()).ToList();
            vm.WalletAddress = address;
            vm.IsConnected = true;
            vm.PendingSync = sync && vm.BtcBalance == 0;

            await using var ctx = _db.CreateContext();
            var pendingBlind = await ctx.RGBInvoices
                .Where(i => i.WalletId == wallet.Id && i.AssetId == null && i.BtcPayInvoiceId == null
                            && (i.Status == RGBInvoiceStatus.Pending || i.Status == RGBInvoiceStatus.WaitingConfirmations))
                .OrderByDescending(i => i.CreatedAt)
                .Take(20)
                .ToListAsync();
            vm.PendingBlindReceives = pendingBlind.Select(p => new RGBPendingBlindReceiveRow
            {
                InvoiceId = p.Id,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpirationTimestamp.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(p.ExpirationTimestamp.Value) : null,
                Status = p.Status.ToString()
            }).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load RGB wallet overview for store {StoreId}", storeId);
            vm.IsConnected = false;
            vm.ConnectionError = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to load wallet data. {RgbOperatorFacingFailure.EscalateToServerLogs}");
        }

        return View(vm);
    }

    [HttpGet("setup")]
    public IActionResult Setup([FromRoute] string storeId)
    {
        var defaultNetwork = MapChainNameToRgbNetwork(_btcPayOptions.NetworkType);
        var networkSettings = NetworkSettings.GetForNetwork(defaultNetwork);
        return View(new RGBSetupViewModel 
        { 
            StoreId = storeId,
            SelectedNetwork = defaultNetwork,
            AvailableNetworks = NetworkSettings.AvailableNetworks,
            ElectrumUrl = networkSettings.ElectrumUrl,
            ProxyEndpoint = networkSettings.ProxyEndpoint,
            Network = defaultNetwork,
            AllNetworkSettings = BuildAllNetworkSettings()
        });
    }
    
    internal static string MapChainNameToRgbNetwork(ChainName chainName)
        => AllowedRgbNetworksFor(chainName)[0];

    internal static string[] AllowedRgbNetworksFor(ChainName chainName)
    {
        var name = chainName.ToString();
        if (name.Equals("Mainnet", StringComparison.OrdinalIgnoreCase)) return ["mainnet"];
        if (name.Equals("Testnet", StringComparison.OrdinalIgnoreCase)) return ["testnet"];
        if (name.Equals("Regtest", StringComparison.OrdinalIgnoreCase)) return ["regtest"];
        if (name.Equals("Signet",  StringComparison.OrdinalIgnoreCase)) return ["signet", "utexo"];
        throw new InvalidOperationException($"Unsupported BTCPay network type: {name}");
    }

    internal static string? ValidateSelectedNetwork(string? selectedNetwork, ChainName chainName)
    {
        if (string.IsNullOrWhiteSpace(selectedNetwork)
            || !NetworkSettings.AvailableNetworks.Contains(selectedNetwork, StringComparer.OrdinalIgnoreCase))
            return "Invalid network selection";

        var allowed = AllowedRgbNetworksFor(chainName);
        if (!allowed.Contains(selectedNetwork, StringComparer.OrdinalIgnoreCase))
            return $"Wallet network '{selectedNetwork}' is not allowed for BTCPay deployment network '{chainName}' (allowed: {string.Join(", ", allowed)})";

        return null;
    }

    static Dictionary<string, NetworkSettingsDto> BuildAllNetworkSettings()
    {
        return NetworkSettings.AvailableNetworks.ToDictionary(
            n => n,
            n => {
                var s = NetworkSettings.GetForNetwork(n);
                return new NetworkSettingsDto { Electrum = s.ElectrumUrl, Proxy = s.ProxyEndpoint };
            });
    }

    [HttpPost("setup")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> SetupWallet([FromRoute] string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        if (!ModelState.IsValid)
        {
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = RGBWalletService.ResolveAllocationsPerUtxo(model.MaxAllocationsPerUtxo);
            var wallet = await _wallets.CreateWalletAsync(storeId, model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet created on {model.SelectedNetwork} with max {maxAlloc} allocations per UTXO! " + AutoReplenishmentNotAuthorizedDisclosure;
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create RGB wallet for store {StoreId}", storeId);
            ModelState.AddModelError("", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to create wallet. {RgbOperatorFacingFailure.EscalateToServerLogs}"));
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }
    }

    [HttpPost("restore")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> RestoreWallet([FromRoute] string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            model.IsRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            model.IsRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = RGBWalletService.ResolveAllocationsPerUtxo(model.MaxAllocationsPerUtxo);
            var wallet = await _wallets.RestoreWalletAsync(storeId, model.Mnemonic!.Trim(), model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet restored on {model.SelectedNetwork}! " + AutoReplenishmentNotAuthorizedDisclosure;
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore RGB wallet from mnemonic for store {StoreId}", storeId);
            ModelState.AddModelError("", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to restore wallet. {RgbOperatorFacingFailure.EscalateToServerLogs}"));
            model.IsRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }
    }

    [HttpPost("restore-backup")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [BoundRgbBackupUploadToConfiguredLimit]
    [BoundRgbBackupUploadConcurrency]
    public async Task<IActionResult> RestoreFromBackup([FromRoute] string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        if (model.BackupFile == null || model.BackupFile.Length == 0)
        {
            ModelState.AddModelError("BackupFile", "Backup file is required");
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        var uploadBoundBytes = RgbRestoreUploadBound.ResolveBytes(_cfg);
        if (RgbRestoreUploadBound.IsOverBound(model.BackupFile.Length, uploadBoundBytes))
        {
            ModelState.AddModelError("BackupFile", RgbRestoreUploadBound.RefusalMessage(uploadBoundBytes));
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        try
        {
            await RgbRestoreValidationGate.RunOneAtATimeOrRefuseAsync(
                () => ValidateBackupFileHeader(model.BackupFile!));
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("BackupFile", ex.Message);
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        if (string.IsNullOrWhiteSpace(model.BackupPassword))
        {
            ModelState.AddModelError("BackupPassword", "Backup password is required");
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }

        string? uploadDir = null;
        try
        {
            uploadDir = RgbRestoreUploadStaging.CreateDirectoryForAttempt(_cfg, model.SelectedNetwork);
            var tempPath = Path.Combine(uploadDir, RgbRestoreUploadStaging.UploadedBackupFileName);
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await model.BackupFile.CopyToAsync(stream);
            }

            var maxAlloc = RGBWalletService.ResolveAllocationsPerUtxo(model.MaxAllocationsPerUtxo);
            var wallet = await _wallets.RestoreFromBackupAsync(
                storeId, model.Mnemonic!.Trim(), tempPath, model.BackupPassword,
                model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet restored from backup on {model.SelectedNetwork}! " + AutoReplenishmentNotAuthorizedDisclosure;
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to restore RGB wallet from backup for store {StoreId}", storeId);
            ModelState.AddModelError("", "Restore failed: " + RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, RgbOperatorFacingFailure.EscalateToServerLogs));
            model.IsBackupRestore = true;
            PopulateSetupModelAndDropRecoverySecrets(model);
            return View("Setup", model);
        }
        finally
        {
            RgbRestoreUploadStaging.DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(uploadDir, _log);
        }
    }

    [HttpGet("assets")]
    public async Task<IActionResult> Assets([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var assets = await _wallets.ListAssetsAsync(wallet.Id);

        return View(new RGBAssetsViewModel
        {
            StoreId = storeId,
            Assets = assets.Select(a => a.ToViewModel()).ToList()
        });
    }

    [HttpGet("assets/issue")]
    public async Task<IActionResult> IssueAsset([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        return View(new RGBIssueAssetViewModel { StoreId = storeId });
    }

    [HttpPost("assets/issue")]
    public async Task<IActionResult> IssueAsset([FromRoute] string storeId, RGBIssueAssetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        RgbAsset asset;
        try
        {
            asset = await _wallets.IssueAssetAsync(wallet.Id, model.Ticker, model.Name, model.Amount, model.Precision);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to issue RGB asset for wallet {WalletId}", wallet.Id);
            ModelState.AddModelError("", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to issue asset. {RgbOperatorFacingFailure.EscalateToServerLogs}"));
            return View(model);
        }

        TempData["SuccessMessage"] =
            $"Issued {asset.Ticker} ({RGBAssetViewModel.AbbreviateContractIdKeepingHeadAndTail(asset.AssetId)})";
        return RedirectToAction(nameof(Assets), new { storeId });
    }

    [HttpGet("utxos")]
    public async Task<IActionResult> Utxos([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        // ListUnspentsAsync now throws when the native call fails instead of returning an empty list, so this
        // page reports the failure rather than rendering a convincing "0 UTXOs" that is really an error.
        List<UnspentOutput> unspents;
        try
        {
            unspents = await _wallets.ListUnspentsAsync(wallet.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not list UTXOs for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = "Could not read this wallet's UTXOs from RGB. Try again in a moment.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        await using var ctx = _db.CreateContext();
        // Shares the listener's predicate so the figure an operator reads while diagnosing a skipped
        // replenishment matches the one the listener decided on.
        var pendingInvoices = await ctx.RGBInvoices.CountAsync(
            RGBInvoiceListener.ActivePendingInvoicePredicate(
                wallet.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        return View(new RGBUtxosViewModel
        {
            StoreId = storeId,
            MaxAllocationsPerUtxo = wallet.MaxAllocationsPerUtxo,
            PendingInvoices = pendingInvoices,
            Utxos = unspents.Select(u => new RGBUtxoViewModel
            {
                Outpoint = $"{u.Utxo.Outpoint.Txid}:{u.Utxo.Outpoint.Vout}",
                Amount = u.Utxo.BtcAmount,
                Colorable = u.Utxo.Colorable,
                Allocations = u.RgbAllocations.Select(a => new RGBAllocationViewModel
                {
                    AssetId = a.AssetId, Amount = a.Amount, Settled = a.Settled
                }).ToList()
            }).ToList()
        });
    }

    [HttpPost("utxos/create")]
    public async Task<IActionResult> CreateUtxos([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var count = config?.UtxoCount ?? 4;
        var size = config?.UtxoSize ?? 1000;

        try
        {
            RgbConfigBounds.EnsurePaymentMethodValuesValid(
                count, size, config?.MinConfirmations ?? 1);
            var created = await _wallets.CreateColorableUtxosAsync(wallet.Id, count, size);
            TempData["SuccessMessage"] = created > 0 ? $"{created} UTXOs created ({size} sats each)" : "UTXOs already available";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create colorable UTXOs for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to create UTXOs. {RgbOperatorFacingFailure.EscalateToServerLogs}");
        }

        return RedirectToAction(nameof(Utxos), new { storeId });
    }

    [HttpGet("send-btc")]
    public async Task<IActionResult> SendBtc([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var vm = new RGBSendBtcViewModel { StoreId = storeId };
        var failure = await PopulateSendBtcBalance(wallet, vm);
        if (failure != null)
            TempData["ErrorMessage"] = $"Failed to load wallet data: {failure}";

        return View(vm);
    }

    [HttpPost("send-btc")]
    public async Task<IActionResult> SendBtc([FromRoute] string storeId, RGBSendBtcViewModel model)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (!ModelState.IsValid)
        {
            await PopulateSendBtcBalance(wallet, model);
            return View(model);
        }

        var network = NetworkHelper.GetNetwork(wallet.Network);
        try
        {
            BitcoinAddress.Create(model.DestinationAddress.Trim(), network);
        }
        catch
        {
            ModelState.AddModelError("DestinationAddress", "Invalid Bitcoin address for this network");
            await PopulateSendBtcBalance(wallet, model);
            return View(model);
        }

        try
        {
            var result = await _wallets.SendBtcAsync(
                wallet.Id, model.DestinationAddress.Trim(), model.Amount, model.FeeRate);
            TempData["SuccessMessage"] = $"Sent {result.AmountSent:N0} sats (fee: {result.Fee:N0} sats). Txid: {result.Txid}";
            return RedirectToAction(nameof(BtcTransactions), new { storeId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send BTC from wallet {WalletId}", wallet.Id);
            ModelState.AddModelError("", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to send BTC. {RgbOperatorFacingFailure.EscalateToServerLogs}"));
            await PopulateSendBtcBalance(wallet, model);
            return View(model);
        }
    }

    // Returns null on success, otherwise the failure detail. WHY returned rather than only logged:
    // the GET handler surfaces the native message to the merchant, and a log-only catch would
    // silently downgrade that report to nothing.
    internal async Task<string?> PopulateSendBtcBalance(Data.Entities.RGBWallet wallet, RGBSendBtcViewModel model)
    {
        try
        {
            var balance = await _wallets.GetBtcBalanceAsync(wallet.Id);
            var unspents = await _wallets.ListUnspentsAsync(wallet.Id);
            model.VanillaBalance = balance.Vanilla.Settled;
            model.PendingVanillaBalance = Math.Max(0, balance.Vanilla.Future - balance.Vanilla.Settled);
            model.ColoredBalance = balance.Colored.Spendable;
            model.VanillaUtxoCount = unspents.Count(u => !u.Utxo.Colorable);
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to populate balance for send form");
            model.BalanceUnavailable = true;
            return RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, RgbOperatorFacingFailure.EscalateToServerLogs);
        }
    }

    [HttpGet("send-asset")]
    public async Task<IActionResult> SendAsset([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var vm = new RGBSendAssetViewModel { StoreId = storeId };
        await PopulateSendAssetData(wallet, vm);
        return View(vm);
    }

    [HttpPost("send-asset")]
    public async Task<IActionResult> SendAsset([FromRoute] string storeId, RGBSendAssetViewModel model)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (!ModelState.IsValid)
        {
            await PopulateSendAssetData(wallet, model);
            return View(model);
        }

        try
        {
            var result = await _wallets.SendAssetAsync(
                wallet.Id, model.RgbInvoice.Trim(), model.AssetId, model.Amount, model.FeeRate);
            var msg = $"Initiated {result.AmountSent:N0} {result.AssetTicker} transfer — Txid: {result.Txid}. "
                      + "The transaction broadcasts after the recipient acknowledges the consignment";
            if (result.RecoveryAdvisory != null)
                msg = $"rgb-lib recorded transfer initiation for Txid: {result.Txid} despite a helper or refresh failure. {result.RecoveryAdvisory}";
            TempData[WellKnownTempData.SuccessMessage] = msg;
            return RedirectToAction(nameof(Transfers), new { storeId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send RGB asset");
            ModelState.AddModelError("", RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to send asset. {RgbOperatorFacingFailure.EscalateToServerLogs}"));
            await PopulateSendAssetData(wallet, model);
            return View(model);
        }
    }

    async Task PopulateSendAssetData(Data.Entities.RGBWallet wallet, RGBSendAssetViewModel model)
    {
        try
        {
            var assets = await _wallets.ListAssetsAsync(wallet.Id);
            model.AvailableAssets = assets.Select(a => a.ToViewModel()).ToList();
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to populate assets for send form"); }
    }

    [HttpGet("btc-transactions")]
    public async Task<IActionResult> BtcTransactions([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var txs = await _wallets.ListBtcTransactionsAsync(wallet.Id);

            return View(new RGBBtcTransactionsViewModel
            {
                StoreId = storeId,
                Transactions = txs.Select(t => new RGBBtcTransactionViewModel
                {
                    Txid = t.Txid,
                    Type = BtcTxType(t.GetTransactionTypeInt()),
                    Received = t.Received,
                    Sent = t.Sent,
                    Fee = t.Fee,
                    Height = t.ConfirmationTime?.Height,
                    Timestamp = t.ConfirmationTime != null
                        ? DateTimeOffset.FromUnixTimeSeconds(t.ConfirmationTime.Timestamp)
                        : null
                }).OrderByDescending(t => t.Height ?? long.MaxValue).ToList()
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load BTC transactions for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = "Failed to load transactions: "
                + RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                    ex, RgbOperatorFacingFailure.EscalateToServerLogs);
            return RedirectToAction(nameof(Index), new { storeId });
        }
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> Transfers([FromRoute] string storeId, string? assetId = null)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var transfers = await _wallets.GetTransfersAsync(wallet.Id, assetId);
        var rows = transfers.Select(t => new RGBTransferViewModel
            {
                Idx = t.Idx,
                Status = TransferStatus(t.Status),
                Kind = TransferKind(t.Kind),
                Amount = t.Amount,
                Txid = t.Txid,
                RecipientId = t.RecipientId,
                AssetTicker = t.AssetTicker
            }).ToList();

        return View(new RGBTransfersViewModel
        {
            StoreId = storeId,
            SelectedAssetId = assetId,
            Transfers = rows
        });
    }

    [HttpPost("receive-any-asset")]
    public async Task<IActionResult> CreateReceiveAnyAsset([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var inv = await _wallets.CreateInvoiceAsync(
                wallet.Id, assetId: null, amount: null,
                expiration: TimeSpan.FromHours(2), btcPayInvoiceId: null);
            return RedirectToAction(nameof(ReceiveAnyAsset), new { storeId, rgbInvoiceId = inv.Id });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to create blind-receive invoice for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to create a receive invoice. {RgbOperatorFacingFailure.EscalateToServerLogs}");
            return RedirectToAction(nameof(Index), new { storeId });
        }
    }

    [HttpGet("receive-any-asset/{rgbInvoiceId}")]
    public async Task<IActionResult> ReceiveAnyAsset([FromRoute] string storeId, string rgbInvoiceId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        await using var ctx = _db.CreateContext();
        var inv = await ctx.RGBInvoices
            .FirstOrDefaultAsync(i => i.Id == rgbInvoiceId && i.WalletId == wallet.Id);
        if (inv == null || inv.AssetId != null || inv.BtcPayInvoiceId != null) return NotFound();

        return View("BlindReceive", new RGBBlindReceiveViewModel
        {
            StoreId = storeId,
            WalletId = wallet.Id,
            InvoiceId = inv.Id,
            RgbInvoiceString = inv.Invoice,
            RecipientId = inv.RecipientId,
            ExpiresAt = inv.ExpirationTimestamp.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(inv.ExpirationTimestamp.Value)
                : null,
            Status = inv.Status.ToString(),
            ReceivedAssetId = inv.ReceivedAssetId,
            ReceivedAmount = inv.ReceivedAmount
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            await _wallets.RefreshWalletAsync(wallet.Id);
            TempData["SuccessMessage"] = "Wallet refreshed";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to refresh wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to refresh wallet. {RgbOperatorFacingFailure.EscalateToServerLogs}");
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteWallet([FromRoute] string storeId, bool acknowledgedRecoveryPhrase)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (!acknowledgedRecoveryPhrase)
        {
            TempData["ErrorMessage"] = "Record the wallet seed phrase before acknowledging wallet deletion.";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        try
        {
            var store = await _stores.FindStore(storeId);
            Newtonsoft.Json.Linq.JToken? originalConfig = null;
            var originallyExcluded = false;
            if (store != null)
            {
                originalConfig = store.GetPaymentMethodConfig(
                    RGBPlugin.RGBPaymentMethodId)?.DeepClone();
                originallyExcluded = store.GetStoreBlob().IsExcluded(RGBPlugin.RGBPaymentMethodId);

                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], null);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            try
            {
                await _wallets.DeleteWalletAsync(wallet.Id);
            }
            catch (Exception deleteError)
            {
                if (store != null)
                {
                    try
                    {
                        store.SetPaymentMethodConfig(
                            RGBPlugin.RGBPaymentMethodId, originalConfig);
                        var blob = store.GetStoreBlob();
                        blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, originallyExcluded);
                        store.SetStoreBlob(blob);
                        await _stores.UpdateStore(store);
                    }
                    catch (Exception compensationError)
                    {
                        throw new AggregateException(
                            "Wallet deletion failed and store configuration rollback also failed",
                            deleteError, compensationError);
                    }
                }
                throw;
            }
            TempData["SuccessMessage"] = "RGB wallet deleted";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = "Failed to delete wallet: "
                + RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                    ex, RgbOperatorFacingFailure.EscalateToServerLogs);
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("backup")]
    public async Task<IActionResult> BackupWallet([FromRoute] string storeId, string password)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            TempData["ErrorMessage"] = "Password must be at least 8 characters";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        if (RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(password))
        {
            TempData["ErrorMessage"] = RestoreProcessRunner.BackupPasswordLineBreakRefusal;
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        string? tempPath = null;
        try
        {
            tempPath = await _wallets.BackupWalletAsync(wallet.Id, password, HttpContext.RequestAborted);
            var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.DeleteOnClose);
            return File(stream, "application/octet-stream", $"rgb-wallet-backup-{DateTime.UtcNow:yyyyMMdd}.rgb");
        }
        catch (Exception ex)
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
            _log.LogError(ex, "Backup failed for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = "Backup failed: "
                + RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                    ex, RgbOperatorFacingFailure.EscalateToServerLogs);
            return RedirectToAction(nameof(Settings), new { storeId });
        }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);

        var vm = new RGBSettingsViewModel
        {
            StoreId = storeId,
            DefaultAssetId = config?.DefaultAssetId,
            UtxoCount = config?.UtxoCount ?? 4,
            UtxoSize = config?.UtxoSize ?? 1000,
            MinConfirmations = config?.MinConfirmations ?? 1
        };
        await PopulateSettingsViewModel(vm, wallet, storeId);
        return View(vm);
    }

    async Task PopulateSettingsViewModel(RGBSettingsViewModel vm, Data.Entities.RGBWallet wallet, string storeId,
        bool preferSubmitted = false)
    {
        var networkSettings = RGBConfiguration.GetNetworkSettings(wallet.Network);
        vm.StoreId = storeId;
        vm.WalletId = wallet.Id;
        vm.WalletName = wallet.Name;
        vm.XpubVanilla = wallet.XpubVanilla;
        vm.XpubColored = wallet.XpubColored;
        vm.MasterFingerprint = wallet.MasterFingerprint;
        vm.Network = wallet.Network;
        vm.CreatedAt = wallet.CreatedAt;
        vm.ElectrumUrl = networkSettings.ElectrumUrl;
        // WHY here and not inside either try: both catches only log, so an assignment inside one is
        // silently skipped on exactly the degraded paths they exist to tolerate.
        vm.MaxAllocationsPerUtxo = wallet.MaxAllocationsPerUtxo;

        try
        {
            var assets = await _wallets.ListAssetsAsync(wallet.Id);
            vm.AvailableAssets = assets.Select(a => a.ToViewModel()).ToList();
            vm.IsConnected = true;
        }
        catch (Exception ex)
        {
            vm.ConnectionError = RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                ex, $"Failed to load wallet assets. {RgbOperatorFacingFailure.EscalateToServerLogs}");
            _log.LogWarning(ex, "RGB wallet connection failed");
        }

        // The notice is advisory and must NEVER fail the settings page, so the WHOLE block —
        // FindStore, GetRgbConfig, GetStoreBlob and the probe — sits inside one catch.
        try
        {
            var store = await _stores.FindStore(storeId);
            // On the validation-failure re-render the form redisplays what the merchant submitted, so
            // the notice must describe that, not what is persisted — otherwise they copy a rule for the
            // wrong contract. A cleared selection must show nothing, which is why this is a flag rather
            // than a null-coalesce: "" is a meaningful submitted value.
            var selectedAssetId = preferSubmitted ? vm.DefaultAssetId : GetRgbConfig(store)?.DefaultAssetId;

            // The whitespace/empty handling lives in RgbPricingNotice.For, which returns None; the
            // probe is skipped in that case because RgbPricingCode.For would throw on it.
            if (store is not null)
            {
                var quote = store.GetStoreBlob().DefaultCurrency;
                var probe = string.IsNullOrWhiteSpace(selectedAssetId)
                    ? null
                    : await ProbeRateAsync(store, RgbPricingCode.For(selectedAssetId), quote);

                var notice = RgbPricingNotice.For(selectedAssetId, quote, probe);
                vm.PricingCode = notice.PricingCode;
                vm.SuggestedRateRule = notice.SuggestedRateRule;
                vm.SuggestedPegRule = notice.SuggestedPegRule;
                vm.QuoteCurrency = notice.QuoteCurrency;
                vm.RateRuleMissing = notice.RateRuleMissing;
                vm.UsesDefaultRules = notice.UsesDefaultRules;
                vm.RateUnresolved = notice.RateUnresolved;
            }
        }
        catch (Exception ex)
        {
            // Render no notice rather than a wrong one, and never break the page.
            _log.LogWarning(ex, "RGB pricing notice unavailable for store {StoreId}", storeId);
        }

        try
        {
            var owningStore = await _stores.FindStore(storeId);
            vm.StoreArchived = owningStore?.Archived == true;

            var grant = await _authorizations.FindAsync(storeId);
            vm.AutomaticReplenishmentDecision = grant?.Decision ?? RgbAutoReplenishmentDecision.Undecided;
            vm.AutomaticReplenishmentDecidedAt = grant?.DecidedAt;
            vm.AutomaticReplenishmentDecidedBy = grant?.DecidedBy;
            vm.AutomaticReplenishmentGranted =
                RgbAutoReplenishmentAuthorizationStore.IsGranted(grant, wallet.Id);
            vm.MaxAutoColorableUtxos = _cfg.MaxAutoColorableUtxos;

            var storedConfig = GetRgbConfig(owningStore);
            var persistedValuesValid = ArePersistedReplenishmentFiguresValid(storedConfig);
            if (persistedValuesValid)
            {
                vm.PersistedUtxoCount = storedConfig!.UtxoCount;
                vm.PersistedUtxoSize = storedConfig.UtxoSize;
                vm.WorstCaseReplenishFeeBaseSats =
                    RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(storedConfig.UtxoCount);
                vm.WorstCaseReplenishFeePerVanillaUtxoSats =
                    RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(storedConfig.UtxoCount);
            }

            vm.ReplenishmentNoticeCause = RgbReplenishmentNotice.Evaluate(
                paymentMethodEnabled: owningStore != null
                    && !owningStore.GetStoreBlob().IsExcluded(RGBPlugin.RGBPaymentMethodId),
                hasStoredConfig: storedConfig != null,
                configValuesValid: persistedValuesValid,
                maxAutoColorableUtxos: _cfg.MaxAutoColorableUtxos,
                standingAuthorizationGranted: vm.AutomaticReplenishmentGranted);
            vm.ReplenishmentNoticeMessage =
                RgbReplenishmentNotice.MessageFor(vm.ReplenishmentNoticeCause);
            vm.ReplenishmentNoticeInvitesGrant =
                RgbReplenishmentNotice.InvitesGrant(vm.ReplenishmentNoticeCause);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "RGB archived-store notice unavailable for store {StoreId}", storeId);
        }

        try
        {
            var reservations = await _wallets.GetVanillaReservationReportAsync(wallet.Id);
            vm.VanillaReservationState = reservations.State;
            vm.VanillaReservationCount = reservations.Reserved.Count;
            vm.VanillaReservationStillUnspentCount = reservations.StillUnspent.Count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "RGB pending vanilla reservation report unavailable for wallet {WalletId}", wallet.Id);
        }

        vm.DeleteBalance = null;
        if (vm.ConnectionError is null)
        {
            try
            {
                using var balanceReadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var balance = await _wallets.GetBtcBalanceAsync(
                    wallet.Id, balanceReadTimeout.Token, sync: false);
                if (wallet.LastSyncAt is not null
                    && (balance.Vanilla.Future != 0 || balance.Vanilla.Settled != 0
                        || balance.Colored.Future != 0 || balance.Colored.Settled != 0))
                    vm.DeleteBalance = balance;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "RGB deletion balance unavailable for wallet {WalletId}", wallet.Id);
            }
        }
    }

    // Timeout and Error deliberately set nothing upstream: they say nothing about the store's
    // configuration, and a transient exchange problem must not tell a correctly-configured merchant
    // that their rules are wrong.
    async Task<RgbRateResult> ProbeRateAsync(BTCPayServer.Data.StoreData store, string pricingCode, string quote)
    {
        // The rules are part of the key, not just the pair: this notice is what a merchant reloads to
        // confirm they have FIXED their rule, and rates are edited on a different controller with no
        // invalidation hook. Keyed on the pair alone, a stale NoRate would keep claiming failure.
        var fingerprint = RgbPricingNotice.RateRulesFingerprint(store.GetStoreBlob());
        var key = $"rgb-rate-probe:{store.Id}:{pricingCode}:{quote}:{fingerprint.GetHashCode(StringComparison.Ordinal)}";
        if (_cache.TryGetValue<RgbRateResult>(key, out var cached) && cached is not null)
            return cached;

        var result = await _rateSource.FetchAsync(pricingCode, quote, store, default);
        _cache.Set(key, result, TimeSpan.FromSeconds(60));
        return result;
    }

    [HttpPost("view-seed")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ViewSeed([FromRoute] string storeId, [FromForm] string password)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var userLock = _viewSeedLocks.GetOrAdd(user.Id, _ => new SemaphoreSlim(1, 1));
        if (!await userLock.WaitAsync(TimeSpan.FromSeconds(5)))
            return StatusCode(429, "Concurrent seed view attempt blocked. Try again.");
        try
        {
        var limiter = new ViewSeedRateLimiter(_cache);
        var authResult = await limiter.Evaluate(user.Id, password,
            pwd => _userManager.CheckPasswordAsync(user, pwd));
        switch (authResult)
        {
            case ViewSeedAuthResult.TooManyFailedAttempts:
                return StatusCode(429, "Too many failed attempts. Try again later.");
            case ViewSeedAuthResult.SeedViewLimitReached:
                return StatusCode(429, "Seed view limit reached. Try again later.");
            case ViewSeedAuthResult.InvalidPassword:
                return StatusCode(403, "Invalid password");
        }

        var wallet = await RequireWallet(storeId);
        if (wallet == null) return NotFound();

        try
        {
            var mnemonic = HttpContext.RequestServices
                .GetRequiredService<MnemonicProtectionService>()
                .Unprotect(wallet.EncryptedMnemonic);

            _events.Publish(new RgbSeedViewedEvent { UserId = user.Id, StoreId = storeId });
            _log.LogWarning("Seed phrase viewed for store {StoreId} by user {UserId}", storeId, user.Id);

            var words = mnemonic.Split(' ');
            var html = "<div class='alert alert-danger mb-3'><i class='fa fa-exclamation-triangle'></i> <strong>Never share this phrase. Anyone with these words can steal your funds.</strong></div>";
            html += "<div class='row g-2'>";
            for (int i = 0; i < words.Length; i++)
                html += $"<div class='col-4 col-md-3'><span class='text-muted me-1'>{i + 1}.</span>{System.Net.WebUtility.HtmlEncode(words[i])}</div>";
            html += "</div>";

            return Content(html, "text/html");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to decrypt seed for wallet {WalletId}", wallet.Id);
            return StatusCode(500, "Failed to decrypt seed phrase");
        }
        }
        finally { userLock.Release(); }
    }

    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection([FromRoute] string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            await _wallets.GetBtcBalanceAsync(wallet.Id);
            TempData["SuccessMessage"] = "Connected to RGB wallet";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Connection test failed for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = "Connection failed: "
                + RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
                    ex, RgbOperatorFacingFailure.EscalateToServerLogs);
        }

        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("auto-replenishment")]
    public async Task<IActionResult> SetAutomaticReplenishmentAuthorization([FromRoute] string storeId, bool grant)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (grant && !HasPersistedReplenishmentFigures(await _stores.FindStore(storeId)))
        {
            TempData["ErrorMessage"] =
                "Save this store's RGB payment settings first. Automatic colorable-UTXO creation cannot "
                + "be authorized until the UTXO count and size it would use are saved and in range, "
                + "because until then the authorization page cannot state what it would permit.";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        await _authorizations.RecordDecisionAsync(
            storeId,
            wallet.Id,
            grant ? RgbAutoReplenishmentDecision.Granted : RgbAutoReplenishmentDecision.Revoked,
            _userManager.GetUserId(User));

        TempData["SuccessMessage"] = grant
            ? "Automatic colorable-UTXO creation is now authorized for this store's current RGB wallet."
            : "Automatic colorable-UTXO creation is no longer authorized for this store.";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings([FromRoute] string storeId, RGBSettingsViewModel model)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (!ModelState.IsValid)
        {
            await PopulateSettingsViewModel(model, wallet, storeId, preferSubmitted: true);
            return View(nameof(Settings), model);
        }

        var store = await _stores.FindStore(storeId);
        if (store == null)
        {
            TempData["ErrorMessage"] = "Store not found";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var config = BuildSettingsConfig(wallet.Id, model);

        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);

        var hasDefaultAsset = !string.IsNullOrEmpty(config.DefaultAssetId);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, !hasDefaultAsset);
        store.SetStoreBlob(blob);
        await _stores.UpdateStore(store);

        TempData["SuccessMessage"] = "Settings saved";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

    // WHY extracted: SaveSettings reaches the concrete, non-virtual StoreRepository, so the action
    // itself cannot be exercised in a unit test. Pulling the initialiser out is what makes the
    // values it produces assertable.
    // The ternaries the previous version applied here were unreachable: ModelState.IsValid rejects
    // out-of-range input first, and the [Range] bounds are identical.
    internal static RGBPaymentMethodConfig BuildSettingsConfig(string walletId, RGBSettingsViewModel model)
        => new()
        {
            WalletId = walletId,
            DefaultAssetId = string.IsNullOrEmpty(model.DefaultAssetId) ? null : model.DefaultAssetId,
            UtxoCount = model.UtxoCount,
            UtxoSize = model.UtxoSize,
            MinConfirmations = model.MinConfirmations
        };

    async Task<RGBWallet?> RequireWallet(string storeId)
    {
        var w = await _wallets.GetWalletForStoreAsync(storeId);
        if (w == null) TempData["ErrorMessage"] = "Create an RGB wallet first";
        return w;
    }

    async Task<(BtcBalance, List<RgbAsset>, string?)> FetchWalletOverview(string walletId)
    {
        var balTask = _wallets.GetBtcBalanceAsync(walletId);
        var assetsTask = _wallets.ListAssetsAsync(walletId);
        var addrTask = _wallets.GetAddressAsync(walletId);
        await Task.WhenAll(balTask, assetsTask, addrTask);
        return (balTask.Result, assetsTask.Result, addrTask.Result);
    }

    bool ValidateMnemonic(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            ModelState.AddModelError("Mnemonic", "Recovery phrase is required");
            return false;
        }

        var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
        {
            ModelState.AddModelError("Mnemonic", "Recovery phrase must be 12, 15, 18, 21 or 24 words");
            return false;
        }

        try
        {
            _ = new Mnemonic(mnemonic.Trim(), NBitcoin.Wordlist.English);
        }
        catch
        {
            ModelState.AddModelError("Mnemonic", "Invalid BIP39 recovery phrase");
            return false;
        }

        return true;
    }

    void PopulateSetupModelAndDropRecoverySecrets(RGBSetupViewModel model)
    {
        model.AvailableNetworks = NetworkSettings.AvailableNetworks;
        model.AllNetworkSettings = BuildAllNetworkSettings();
        model.Mnemonic = null;
        model.BackupPassword = null;
        DropSubmittedValueButKeepValidationErrors(nameof(RGBSetupViewModel.Mnemonic));
        DropSubmittedValueButKeepValidationErrors(nameof(RGBSetupViewModel.BackupPassword));
    }

    void DropSubmittedValueButKeepValidationErrors(string fieldName)
    {
        if (!ModelState.TryGetValue(fieldName, out var entry) || entry == null)
            return;
        entry.RawValue = null;
        entry.AttemptedValue = null;
    }

    internal static bool ArePersistedReplenishmentFiguresValid(RGBPaymentMethodConfig? storedConfig)
        => storedConfig != null && RgbConfigBounds.ArePaymentMethodValuesValid(
            storedConfig.UtxoCount, storedConfig.UtxoSize, storedConfig.MinConfirmations);

    static bool HasPersistedReplenishmentFigures(StoreData? store)
        => ArePersistedReplenishmentFiguresValid(GetRgbConfig(store));

    static RGBPaymentMethodConfig? GetRgbConfig(StoreData? store)
    {
        if (store == null) return null;
        return store.GetPaymentMethodConfigs().TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)
            ? tok.ToObject<RGBPaymentMethodConfig>(_blobSerializer) : null;
    }

    static string TransferStatus(int s) => s switch {
        1 => "Waiting Counterparty", 2 => "Waiting Confirmations",
        3 => "Settled", 4 => "Failed", 5 => "Initiated", 6 => "Waiting Safe Height",
        _ => $"Unknown ({s})"
    };

    static string TransferKind(int k) => k switch {
        0 => "Issuance", 1 => "Receive Blind", 2 => "Receive Witness", 3 => "Send",
        _ => $"Unknown ({k})"
    };

    static string BtcTxType(int t) => t switch {
        0 => "User", 1 => "Create UTXOs", 2 => "RGB Send", 3 => "Drain",
        4 => "Incoming", 5 => "Send BTC",
        _ => $"Unknown ({t})"
    };

    internal static Task ValidateBackupFileHeader(IFormFile file) =>
        RgbBackupValidator.ValidateAsync(file);
}

public class RgbSeedViewedEvent
{
    public string UserId { get; set; } = "";
    public string StoreId { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

static class RgbAssetExtensions
{
    public static RGBAssetViewModel ToViewModel(this RgbAsset a) => new() {
        AssetId = a.AssetId, Ticker = a.Ticker, Name = a.Name,
        // ToViewModel has five call sites, two outside any try/catch, and RgbAsset.AssetId defaults
        // to "" — an unguarded For() would throw and 500 the Assets and Transfers pages.
        PricingCode = string.IsNullOrWhiteSpace(a.AssetId) ? "" : RgbPricingCode.For(a.AssetId),
        Precision = a.Precision, IssuedSupply = a.IssuedSupply, Balance = a.Balance,
        FutureBalance = a.FutureBalance, SpendableBalance = a.SpendableBalance
    };
}
