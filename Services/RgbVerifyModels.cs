using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbDecodeInvoiceResult
{
    [JsonPropertyName("contractId")] public string ContractId { get; set; } = "";
    [JsonPropertyName("amountKind")] public string AmountKind { get; set; } = "";
    [JsonPropertyName("amount")] public ulong? Amount { get; set; }
    [JsonPropertyName("recipientSeal")] public string RecipientSeal { get; set; } = "";
    [JsonPropertyName("recipientChainNet")] public string RecipientChainNet { get; set; } = "";
    [JsonPropertyName("expiry")] public long? Expiry { get; set; }
    [JsonPropertyName("transports")] public List<string> Transports { get; set; } = [];
}

public class RgbValidateResult
{
    [JsonPropertyName("contractId")] public string ContractId { get; set; } = "";
    [JsonPropertyName("chainNet")] public string ChainNet { get; set; } = "";
    [JsonPropertyName("witnessTxid")] public string WitnessTxid { get; set; } = "";
    [JsonPropertyName("prevouts")] public List<string> Prevouts { get; set; } = [];
    [JsonPropertyName("legs")] public List<RgbLeg> Legs { get; set; } = [];
    [JsonPropertyName("inputsAccounted")] public bool InputsAccounted { get; set; }
    [JsonPropertyName("inputs")] public List<RgbObservedInput> Inputs { get; set; } = [];
}

public class RgbObservedInput
{
    [JsonPropertyName("outpoint")] public string Outpoint { get; set; } = "";
    [JsonPropertyName("observed")] public List<RgbObservedAllocation> Observed { get; set; } = [];
}

public class RgbObservedAllocation
{
    [JsonPropertyName("contractId")] public string ContractId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("amount")] public ulong? Amount { get; set; }
    [JsonPropertyName("accounted")] public bool Accounted { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public class RgbLeg
{
    [JsonPropertyName("assignmentType")] public int AssignmentType { get; set; }
    [JsonPropertyName("sealKind")] public string SealKind { get; set; } = "";
    [JsonPropertyName("sealBytes")] public string? SealBytes { get; set; }
    [JsonPropertyName("witnessVout")] public uint? WitnessVout { get; set; }
    [JsonPropertyName("outpoint")] public string? Outpoint { get; set; }
    [JsonPropertyName("derivationPath")] public string? DerivationPath { get; set; }
    [JsonPropertyName("amount")] public ulong Amount { get; set; }
}

public class RgbCommitmentCheckResult
{
    [JsonPropertyName("matches")] public bool Matches { get; set; }
    [JsonPropertyName("witnessIdMatches")] public bool WitnessIdMatches { get; set; }
    [JsonPropertyName("committedContractIds")] public List<string> CommittedContractIds { get; set; } = [];
}

public class RgbValidateV2Request
{
    [JsonPropertyName("consignmentPath")] public string ConsignmentPath { get; set; } = "";
    [JsonPropertyName("fasciaPath")] public string FasciaPath { get; set; } = "";
    [JsonPropertyName("unsignedTxid")] public string UnsignedTxid { get; set; } = "";
    [JsonPropertyName("opretCommitmentBytes")] public string OpretCommitmentBytes { get; set; } = "";
    [JsonPropertyName("entropy")] public ulong Entropy { get; set; }
    [JsonPropertyName("indexerUrl")] public string IndexerUrl { get; set; } = "";
    [JsonPropertyName("network")] public string Network { get; set; } = "";
    [JsonPropertyName("stockDir")] public string StockDir { get; set; } = "";
    [JsonPropertyName("bdkStorePath")] public string BdkStorePath { get; set; } = "";
    [JsonPropertyName("accountXpubVanilla")] public string AccountXpubVanilla { get; set; } = "";
    [JsonPropertyName("accountXpubColored")] public string AccountXpubColored { get; set; } = "";
    [JsonPropertyName("masterFingerprint")] public string MasterFingerprint { get; set; } = "";
}

public class RgbValidateV2Result : RgbValidateResult
{
    [JsonPropertyName("validationVersion")] public int ValidationVersion { get; set; }
    [JsonPropertyName("commitmentMatches")] public bool CommitmentMatches { get; set; }
    [JsonPropertyName("witnessIdMatches")] public bool WitnessIdMatches { get; set; }
    [JsonPropertyName("committedContractIds")] public List<string> CommittedContractIds { get; set; } = [];
    [JsonPropertyName("verifiedContractIds")] public List<string> VerifiedContractIds { get; set; } = [];
    [JsonPropertyName("mainTransitionId")] public string MainTransitionId { get; set; } = "";
    [JsonPropertyName("verifiedTransitionIds")] public List<string> VerifiedTransitionIds { get; set; } = [];
    [JsonPropertyName("carryForwards")] public List<RgbCarryForwardProof> CarryForwards { get; set; } = [];
}

public class RgbCarryForwardProof
{
    [JsonPropertyName("contractId")] public string ContractId { get; set; } = "";
    [JsonPropertyName("opout")] public string Opout { get; set; } = "";
    [JsonPropertyName("transitionId")] public string TransitionId { get; set; } = "";
    [JsonPropertyName("inputOutpoint")] public string InputOutpoint { get; set; } = "";
    [JsonPropertyName("assignmentType")] public int AssignmentType { get; set; }
    [JsonPropertyName("stateKind")] public string StateKind { get; set; } = "";
    [JsonPropertyName("amount")] public ulong? Amount { get; set; }
    [JsonPropertyName("successorKind")] public string SuccessorKind { get; set; } = "";
    [JsonPropertyName("witnessVout")] public uint? WitnessVout { get; set; }
    [JsonPropertyName("successorOutpoint")] public string? SuccessorOutpoint { get; set; }
    [JsonPropertyName("derivationPath")] public string? DerivationPath { get; set; }
}

public class SendBeginResult
{
    [JsonPropertyName("psbt")] public string Psbt { get; set; } = "";
    [JsonPropertyName("batch_transfer_idx")] public int? BatchTransferIdx { get; set; }
    [JsonPropertyName("details")] public SendDetails? Details { get; set; }
}

public class SendDetails
{
    [JsonPropertyName("fascia_path")] public string FasciaPath { get; set; } = "";
    [JsonPropertyName("entropy")] public ulong Entropy { get; set; }
    [JsonPropertyName("is_donation")] public bool IsDonation { get; set; }
}
