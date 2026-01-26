using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[AutoValidateAntiforgeryToken]
[Route("stores/{storeId}/rgb")]
public class RGBController : Controller
{
    readonly RGBWalletService _wallets;
    readonly StoreRepository _stores;
    readonly PaymentMethodHandlerDictionary _handlers;
    readonly ILogger<RGBController> _log;

    public RGBController(RGBWalletService wallets, StoreRepository stores,
        PaymentMethodHandlerDictionary handlers, ILogger<RGBController> log)
    {
        _wallets = wallets; _stores = stores; _handlers = handlers; _log = log;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string storeId)
    {
        var wallet = await _wallets.GetWalletForStoreAsync(storeId);
        if (wallet == null)
        {
            var rgbConfig = HttpContext.RequestServices.GetService<RGBConfiguration>();
            return View("Setup", new RGBSetupViewModel 
            { 
                StoreId = storeId,
                ElectrumUrl = rgbConfig?.ElectrumUrl ?? "N/A",
                Network = rgbConfig?.Network ?? "unknown"
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
            var (balance, assets, address) = await FetchWalletOverview(wallet.Id);

            vm.BtcBalance = balance.Vanilla.Spendable + balance.Colored.Spendable;
            vm.ColoredBalance = balance.Colored.Spendable;
            vm.Assets = assets.Select(a => a.ToViewModel()).ToList();
            vm.WalletAddress = address;
            vm.IsConnected = true;
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
        var rgbConfig = HttpContext.RequestServices.GetService<RGBConfiguration>();
        return View(new RGBSetupViewModel 
        { 
            StoreId = storeId,
            ElectrumUrl = rgbConfig?.ElectrumUrl ?? "N/A",
            Network = rgbConfig?.Network ?? "unknown"
        });
    }

    [HttpPost("setup")]
    public async Task<IActionResult> SetupWallet(string storeId, RGBSetupViewModel model)
    {
        if (!ModelState.IsValid)
            return View("Setup", model);

        try
        {
            var wallet = await _wallets.CreateWalletAsync(storeId, model.WalletName);
            await EnableRgbPaymentMethod(storeId, wallet.Id);

            TempData["SuccessMessage"] = "RGB wallet created!";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View("Setup", model);
        }
    }

    [HttpPost("enable")]
    public async Task<IActionResult> EnablePaymentMethod(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            await EnableRgbPaymentMethod(storeId, wallet.Id);
            TempData["SuccessMessage"] = "RGB payments enabled";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Index), new { storeId });
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

        return View(new RGBUtxosViewModel
        {
            StoreId = storeId,
            Utxos = unspents.Select(u => new RGBUtxoViewModel
            {
                Outpoint = $"{u.Utxo.Outpoint.Txid}:{u.Utxo.Outpoint.Vout}",
                Amount = u.Utxo.BtcAmount,
                Colorable = u.Utxo.Colorable,
                HasAllocations = u.RgbAllocations.Count > 0,
                Allocations = u.RgbAllocations.Select(a => new RGBAllocationViewModel
                {
                    AssetId = a.AssetId, Amount = a.Amount, Settled = a.Settled
                }).ToList()
            }).ToList()
        });
    }

    [HttpPost("utxos/create")]
    public async Task<IActionResult> CreateUtxos(string storeId, int count = 5)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        try
        {
            var created = await _wallets.CreateColorableUtxosAsync(wallet.Id, count);
            TempData["SuccessMessage"] = created > 0 ? $"{created} UTXOs created" : "UTXOs already available";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(Utxos), new { storeId });
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> Transfers(string storeId, string? assetId = null)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var assets = await _wallets.ListAssetsAsync(wallet.Id);
        var selectedAsset = assetId ?? assets.FirstOrDefault()?.AssetId;

        var transfers = string.IsNullOrEmpty(selectedAsset)
            ? new List<RgbTransfer>()
            : await _wallets.GetTransfersAsync(wallet.Id, selectedAsset);

        return View(new RGBTransfersViewModel
        {
            StoreId = storeId,
            SelectedAssetId = selectedAsset,
            Assets = assets.Select(a => a.ToViewModel()).ToList(),
            Transfers = transfers
                .OrderByDescending(t => t.Idx)
                .Select(t => new RGBTransferViewModel
                {
                    Idx = t.Idx,
                    Status = TransferStatus(t.Status),
                    Kind = TransferKind(t.Kind),
                    Amount = t.Amount,
                    Txid = t.Txid,
                    RecipientId = t.RecipientId
                }).ToList()
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

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(string storeId)
    {
        var wallet = await RequireWallet(storeId);
        if (wallet == null) return RedirectToAction(nameof(Setup), new { storeId });

        var store = await _stores.FindStore(storeId);
        var config = GetRgbConfig(store);
        var rgbConfig = HttpContext.RequestServices.GetService<RGBConfiguration>();

        var vm = new RGBSettingsViewModel
        {
            StoreId = storeId,
            WalletId = wallet.Id,
            WalletName = wallet.Name,
            XpubVanilla = wallet.XpubVanilla,
            XpubColored = wallet.XpubColored,
            MasterFingerprint = wallet.MasterFingerprint,
            Network = wallet.Network,
            CreatedAt = wallet.CreatedAt,
            DefaultAssetId = config?.DefaultAssetId,
            AcceptAnyAsset = config?.AcceptAnyAsset ?? false,
            ElectrumUrl = rgbConfig?.ElectrumUrl ?? "N/A"
        };

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

        return View(vm);
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
            AcceptAnyAsset = model.AcceptAnyAsset
        };

        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], config);
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

    async Task EnableRgbPaymentMethod(string storeId, string walletId)
    {
        var store = await _stores.FindStore(storeId) ?? throw new InvalidOperationException("Store not found");
        store.SetPaymentMethodConfig(_handlers[RGBPlugin.RGBPaymentMethodId], new RGBPaymentMethodConfig { WalletId = walletId });
        var blob = store.GetStoreBlob();
        blob.SetExcluded(RGBPlugin.RGBPaymentMethodId, false);
        store.SetStoreBlob(blob);
        await _stores.UpdateStore(store);
    }

    static RGBPaymentMethodConfig? GetRgbConfig(StoreData? store)
    {
        if (store == null) return null;
        return store.GetPaymentMethodConfigs().TryGetValue(RGBPlugin.RGBPaymentMethodId, out var tok)
            ? tok.ToObject<RGBPaymentMethodConfig>() : null;
    }

    static string TransferStatus(int s) => s switch {
        0 => "Waiting Counterparty", 1 => "Waiting Confirmations", 2 => "Settled", 3 => "Failed",
        _ => $"Unknown ({s})"
    };

    static string TransferKind(int k) => k switch {
        0 => "Issuance", 1 => "Receive Blind", 2 => "Receive Witness", 3 => "Send",
        _ => $"Unknown ({k})"
    };
}

static class RgbAssetExtensions
{
    public static RGBAssetViewModel ToViewModel(this RgbAsset a) => new() {
        AssetId = a.AssetId, Ticker = a.Ticker, Name = a.Name,
        Precision = a.Precision, IssuedSupply = a.IssuedSupply
    };
}
