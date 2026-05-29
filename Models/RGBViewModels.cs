using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Models;

public abstract class StoreViewModel
{
    public string StoreId { get; set; } = "";
}

public class RGBSetupViewModel : StoreViewModel
{
    [Display(Name = "Wallet Name")]
    public string WalletName { get; set; } = "RGB Wallet";

    [Display(Name = "Network")]
    public string SelectedNetwork { get; set; } = "";

    public string[] AvailableNetworks { get; set; } = ["regtest", "testnet", "signet", "utexo", "mainnet"];

    public string ElectrumUrl { get; set; } = "";
    public string ProxyEndpoint { get; set; } = "";
    public string Network { get; set; } = "";

    public Dictionary<string, NetworkSettingsDto> AllNetworkSettings { get; set; } = new();

    [Display(Name = "Max Allocations per UTXO")]
    [Range(1, 50)]
    public int MaxAllocationsPerUtxo { get; set; } = 10;

    public bool IsRestore { get; set; }
    public bool IsBackupRestore { get; set; }

    [Display(Name = "Recovery Phrase")]
    public string? Mnemonic { get; set; }

    [Display(Name = "Backup File")]
    public IFormFile? BackupFile { get; set; }

    [Display(Name = "Backup Password")]
    public string? BackupPassword { get; set; }

    [Display(Name = "I understand and accept the custodial hot-wallet risk")]
    public bool AcknowledgesCustodialRisk { get; set; }
}

public class NetworkSettingsDto
{
    public string Electrum { get; set; } = "";
    public string Proxy { get; set; } = "";
}

public class RGBIndexViewModel : StoreViewModel
{
    public string WalletId { get; set; } = "";
    public string WalletName { get; set; } = "";
    public string? WalletAddress { get; set; }
    public long BtcBalance { get; set; }
    public long ColoredBalance { get; set; }
    public int ColorableUtxoCount { get; set; }
    public List<RGBAssetViewModel> Assets { get; set; } = [];
    public bool IsConnected { get; set; }
    public string? ConnectionError { get; set; }
    public bool PendingSync { get; set; }
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
    public long FutureBalance { get; set; }
    public long SpendableBalance { get; set; }
    public long PendingOutgoing => Balance > FutureBalance ? Balance - FutureBalance : 0;
    public long PendingIncoming => FutureBalance > Balance ? FutureBalance - Balance : 0;
}

public class RGBIssueAssetViewModel : StoreViewModel
{
    [Required, StringLength(8, MinimumLength = 2)]
    [RegularExpression(@"^[A-Za-z0-9]+$", ErrorMessage = "Ticker must contain only letters and numbers")]
    [Display(Name = "Ticker")]
    public string Ticker { get; set; } = "";

    [Required, StringLength(64, MinimumLength = 1)]
    [RegularExpression(@"^[A-Za-z0-9\s\-_\.]+$", ErrorMessage = "Name contains invalid characters")]
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
    public int MaxAllocationsPerUtxo { get; set; } = 10;
    public int PendingInvoices { get; set; }
}

public class RGBUtxoViewModel
{
    public string Outpoint { get; set; } = "";
    public long Amount { get; set; }
    public bool Colorable { get; set; }
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
    public string AssetTicker { get; set; } = "";
}

public class RGBBtcTransactionsViewModel : StoreViewModel
{
    public List<RGBBtcTransactionViewModel> Transactions { get; set; } = [];
}

public class RGBBtcTransactionViewModel
{
    public string Txid { get; set; } = "";
    public string Type { get; set; } = "";
    public long Received { get; set; }
    public long Sent { get; set; }
    public long Fee { get; set; }
    public long? Height { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}

public class RGBSendBtcViewModel : StoreViewModel
{
    [Required]
    [Display(Name = "Destination Address")]
    public string DestinationAddress { get; set; } = "";

    [Required]
    [Range(546, long.MaxValue, ErrorMessage = "Amount must be at least 546 sats (dust limit)")]
    [Display(Name = "Amount (sats)")]
    public long Amount { get; set; }

    [Required]
    [Range(1, 1000, ErrorMessage = "Fee rate must be between 1 and 1000 sat/vB")]
    [Display(Name = "Fee Rate (sat/vB)")]
    public float FeeRate { get; set; } = 2.0f;

    public long VanillaBalance { get; set; }
    public long ColoredBalance { get; set; }
    public int VanillaUtxoCount { get; set; }
}

public class RGBSendAssetViewModel : StoreViewModel
{
    [Required]
    [Display(Name = "Asset")]
    public string AssetId { get; set; } = "";

    [Required]
    [Display(Name = "RGB Invoice")]
    public string RgbInvoice { get; set; } = "";

    [Required]
    [Range(1, long.MaxValue, ErrorMessage = "Amount must be at least 1")]
    [Display(Name = "Amount")]
    public long Amount { get; set; }

    [Required]
    [Range(1, 1000, ErrorMessage = "Fee rate must be between 1 and 1000 sat/vB")]
    [Display(Name = "Fee Rate (sat/vB)")]
    public float FeeRate { get; set; } = 2.0f;

    public List<RGBAssetViewModel> AvailableAssets { get; set; } = [];
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
    public List<RGBAssetViewModel> AvailableAssets { get; set; } = [];
    public string ElectrumUrl { get; set; } = "";
    public bool IsConnected { get; set; }
    public string? ConnectionError { get; set; }

    [Display(Name = "UTXO Count")]
    [Range(1, 20)]
    public int UtxoCount { get; set; } = 4;

    [Display(Name = "UTXO Size (sats)")]
    [Range(546, 100000)]
    public int UtxoSize { get; set; } = 1000;

    [Display(Name = "Max Allocations per UTXO")]
    [Range(1, 50)]
    public int MaxAllocationsPerUtxo { get; set; } = 10;

    [Display(Name = "Min Confirmations")]
    [Range(1, 100)]
    public int MinConfirmations { get; set; } = 1;

    public bool AllowOneToOneRateFallback { get; set; }
}
