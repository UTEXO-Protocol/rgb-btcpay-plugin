using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbIntentVerifier
{
    const int OsAsset = 4000;
    const string ConfidentialSeal = "confidentialSeal";
    const string RevealedWitnessVout = "revealedWitnessVout";
    const string RevealedConcreteOutpoint = "revealedConcreteOutpoint";

    public static async Task VerifyAsync(
        RgbDecodeInvoiceResult decode,
        RgbValidateV2Result validate,
        PSBT unsignedPsbt,
        string unsignedTxid,
        MemoryWalletSigner signer,
        Network walletNetwork,
        long operatorAmount,
        string operatorAssetId,
        IReadOnlyList<string> stagedTransportEndpoints,
        IBitcoinChainClient chainClient,
        CancellationToken ct = default)
    {
        VerifyExpiry(decode);
        VerifyOperatorApproval(decode, operatorAssetId, operatorAmount);
        VerifyAsset(decode, validate);
        EnsureNetwork(validate.ChainNet, walletNetwork, "consignment network");
        EnsureNetwork(decode.RecipientChainNet, walletNetwork, "recipient network");
        VerifyWitnessIdentity(validate, unsignedPsbt, unsignedTxid);
        VerifyNoDecoyTaproot(unsignedPsbt, signer, walletNetwork);
        var recipientLeg = VerifyRecipientLeg(decode, validate, operatorAmount);
        await VerifyChangeLegsAsync(validate, recipientLeg, unsignedPsbt, signer, walletNetwork, chainClient, ct);
        VerifyCommitmentAndCoverage(decode, validate);
        await VerifyCarryForwardsAsync(validate, unsignedPsbt, signer, walletNetwork, chainClient, ct);
        VerifyTransportEndpoints(decode, stagedTransportEndpoints);
    }

    static void VerifyExpiry(RgbDecodeInvoiceResult decode)
    {
        if (decode.Expiry.HasValue
            && DateTimeOffset.FromUnixTimeSeconds(decode.Expiry.Value) < DateTimeOffset.UtcNow)
            throw new RgbIntentVerificationException("RGB invoice has expired");
    }

    static void EnsureNetwork(string prefix, Network walletNetwork, string context)
    {
        if (!RgbChainNetMapper.TryMapPrefix(prefix, out var mapped) || mapped == null)
            throw new RgbIntentVerificationException(
                $"{context}: RGB chain-net prefix '{prefix}' is not a supported plugin network");
        if (mapped != walletNetwork)
            throw new RgbIntentVerificationException(
                $"{context}: RGB chain-net prefix '{prefix}' maps to {mapped} but wallet is {walletNetwork}");
    }

    static void VerifyAsset(RgbDecodeInvoiceResult decode, RgbValidateResult validate)
    {
        if (!string.Equals(decode.ContractId, validate.ContractId, StringComparison.Ordinal))
            throw new RgbIntentVerificationException(
                "asset mismatch: invoice contract does not match the consignment contract");
    }

    static void VerifyOperatorApproval(RgbDecodeInvoiceResult decode, string operatorAssetId, long operatorAmount)
    {
        if (string.IsNullOrEmpty(operatorAssetId))
            throw new RgbIntentVerificationException("operator did not approve an asset to send");
        if (!string.Equals(StripRgbPrefix(decode.ContractId), StripRgbPrefix(operatorAssetId), StringComparison.Ordinal))
            throw new RgbIntentVerificationException(
                "asset mismatch: invoice contract does not match the operator-approved asset");
        if (operatorAmount < 0)
            throw new RgbIntentVerificationException("operator amount is negative");
        if (decode.AmountKind == "amount")
        {
            if (!decode.Amount.HasValue)
                throw new RgbIntentVerificationException("invoice declares an amount but none was decoded");
            if ((ulong)operatorAmount != decode.Amount.Value)
                throw new RgbIntentVerificationException(
                    "amount mismatch: operator-approved amount does not match the invoice amount");
        }
    }

    static string StripRgbPrefix(string s) =>
        s.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase) ? s.Substring(4) : s;

    static void VerifyWitnessIdentity(RgbValidateResult validate, PSBT unsignedPsbt, string unsignedTxid)
    {
        if (!string.Equals(validate.WitnessTxid, unsignedTxid, StringComparison.OrdinalIgnoreCase))
            throw new RgbIntentVerificationException(
                "witness identity mismatch: consignment witness txid does not match the tx being signed");

        if (validate.Prevouts.Count == 0)
            throw new RgbIntentVerificationException(
                "prevout canary failed: consignment lists no prevouts for the anchored bundle");

        var tx = unsignedPsbt.GetGlobalTransaction();
        var psbtInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in tx.Inputs)
            psbtInputs.Add($"{input.PrevOut.Hash}:{input.PrevOut.N}");

        foreach (var prevout in validate.Prevouts)
            if (!psbtInputs.Contains(prevout))
                throw new RgbIntentVerificationException(
                    $"prevout canary failed: consignment prevout {prevout} is not an input of the tx being signed");
    }

    static void VerifyNoDecoyTaproot(PSBT unsignedPsbt, MemoryWalletSigner signer, Network network)
    {
        var tx = unsignedPsbt.GetGlobalTransaction();
        for (int i = 0; i < tx.Outputs.Count; i++)
        {
            var script = tx.Outputs[i].ScriptPubKey;
            if (!RgbPsbtInspector.IsTaproot(script)) continue;
            if (signer.IsOwnOutput(unsignedPsbt.Outputs[i], script, network)) continue;
            if (signer.IsOwnScript(script, network)) continue;
            throw new RgbIntentVerificationException(
                $"anti-decoy check failed: taproot output #{i} is not a plain wallet-derived key");
        }
    }

    static RgbLeg VerifyRecipientLeg(RgbDecodeInvoiceResult decode, RgbValidateResult validate, long operatorAmount)
    {
        RgbLeg? recipient = null;
        foreach (var leg in validate.Legs)
        {
            if (leg.AssignmentType != OsAsset) continue;
            if (leg.SealKind != ConfidentialSeal) continue;
            if (leg.SealBytes == null) continue;
            if (!string.Equals(leg.SealBytes, decode.RecipientSeal, StringComparison.OrdinalIgnoreCase)) continue;
            if (recipient != null)
                throw new RgbIntentVerificationException("duplicate recipient leg matching the invoice seal");
            recipient = leg;
        }

        if (recipient == null)
            throw new RgbIntentVerificationException(
                "recipient leg not found: no consignment leg commits to the invoice recipient seal");

        ulong expectedAmount;
        if (decode.AmountKind == "amount")
        {
            if (!decode.Amount.HasValue)
                throw new RgbIntentVerificationException("invoice declares an amount but none was decoded");
            expectedAmount = decode.Amount.Value;
        }
        else if (decode.AmountKind == "absent")
        {
            if (operatorAmount < 0)
                throw new RgbIntentVerificationException("operator amount is negative");
            expectedAmount = (ulong)operatorAmount;
        }
        else
        {
            throw new RgbIntentVerificationException(
                $"unexpected invoice amountKind '{decode.AmountKind}'");
        }

        if (recipient.Amount != expectedAmount)
            throw new RgbIntentVerificationException(
                $"recipient amount mismatch: consignment commits {recipient.Amount}, expected {expectedAmount}");

        return recipient;
    }

    static async Task VerifyChangeLegsAsync(
        RgbValidateResult validate,
        RgbLeg recipientLeg,
        PSBT unsignedPsbt,
        MemoryWalletSigner signer,
        Network network,
        IBitcoinChainClient chainClient,
        CancellationToken ct)
    {
        var tx = unsignedPsbt.GetGlobalTransaction();

        foreach (var leg in validate.Legs)
        {
            if (ReferenceEquals(leg, recipientLeg)) continue;

            switch (leg.SealKind)
            {
                case RevealedWitnessVout:
                    if (!leg.WitnessVout.HasValue)
                        throw new RgbIntentVerificationException("change leg is missing its witness vout");
                    var vout = leg.WitnessVout.Value;
                    if (vout >= tx.Outputs.Count)
                        throw new RgbIntentVerificationException(
                            $"change leg witness vout {vout} is out of range of the tx being signed");
                    var script = tx.Outputs[(int)vout].ScriptPubKey;
                    if (!signer.IsRgbColoredOutput(unsignedPsbt.Outputs[(int)vout], script, network))
                        throw new RgbIntentVerificationException(
                            $"change leg witness vout {vout} is not proven on rgb-lib's colored account");
                    break;

                case RevealedConcreteOutpoint:
                    await VerifyConcreteChangeAsync(leg, unsignedPsbt, signer, network, chainClient, ct);
                    break;

                default:
                    throw new RgbIntentVerificationException(
                        $"non-recipient leg has non-revealed or unknown seal kind '{leg.SealKind}' — refusing to sign");
            }
        }
    }

    static async Task VerifyConcreteChangeAsync(
        RgbLeg leg,
        PSBT unsignedPsbt,
        MemoryWalletSigner signer,
        Network network,
        IBitcoinChainClient chainClient,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(leg.Outpoint))
            throw new RgbIntentVerificationException("change leg is missing its concrete outpoint");

        var parts = leg.Outpoint.Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[1], out var vout))
            throw new RgbIntentVerificationException($"change leg outpoint '{leg.Outpoint}' is malformed");
        var txid = parts[0];

        var tx = unsignedPsbt.GetGlobalTransaction();
        foreach (var input in tx.Inputs)
            if (string.Equals(input.PrevOut.Hash.ToString(), txid, StringComparison.OrdinalIgnoreCase)
                && input.PrevOut.N == vout)
                throw new RgbIntentVerificationException(
                    $"change leg outpoint {leg.Outpoint} is an input of the tx being signed, not a retained UTXO");

        var rawTx = await chainClient.GetRawTransactionAsync(txid, ct);
        var funding = Transaction.Parse(rawTx, network);
        if (!string.Equals(funding.GetHash().ToString(), txid, StringComparison.OrdinalIgnoreCase))
            throw new RgbIntentVerificationException(
                $"funding tx for change outpoint {leg.Outpoint} does not hash to the requested txid");
        if (vout >= funding.Outputs.Count)
            throw new RgbIntentVerificationException($"change leg outpoint {leg.Outpoint} vout is out of range");
        var script = funding.Outputs[(int)vout].ScriptPubKey;

        if (string.IsNullOrWhiteSpace(leg.DerivationPath)
            || !signer.IsRgbColoredScriptAtPath(script, leg.DerivationPath, network))
            throw new RgbIntentVerificationException(
                $"change leg outpoint {leg.Outpoint} is not proven on rgb-lib's colored account");

        var unspent = await chainClient.ListUnspentByScriptAsync(script, ct);
        var isUnspent = unspent.Any(o =>
            string.Equals(o.Txid, txid, StringComparison.OrdinalIgnoreCase) && o.Vout == vout);
        if (!isUnspent)
            throw new RgbIntentVerificationException(
                $"change leg outpoint {leg.Outpoint} is not in the wallet's unspent set");
    }

    static void VerifyCommitmentAndCoverage(RgbDecodeInvoiceResult decode, RgbValidateV2Result validate)
    {
        if (validate.ValidationVersion != 2)
            throw new RgbIntentVerificationException(
                $"unsupported native validation version {validate.ValidationVersion}");
        if (!validate.InputsAccounted)
            throw new RgbIntentVerificationException(
                "intent gate: not every PSBT input allocation is exhaustively accounted for");
        if (!validate.CommitmentMatches)
            throw new RgbIntentVerificationException(
                "commitment mismatch: the opret in the tx being signed does not commit the fascia bundles");
        if (!validate.WitnessIdMatches)
            throw new RgbIntentVerificationException(
                "commitment witness mismatch: the fascia is not bound to the tx being signed");

        EnsureUniqueNonEmpty(validate.CommittedContractIds, "committed contract");
        EnsureUniqueNonEmpty(validate.VerifiedContractIds, "verified contract");
        EnsureUniqueNonEmpty(validate.VerifiedTransitionIds, "verified transition");
        if (string.IsNullOrWhiteSpace(validate.MainTransitionId))
            throw new RgbIntentVerificationException("native result omits the main transition id");

        var expectedContracts = new HashSet<string>(StringComparer.Ordinal) { decode.ContractId };
        var expectedTransitions = new HashSet<string>(StringComparer.Ordinal) { validate.MainTransitionId };
        var opouts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var carry in validate.CarryForwards)
        {
            if (string.IsNullOrWhiteSpace(carry.ContractId)
                || string.IsNullOrWhiteSpace(carry.Opout)
                || string.IsNullOrWhiteSpace(carry.TransitionId)
                || string.IsNullOrWhiteSpace(carry.InputOutpoint))
                throw new RgbIntentVerificationException("native carry-forward proof is incomplete");
            if (!opouts.Add($"{carry.ContractId}\0{carry.Opout}"))
                throw new RgbIntentVerificationException("native result duplicates a carry-forward opout");
            if (!expectedTransitions.Add(carry.TransitionId))
                throw new RgbIntentVerificationException("native result reuses a carry-forward transition");
            if (!validate.Prevouts.Contains(carry.InputOutpoint, StringComparer.OrdinalIgnoreCase))
                throw new RgbIntentVerificationException(
                    $"carry-forward input {carry.InputOutpoint} is not a witness prevout");
            if (!string.Equals(carry.StateKind, "amount", StringComparison.Ordinal)
                || !carry.Amount.HasValue)
                throw new RgbIntentVerificationException("unsupported carry-forward state proof");
            expectedContracts.Add(carry.ContractId);
        }

        var committed = new HashSet<string>(validate.CommittedContractIds, StringComparer.Ordinal);
        var verified = new HashSet<string>(validate.VerifiedContractIds, StringComparer.Ordinal);
        var transitions = new HashSet<string>(validate.VerifiedTransitionIds, StringComparer.Ordinal);
        if (!committed.SetEquals(expectedContracts) || !verified.SetEquals(expectedContracts))
            throw new RgbIntentVerificationException(
                "commitment scope mismatch: committed, verified, and intended carry contract sets differ");
        if (!transitions.SetEquals(expectedTransitions))
            throw new RgbIntentVerificationException(
                "transition scope mismatch: native result contains missing or unrelated transitions");
    }

    static void EnsureUniqueNonEmpty(IEnumerable<string> values, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value))
                throw new RgbIntentVerificationException($"native result has an empty or duplicate {kind} id");
    }

    static async Task VerifyCarryForwardsAsync(
        RgbValidateV2Result validate,
        PSBT unsignedPsbt,
        MemoryWalletSigner signer,
        Network network,
        IBitcoinChainClient chainClient,
        CancellationToken ct)
    {
        var tx = unsignedPsbt.GetGlobalTransaction();
        foreach (var carry in validate.CarryForwards)
        {
            switch (carry.SuccessorKind)
            {
                case RevealedWitnessVout:
                    if (!carry.WitnessVout.HasValue || carry.SuccessorOutpoint != null
                        || carry.DerivationPath != null)
                        throw new RgbIntentVerificationException(
                            "witness carry-forward successor proof has inconsistent fields");
                    var vout = carry.WitnessVout.Value;
                    if (vout >= tx.Outputs.Count)
                        throw new RgbIntentVerificationException(
                            $"carry-forward witness vout {vout} is out of range");
                    var script = tx.Outputs[(int)vout].ScriptPubKey;
                    if (!signer.IsRgbColoredOutput(unsignedPsbt.Outputs[(int)vout], script, network))
                        throw new RgbIntentVerificationException(
                            $"carry-forward witness vout {vout} is not proven on rgb-lib's colored account");
                    break;

                case RevealedConcreteOutpoint:
                    if (carry.WitnessVout.HasValue || string.IsNullOrWhiteSpace(carry.SuccessorOutpoint)
                        || string.IsNullOrWhiteSpace(carry.DerivationPath))
                        throw new RgbIntentVerificationException(
                            "concrete carry-forward successor proof has inconsistent fields");
                    await VerifyConcreteCarryAsync(carry, unsignedPsbt, signer, network, chainClient, ct);
                    break;

                default:
                    throw new RgbIntentVerificationException(
                        $"carry-forward has unsupported successor kind '{carry.SuccessorKind}'");
            }
        }
    }

    static async Task VerifyConcreteCarryAsync(
        RgbCarryForwardProof carry,
        PSBT unsignedPsbt,
        MemoryWalletSigner signer,
        Network network,
        IBitcoinChainClient chainClient,
        CancellationToken ct)
    {
        var outpoint = carry.SuccessorOutpoint!;
        var parts = outpoint.Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[1], out var vout))
            throw new RgbIntentVerificationException($"carry successor outpoint '{outpoint}' is malformed");
        var txid = parts[0];
        foreach (var input in unsignedPsbt.GetGlobalTransaction().Inputs)
            if (string.Equals(input.PrevOut.Hash.ToString(), txid, StringComparison.OrdinalIgnoreCase)
                && input.PrevOut.N == vout)
                throw new RgbIntentVerificationException(
                    $"carry successor {outpoint} is consumed by the transaction being signed");

        var rawTx = await chainClient.GetRawTransactionAsync(txid, ct);
        var funding = Transaction.Parse(rawTx, network);
        if (!string.Equals(funding.GetHash().ToString(), txid, StringComparison.OrdinalIgnoreCase)
            || vout >= funding.Outputs.Count)
            throw new RgbIntentVerificationException(
                $"carry successor funding transaction does not bind to {outpoint}");
        var script = funding.Outputs[(int)vout].ScriptPubKey;
        if (!signer.IsRgbColoredScriptAtPath(script, carry.DerivationPath!, network))
            throw new RgbIntentVerificationException(
                $"carry successor {outpoint} is not proven on rgb-lib's colored account");
        var unspent = await chainClient.ListUnspentByScriptAsync(script, ct);
        if (!unspent.Any(o => string.Equals(o.Txid, txid, StringComparison.OrdinalIgnoreCase)
                              && o.Vout == vout))
            throw new RgbIntentVerificationException(
                $"carry successor {outpoint} is not in the wallet's unspent set");
    }

    static void VerifyTransportEndpoints(RgbDecodeInvoiceResult decode, IReadOnlyList<string> staged)
    {
        var expected = new HashSet<string>(decode.Transports.Select(Normalize), StringComparer.Ordinal);
        var actual = new HashSet<string>(staged.Select(Normalize), StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            throw new RgbIntentVerificationException(
                "transport endpoint mismatch: staged endpoints differ from the invoice endpoints");
    }

    static string Normalize(string endpoint)
    {
        var e = endpoint.Trim().TrimEnd('/');
        if (e.StartsWith("rpcs://", StringComparison.OrdinalIgnoreCase))
            return "https://" + e.Substring("rpcs://".Length);
        if (e.StartsWith("rpc://", StringComparison.OrdinalIgnoreCase))
            return "http://" + e.Substring("rpc://".Length);
        return e;
    }
}
