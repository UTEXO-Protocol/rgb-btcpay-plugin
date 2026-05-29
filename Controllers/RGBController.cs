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
[Route("stores/{storeId}/rgb")]
public class RGBController : Controller
{
    static readonly Newtonsoft.Json.JsonSerializer _blobSerializer = BlobSerializer.CreateSerializer().Serializer;
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

    public RGBController(IRGBWalletService wallets, StoreRepository stores,
        PaymentMethodHandlerDictionary handlers, RGBPluginDbContextFactory db, ILogger<RGBController> log,
        UserManager<ApplicationUser> userManager, EventAggregator events, IMemoryCache cache,
        IOptions<BTCPayServerOptions> btcPayOptions)
    {
        _wallets = wallets; _stores = stores; _handlers = handlers; _db = db; _log = log;
        _userManager = userManager; _events = events; _cache = cache;
        _btcPayOptions = btcPayOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string storeId, bool sync = false)
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
        }
        catch (Exception ex)
        {
            vm.IsConnected = false;
            vm.ConnectionError = ex.Message;
        }

        return View(vm);
    }

    [HttpGet("setup")]
    public IActionResult Setup(string storeId)
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
    public async Task<IActionResult> SetupWallet(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }

        if (!ModelState.IsValid)
        {
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.CreateWalletAsync(storeId, model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id, MaxAllocationsPerUtxo = maxAlloc };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet created on {model.SelectedNetwork} with max {maxAlloc} allocations per UTXO!";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.AvailableNetworks = NetworkSettings.AvailableNetworks;
            return View("Setup", model);
        }
    }

    [HttpPost("restore")]
    public async Task<IActionResult> RestoreWallet(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        try
        {
            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.RestoreWalletAsync(storeId, model.Mnemonic!.Trim(), model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id, MaxAllocationsPerUtxo = maxAlloc };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet restored on {model.SelectedNetwork}!";
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            model.IsRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }
    }

    [HttpPost("restore-backup")]
    [RequestSizeLimit(5_242_880)]
    public async Task<IActionResult> RestoreFromBackup(string storeId, RGBSetupViewModel model)
    {
        if (await _wallets.GetWalletForStoreAsync(storeId) != null)
            return RedirectToAction(nameof(Index), new { storeId });

        if (!model.AcknowledgesCustodialRisk)
        {
            TempData[WellKnownTempData.ErrorMessage] =
                "You must acknowledge the custodial hot-wallet risk to create a wallet.";
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (!ValidateMnemonic(model.Mnemonic))
        {
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (model.BackupFile == null || model.BackupFile.Length == 0)
        {
            ModelState.AddModelError("BackupFile", "Backup file is required");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        try
        {
            await ValidateBackupFileHeader(model.BackupFile);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError("BackupFile", ex.Message);
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        if (string.IsNullOrWhiteSpace(model.BackupPassword))
        {
            ModelState.AddModelError("BackupPassword", "Backup password is required");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        var networkError = ValidateSelectedNetwork(model.SelectedNetwork, _btcPayOptions.NetworkType);
        if (networkError != null)
        {
            TempData[WellKnownTempData.ErrorMessage] = networkError;
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"rgb-restore-{Guid.NewGuid():N}.rgb");
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
            {
                await model.BackupFile.CopyToAsync(stream);
            }

            var maxAlloc = model.MaxAllocationsPerUtxo > 0 ? model.MaxAllocationsPerUtxo : 10;
            var wallet = await _wallets.RestoreFromBackupAsync(
                storeId, model.Mnemonic!.Trim(), tempPath, model.BackupPassword,
                model.SelectedNetwork, model.WalletName, maxAlloc);

            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                var config = new RGBPaymentMethodConfig { WalletId = wallet.Id, MaxAllocationsPerUtxo = maxAlloc };
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            TempData["SuccessMessage"] = $"RGB wallet restored from backup on {model.SelectedNetwork}!";
            return RedirectToAction(nameof(Index), new { storeId, sync = true });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", $"Restore failed: {ex.Message}");
            model.IsBackupRestore = true;
            PopulateSetupModel(model);
            return View("Setup", model);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    [HttpGet("assets")]
    public async Task<IActionResult> Assets(string storeId)
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
    public async Task<IActionResult> IssueAsset(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        return View(new RGBIssueAssetViewModel { StoreId = storeId });
    }

    [HttpPost("assets/issue")]
    public async Task<IActionResult> IssueAsset(string storeId, RGBIssueAssetViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var asset = await _wallets.IssueAssetAsync(wallet.Id, model.Ticker, model.Name, model.Amount, model.Precision);
            TempData["SuccessMessage"] = $"Issued {asset.Ticker} ({asset.AssetId[..20]}...)";
            return RedirectToAction(nameof(Assets), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(model);
        }
    }

    [HttpGet("utxos")]
    public async Task<IActionResult> Utxos(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var unspents = await _wallets.ListUnspentsAsync(wallet.Id);
        await using var ctx = _db.CreateContext();
        var pendingInvoices = await ctx.RGBInvoices.CountAsync(
            i => i.WalletId == wallet.Id && i.Status == RGBInvoiceStatus.Pending);

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
    public async Task<IActionResult> CreateUtxos(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var count = config?.UtxoCount ?? 4;
        var size = config?.UtxoSize ?? 1000;

        try
        {
            var created = await _wallets.CreateColorableUtxosAsync(wallet.Id, count, size);
            TempData["SuccessMessage"] = created > 0 ? $"{created} UTXOs created ({size} sats each)" : "UTXOs already available";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Utxos), new { storeId });
    }

    [HttpGet("send-btc")]
    public async Task<IActionResult> SendBtc(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var vm = new RGBSendBtcViewModel { StoreId = storeId };
        try
        {
            var balance = await _wallets.GetBtcBalanceAsync(wallet.Id);
            var unspents = await _wallets.ListUnspentsAsync(wallet.Id);
            vm.VanillaBalance = balance.Vanilla.Spendable;
            vm.ColoredBalance = balance.Colored.Spendable;
            vm.VanillaUtxoCount = unspents.Count(u => !u.Utxo.Colorable);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to load wallet data: {ex.Message}";
        }

        return View(vm);
    }

    [HttpPost("send-btc")]
    public async Task<IActionResult> SendBtc(string storeId, RGBSendBtcViewModel model)
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
            ModelState.AddModelError("", ex.Message);
            await PopulateSendBtcBalance(wallet, model);
            return View(model);
        }
    }

    async Task PopulateSendBtcBalance(Data.Entities.RGBWallet wallet, RGBSendBtcViewModel model)
    {
        try
        {
            var balance = await _wallets.GetBtcBalanceAsync(wallet.Id);
            var unspents = await _wallets.ListUnspentsAsync(wallet.Id);
            model.VanillaBalance = balance.Vanilla.Spendable;
            model.ColoredBalance = balance.Colored.Spendable;
            model.VanillaUtxoCount = unspents.Count(u => !u.Utxo.Colorable);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to populate balance for send form"); }
    }

    [HttpGet("send-asset")]
    public async Task<IActionResult> SendAsset(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var vm = new RGBSendAssetViewModel { StoreId = storeId };
        await PopulateSendAssetData(wallet, vm);
        return View(vm);
    }

    [HttpPost("send-asset")]
    public async Task<IActionResult> SendAsset(string storeId, RGBSendAssetViewModel model)
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
            var msg = $"Sent {result.AmountSent:N0} {result.AssetTicker} — Txid: {result.Txid}";
            if (result.BroadcastWarning != null)
                TempData[WellKnownTempData.ErrorMessage] = $"{msg}. Warning: {result.BroadcastWarning}";
            else
                TempData[WellKnownTempData.SuccessMessage] = msg;
            return RedirectToAction(nameof(Transfers), new { storeId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send RGB asset");
            ModelState.AddModelError("", ex is InvalidOperationException or KeyNotFoundException ? ex.Message : "Failed to send asset. Check server logs for details.");
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
    public async Task<IActionResult> BtcTransactions(string storeId)
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
            TempData["ErrorMessage"] = $"Failed to load transactions: {ex.Message}";
            return RedirectToAction(nameof(Index), new { storeId });
        }
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> Transfers(string storeId, string? assetId = null)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var assets = await _wallets.ListAssetsAsync(wallet.Id);
        var assetLookup = assets.ToDictionary(a => a.AssetId, a => a.Ticker);

        var allTransfers = new List<RGBTransferViewModel>();
        foreach (var asset in assets)
        {
            var transfers = await _wallets.GetTransfersAsync(wallet.Id, asset.AssetId);
            allTransfers.AddRange(transfers.Select(t => new RGBTransferViewModel
            {
                Idx = t.Idx,
                Status = TransferStatus(t.Status),
                Kind = TransferKind(t.Kind),
                Amount = t.Amount,
                Txid = t.Txid,
                RecipientId = t.RecipientId,
                AssetTicker = asset.Ticker
            }));
        }

        return View(new RGBTransfersViewModel
        {
            StoreId = storeId,
            SelectedAssetId = assetId,
            Assets = assets.Select(a => a.ToViewModel()).ToList(),
            Transfers = allTransfers.OrderByDescending(t => t.Idx).ToList()
        });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(string storeId)
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
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteWallet(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var store = await _stores.FindStore(storeId);
            if (store != null)
            {
                store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], null);
                var blob = store.GetStoreBlob();
                blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, true);
                store.SetStoreBlob(blob);
                await _stores.UpdateStore(store);
            }

            await _wallets.DeleteWalletAsync(wallet.Id);
            TempData["SuccessMessage"] = "RGB wallet deleted";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Failed to delete wallet: {ex.Message}";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("backup")]
    public async Task<IActionResult> BackupWallet(string storeId, string password)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            TempData["ErrorMessage"] = "Password must be at least 8 characters";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        string? tempPath = null;
        try
        {
            tempPath = await _wallets.BackupWalletAsync(wallet.Id, password);
            var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.DeleteOnClose);
            return File(stream, "application/octet-stream", $"rgb-wallet-backup-{DateTime.UtcNow:yyyyMMdd}.rgb");
        }
        catch (Exception ex)
        {
            if (tempPath != null && System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
            _log.LogError(ex, "Backup failed for wallet {WalletId}", wallet.Id);
            TempData["ErrorMessage"] = $"Backup failed: {ex.Message}";
            return RedirectToAction(nameof(Settings), new { storeId });
        }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string storeId)
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
            MaxAllocationsPerUtxo = config?.MaxAllocationsPerUtxo ?? 10,
            MinConfirmations = config?.MinConfirmations ?? 1,
            AllowOneToOneRateFallback = config?.AllowOneToOneRateFallback ?? false
        };
        await PopulateSettingsViewModel(vm, wallet, storeId);
        return View(vm);
    }

    async Task PopulateSettingsViewModel(RGBSettingsViewModel vm, Data.Entities.RGBWallet wallet, string storeId)
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

        try
        {
            var assets = await _wallets.ListAssetsAsync(wallet.Id);
            vm.AvailableAssets = assets.Select(a => a.ToViewModel()).ToList();
            vm.IsConnected = true;
        }
        catch (Exception ex)
        {
            vm.ConnectionError = ex.Message;
            _log.LogWarning(ex, "RGB wallet connection failed");
        }
    }

    [HttpPost("view-seed")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ViewSeed(string storeId, [FromForm] string password)
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
    public async Task<IActionResult> TestConnection(string storeId)
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
            TempData["ErrorMessage"] = $"Connection failed: {ex.Message}";
        }

        return RedirectToAction(nameof(Settings), new { storeId });
    }

    [HttpPost("settings")]
    public async Task<IActionResult> SaveSettings(string storeId, RGBSettingsViewModel model)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        if (!ModelState.IsValid)
        {
            await PopulateSettingsViewModel(model, wallet, storeId);
            return View(nameof(Settings), model);
        }

        var store = await _stores.FindStore(storeId);
        if (store == null)
        {
            TempData["ErrorMessage"] = "Store not found";
            return RedirectToAction(nameof(Settings), new { storeId });
        }

        var config = new RGBPaymentMethodConfig
        {
            WalletId = wallet.Id,
            DefaultAssetId = string.IsNullOrEmpty(model.DefaultAssetId) ? null : model.DefaultAssetId,
            UtxoCount = model.UtxoCount is > 0 and <= 20 ? model.UtxoCount : 4,
            UtxoSize = model.UtxoSize is >= 546 and <= 100000 ? model.UtxoSize : 1000,
            MaxAllocationsPerUtxo = model.MaxAllocationsPerUtxo is > 0 and <= 50 ? model.MaxAllocationsPerUtxo : 10,
            MinConfirmations = model.MinConfirmations is >= 1 and <= 100 ? model.MinConfirmations : 1,
            AllowOneToOneRateFallback = model.AllowOneToOneRateFallback
        };

        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);

        var hasDefaultAsset = !string.IsNullOrEmpty(config.DefaultAssetId);
        var blob = store.GetStoreBlob();
        blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, !hasDefaultAsset);
        store.SetStoreBlob(blob);
        await _stores.UpdateStore(store);

        TempData["SuccessMessage"] = "Settings saved";
        return RedirectToAction(nameof(Settings), new { storeId });
    }

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

    void PopulateSetupModel(RGBSetupViewModel model)
    {
        model.AvailableNetworks = NetworkSettings.AvailableNetworks;
        model.AllNetworkSettings = BuildAllNetworkSettings();
    }

    static RGBPaymentMethodConfig? GetRgbConfig(StoreData? store)
    {
        if (store == null) return null;
        return store.GetPaymentMethodConfigs().TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)
            ? tok.ToObject<RGBPaymentMethodConfig>(_blobSerializer) : null;
    }

    static string TransferStatus(int s) => s switch {
        0 => "Waiting Counterparty", 1 => "Waiting Confirmations", 2 => "Waiting Confirmations",
        3 => "Settled", 4 => "Failed",
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
        Precision = a.Precision, IssuedSupply = a.IssuedSupply, Balance = a.Balance,
        FutureBalance = a.FutureBalance, SpendableBalance = a.SpendableBalance
    };
}
