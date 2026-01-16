using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.RGB.Models;

public abstract class StoreViewModel
{
    public string StoreId { get; set; } = "";
}

public class RGBSetupViewModel : StoreViewModel
{
    [Display(Name = "Wallet Name")]
    public string WalletName { get; set; } = "RGB Wallet";
}

public class RGBIndexViewModel : StoreViewModel
{
    public string WalletId { get; set; } = "";
    public string WalletName { get; set; } = "";
    public long BtcBalance { get; set; }
    public long ColoredBalance { get; set; }
    public int ColorableUtxoCount { get; set; }
    public List<RGBAssetViewModel> Assets { get; set; } = [];
    public bool IsConnected { get; set; }
    public string? ConnectionError { get; set; }
}

public class RGBAssetsViewModel : StoreViewModel
{
    public List<RGBAssetViewModel> Assets { get; set; } = [];
}

public class RGBAssetViewModel
{
    public string AssetId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public int Precision { get; set; }
    public long IssuedSupply { get; set; }
    public long Balance { get; set; }
}

public class RGBIssueAssetViewModel : StoreViewModel
{
    [Required, StringLength(8, MinimumLength = 2)]
    [Display(Name = "Ticker")]
    public string Ticker { get; set; } = "";
    
    [Required, StringLength(64, MinimumLength = 1)]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";
    
    [Required, Range(1, long.MaxValue)]
    [Display(Name = "Amount")]
    public long Amount { get; set; } = 1000;
    
    [Range(0, 18)]
    [Display(Name = "Precision")]
    public int Precision { get; set; }
}

public class RGBUtxosViewModel : StoreViewModel
{
    public List<RGBUtxoViewModel> Utxos { get; set; } = [];
}

public class RGBUtxoViewModel
{
    public string Outpoint { get; set; } = "";
    public long Amount { get; set; }
    public bool Colorable { get; set; }
    public bool HasAllocations { get; set; }
    public List<RGBAllocationViewModel> Allocations { get; set; } = [];
}

public class RGBAllocationViewModel
{
    public string AssetId { get; set; } = "";
    public long Amount { get; set; }
    public bool Settled { get; set; }
}

public class RGBTransfersViewModel : StoreViewModel
{
    public string? SelectedAssetId { get; set; }
    public List<RGBAssetViewModel> Assets { get; set; } = [];
    public List<RGBTransferViewModel> Transfers { get; set; } = [];
}

public class RGBTransferViewModel
{
    public int Idx { get; set; }
    public string Status { get; set; } = "";
    public string Kind { get; set; } = "";
    public long Amount { get; set; }
    public string? Txid { get; set; }
    public string? RecipientId { get; set; }
}

public class RGBSettingsViewModel : StoreViewModel
{
    public string WalletId { get; set; } = "";
    public string WalletName { get; set; } = "";
    public string XpubVanilla { get; set; } = "";
    public string XpubColored { get; set; } = "";
    public string MasterFingerprint { get; set; } = "";
    public string Network { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? DefaultAssetId { get; set; }
    public bool AcceptAnyAsset { get; set; }
    public List<RGBAssetViewModel> AvailableAssets { get; set; } = [];
    public string RgbNodeUrl { get; set; } = "";
    public bool IsConnected { get; set; }
    public string? ConnectionError { get; set; }
}
