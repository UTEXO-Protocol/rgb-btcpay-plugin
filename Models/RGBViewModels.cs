using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace BTCPayServer.Plugins.RgbUtexo.Models;

public abstract class StoreViewModel
{
    [BindNever]
    [ValidateNever]
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
    [Range(RgbConfigBounds.AllocationsPerUtxoMin, RgbConfigBounds.AllocationsPerUtxoMax)]
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
    public List<RGBPendingBlindReceiveRow>? PendingBlindReceives { get; set; }
}

public class RGBAssetsViewModel : StoreViewModel
{
    public List<RGBAssetViewModel> Assets { get; set; } = [];
}

public class RGBAssetViewModel
{
    public const int ContractIdHeadCharsShown = 12;
    public const int ContractIdTailCharsShown = 8;
    public const string ContractIdElidedMiddleMarker = "…";
    const int LongestContractIdShownWhole =
        ContractIdHeadCharsShown + ContractIdTailCharsShown + 1;

    public string AssetId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Name { get; set; } = "";
    public int Precision { get; set; }
    public ulong IssuedSupply { get; set; }
    public ulong Balance { get; set; }
    public ulong FutureBalance { get; set; }
    public ulong SpendableBalance { get; set; }
    public string PricingCode { get; set; } = "";
    public ulong PendingOutgoing => Balance > FutureBalance ? Balance - FutureBalance : 0;
    public ulong PendingIncoming => FutureBalance > Balance ? FutureBalance - Balance : 0;

    public static string AbbreviateContractIdKeepingHeadAndTail(string? assetId)
    {
        var contractId = assetId ?? "";
        return contractId.Length <= LongestContractIdShownWhole
            ? contractId
            : contractId[..ContractIdHeadCharsShown]
              + ContractIdElidedMiddleMarker
              + contractId[^ContractIdTailCharsShown..];
    }

    public string AssetIdAbbreviatedKeepingHeadAndTail =>
        AbbreviateContractIdKeepingHeadAndTail(AssetId);
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
    public ulong Amount { get; set; }
    public bool Settled { get; set; }

    public string AssetIdAbbreviatedKeepingHeadAndTail =>
        RGBAssetViewModel.AbbreviateContractIdKeepingHeadAndTail(AssetId);
}

public class RGBTransfersViewModel : StoreViewModel
{
    public string? SelectedAssetId { get; set; }
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
    public long PendingVanillaBalance { get; set; }
    public long ColoredBalance { get; set; }
    public int VanillaUtxoCount { get; set; }

    // WHY: the balance fields default to 0, which a merchant reads as "no funds" rather than
    // "lookup failed". The view needs to tell those two apart.
    public bool BalanceUnavailable { get; set; }
}

public class RGBSendAssetViewModel : StoreViewModel
{
    [Required]
    [Display(Name = "Asset")]
    public string AssetId { get; set; } = "";

    [Required]
    [StringLength(TransportEndpointValidator.MaxRgbInvoiceLength)]
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
    [BindNever]
    [ValidateNever]
    public BtcBalance? DeleteBalance { get; set; }

    [Display(Name = "UTXO Count")]
    [Range(RgbConfigBounds.UtxoCountMin, RgbConfigBounds.UtxoCountMax)]
    public int UtxoCount { get; set; } = 4;

    [Display(Name = "UTXO Size (sats)")]
    [Range(RgbConfigBounds.UtxoSizeMin, RgbConfigBounds.UtxoSizeMax)]
    public int UtxoSize { get; set; } = 1000;

    [Display(Name = "Max Allocations per UTXO")]
    public int MaxAllocationsPerUtxo { get; set; } = 10;

    [Display(Name = "Min Confirmations")]
    [Range(RgbConfigBounds.MinConfirmationsMin, RgbConfigBounds.MinConfirmationsMax)]
    public int MinConfirmations { get; set; } = 1;

    public string? PricingCode { get; set; }
    public string? SuggestedRateRule { get; set; }
    public string? SuggestedPegRule { get; set; }
    public string? QuoteCurrency { get; set; }
    public bool RateRuleMissing { get; set; }
    public bool UsesDefaultRules { get; set; }
    public bool RateUnresolved { get; set; }

    public bool AutomaticReplenishmentGranted { get; set; }
    public RgbAutoReplenishmentDecision AutomaticReplenishmentDecision { get; set; }
    public DateTimeOffset? AutomaticReplenishmentDecidedAt { get; set; }
    public string? AutomaticReplenishmentDecidedBy { get; set; }
    public RgbReplenishmentNoticeCause ReplenishmentNoticeCause { get; set; }
    public string ReplenishmentNoticeMessage { get; set; } = "";
    public bool ReplenishmentNoticeInvitesGrant { get; set; }
    public int MaxAutoColorableUtxos { get; set; }
    public int? PersistedUtxoCount { get; set; }
    public int? PersistedUtxoSize { get; set; }
    public long WorstCaseReplenishFeeBaseSats { get; set; }
    public long WorstCaseReplenishFeePerVanillaUtxoSats { get; set; }

    public long? MaxAutoColorablePrincipalSats =>
        PersistedUtxoSize.HasValue ? (long)MaxAutoColorableUtxos * PersistedUtxoSize.Value : null;

    public long MaxAutoColorablePrincipalCeilingSats =>
        (long)MaxAutoColorableUtxos * RgbConfigBounds.UtxoSizeMax;

    public RgbVanillaReservationState VanillaReservationState { get; set; } = RgbVanillaReservationState.Clean;
    public int VanillaReservationCount { get; set; }
    public int VanillaReservationStillUnspentCount { get; set; }
    public bool StoreArchived { get; set; }
}

public class RGBBlindReceiveViewModel : StoreViewModel
{
    public string WalletId { get; set; } = "";
    public string InvoiceId { get; set; } = "";
    public string RgbInvoiceString { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "Waiting";
    public string? ReceivedAssetId { get; set; }
    public long? ReceivedAmount { get; set; }
}

public class RGBPendingBlindReceiveRow
{
    public string InvoiceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "";
}
