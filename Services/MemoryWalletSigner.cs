using System.Collections.Concurrent;
using NBitcoin;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class MemoryWalletSigner : IRgbWalletSigner
{
    ExtKey? _masterKey;
    ExtKey? _vanillaAccountKey;
    ExtKey? _coloredAccountKey;
    ExtKey? _rgbColoredAccountKey;
    readonly ILogger? _logger;
    readonly object _lock = new();

    const uint GapLimitScanBuffer = 200;
    const uint MinScanBaseline = 1000;
    const uint OnlyKeychainBranchTheColoredDescriptorCovers = 0;
    const uint HighestKeychainBranchSeenFromRgbLibOnTheVanillaAccount = 1;
    const int RgbColoredAccountPrefixIndex = 2;
    const ScriptPubKeyType TheOnlyScriptTypeRgbLibDescriptorsProduce = ScriptPubKeyType.TaprootBIP86;
    const long MaxIndexIterationsPerPsbt = 1_000_000;
    const uint MaxReasonableIndex = 100_000;
    internal const long ValueProportionalFeeCeilingFloorSats = 10_000;
    KeyPath[]? _allowedAccountPrefixes;

    public string MasterFingerprint { get; }
    public string XpubRgbLibVanilla { get; }
    public bool IsDisposed { get; private set; }

    public MemoryWalletSigner(string mnemonic, Network network, ILogger? logger = null)
    {
        _logger = logger;

        var mnemonicObj = new Mnemonic(mnemonic);
        _masterKey = mnemonicObj.DeriveExtKey();

        MasterFingerprint = _masterKey.GetPublicKey().GetHDFingerPrint().ToString().ToLowerInvariant();

        var isTestnet = network != Network.Main;
        var vanillaPath = new KeyPath(isTestnet ? "m/84'/1'/0'" : "m/84'/0'/0'");
        var coloredPath = new KeyPath(isTestnet ? "m/86'/1'/0'" : "m/86'/0'/0'");

        _vanillaAccountKey = _masterKey.Derive(vanillaPath);
        _coloredAccountKey = _masterKey.Derive(coloredPath);

        var rgbCoinType = isTestnet ? 827167 : 827166;
        _rgbColoredAccountKey = _masterKey.Derive(new KeyPath($"m/86'/{rgbCoinType}'/0'"));

        XpubRgbLibVanilla = _coloredAccountKey.Neuter().ToString(network);

        _allowedAccountPrefixes = [vanillaPath, coloredPath, new KeyPath($"m/86'/{rgbCoinType}'/0'")];
    }

    bool IsAllowedAccountPath(KeyPath path)
    {
        if (_allowedAccountPrefixes == null || path.Indexes.Length != 5) return false;
        var chain = path.Indexes[3];
        var index = path.Indexes[4];
        if (chain > HighestKeychainBranchSeenFromRgbLibOnTheVanillaAccount) return false;
        if ((index & 0x80000000) != 0 || index > MaxReasonableIndex) return false;
        var accountIndexes = path.Indexes.AsSpan()[..3];
        for (var i = 0; i < _allowedAccountPrefixes.Length; i++)
        {
            if (!_allowedAccountPrefixes[i].Indexes.AsSpan().SequenceEqual(accountIndexes)) continue;
            return chain <= HighestChainBranchAllowedForAccountPrefix(i);
        }
        return false;
    }
    
    public Task<string> SignPsbtAsync(string psbtBase64, Network network, SigningPolicy policy, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var psbt = PSBT.Parse(psbtBase64.Trim('"'), network);

        CalibrateIndexCeiling(psbt);
        PopulateInputKeyPaths(psbt, network, cancellationToken);
        if (policy.RequireUnfinalizedWitnessProgramInputs || policy.RequireRgbVanillaKeychainInputs)
            EnsureInputsAreUnfinalizedWitnessPrograms(psbt, cancellationToken);
        if (policy.RequireRgbVanillaKeychainInputs)
            EnsureInputsOnRgbVanillaAccount(psbt, network, cancellationToken);
        ValidateOutputs(psbt, network, policy, cancellationToken);
        RgbSighashGuard.EnsureAllInputsAllowed(psbt);

        foreach (var input in psbt.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignInput(psbt, input);
        }

        for (int i = 0; i < psbt.Inputs.Count; i++)
        {
            var inp = psbt.Inputs[i];
            if (inp.PartialSigs.Count == 0 && inp.TaprootKeySignature == null && inp.FinalScriptWitness == null && inp.FinalScriptSig == null)
                throw new InvalidOperationException(
                    $"PSBT input #{i} was not signed — no matching key found. The wallet may need to be re-synced.");
        }

        psbt.TryFinalize(out _);
        return Task.FromResult(psbt.ToBase64());
    }

    void CalibrateIndexCeiling(PSBT psbt)
    {
        foreach (var input in psbt.Inputs)
        {
            foreach (var kp in input.HDTaprootKeyPaths)
                UpdateCeiling(kp.Value.RootedKeyPath.MasterFingerprint, kp.Value.RootedKeyPath.KeyPath);
            foreach (var kp in input.HDKeyPaths)
                UpdateCeiling(kp.Value.MasterFingerprint, kp.Value.KeyPath);
        }
        foreach (var output in psbt.Outputs)
        {
            foreach (var kp in output.HDTaprootKeyPaths)
                UpdateCeiling(kp.Value.RootedKeyPath.MasterFingerprint, kp.Value.RootedKeyPath.KeyPath);
            foreach (var kp in output.HDKeyPaths)
                UpdateCeiling(kp.Value.MasterFingerprint, kp.Value.KeyPath);
        }
    }

    void UpdateCeiling(HDFingerprint fp, KeyPath path)
    {
        if (!fp.ToString().Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (!IsAllowedAccountPath(path)) return;
        var lastIndex = path.Indexes[^1];
        InterlockedMax(ref _highestVerifiedIndex, lastIndex);
    }

    internal bool IsOwnOutput(PSBTOutput output, Script outputScript, Network network)
    {
        if (_masterKey == null) return false;

        foreach (var kp in output.HDTaprootKeyPaths)
        {
            if (!kp.Value.RootedKeyPath.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsScriptRediscoverableByAnRgbLibDescriptor(
                    outputScript, kp.Value.RootedKeyPath.KeyPath, network))
                return true;
        }

        foreach (var kp in output.HDKeyPaths)
        {
            if (!kp.Value.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsScriptRediscoverableByAnRgbLibDescriptor(outputScript, kp.Value.KeyPath, network))
                return true;
        }

        return false;
    }

    internal bool IsScriptRediscoverableByAnRgbLibDescriptor(
        Script script, KeyPath claimedPath, Network network)
    {
        if (_masterKey == null) return false;
        if (!IsAllowedAccountPath(claimedPath)) return false;
        if (!TryClassifyAccount(claimedPath, out var account)) return false;
        if (account == PrevoutAccount.UnusedBip84) return false;

        return _masterKey.Derive(claimedPath).GetPublicKey()
            .GetAddress(TheOnlyScriptTypeRgbLibDescriptorsProduce, network).ScriptPubKey == script;
    }

    internal bool IsRgbColoredOutput(PSBTOutput output, Script outputScript, Network network)
    {
        var claims = new List<KeyPath>();
        foreach (var kp in output.HDTaprootKeyPaths)
            if (kp.Value.RootedKeyPath.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                claims.Add(kp.Value.RootedKeyPath.KeyPath);
        foreach (var kp in output.HDKeyPaths)
            if (kp.Value.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                claims.Add(kp.Value.KeyPath);

        return claims.Count == 1
            && IsRgbColoredDescriptorScript(outputScript, claims[0], network);
    }

    internal bool IsRgbColoredScriptAtPath(Script script, string claimedPath, Network network)
    {
        KeyPath path;
        try { path = new KeyPath(claimedPath); }
        catch { return false; }
        return IsRgbColoredDescriptorScript(script, path, network);
    }

    // rgb-lib's allocation-bearing BDK descriptor is exactly tr([origin]account-xpub/0/*).
    // Account-prefix ownership alone is insufficient here: branch /1 or P2WPKH uses a key we control,
    // but rgb-lib will not discover that output through its colored descriptor and the RGB successor can
    // become permanently inaccessible through the plugin.
    internal bool IsRgbColoredDescriptorScript(Script script, KeyPath path, Network network)
    {
        if (!TryClassifyAccount(path, out var account)
            || account != PrevoutAccount.RgbLibColored
            || path.Indexes[3] != 0
            || _masterKey == null)
            return false;

        var pubKey = _masterKey.Derive(path).GetPublicKey();
        return pubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script;
    }

    readonly ConcurrentDictionary<Script, byte> _verifiedScripts = new();
    uint _highestVerifiedIndex;

    const int MaxVerifiedScripts = 10_000;

    static uint HighestChainBranchAllowedForAccountPrefix(int accountPrefixIndex) =>
        accountPrefixIndex == RgbColoredAccountPrefixIndex
            ? OnlyKeychainBranchTheColoredDescriptorCovers
            : HighestKeychainBranchSeenFromRgbLibOnTheVanillaAccount;

    readonly record struct KeychainBranch(ExtPubKey ChainPub, KeyPath AccountPath, uint Chain);

    List<KeychainBranch> EnumerateKeychainBranches(Network network)
    {
        var accounts = new ExtKey?[] { _vanillaAccountKey, _coloredAccountKey, _rgbColoredAccountKey };
        var branches = new List<KeychainBranch>();
        for (var accountPrefixIndex = 0; accountPrefixIndex < accounts.Length; accountPrefixIndex++)
        {
            var account = accounts[accountPrefixIndex];
            if (account == null) continue;
            var accountPath = account == _vanillaAccountKey
                ? (network != Network.Main ? new KeyPath("84'/1'/0'") : new KeyPath("84'/0'/0'"))
                : account == _coloredAccountKey
                    ? (network != Network.Main ? new KeyPath("86'/1'/0'") : new KeyPath("86'/0'/0'"))
                    : new KeyPath($"86'/{(network != Network.Main ? 827167 : 827166)}'/0'");

            var xpub = account.Neuter();
            var highestChain = HighestChainBranchAllowedForAccountPrefix(accountPrefixIndex);
            for (uint chain = 0; chain <= highestChain; chain++)
                branches.Add(new KeychainBranch(xpub.Derive(chain), accountPath, chain));
        }
        return branches;
    }

    uint FastScanCeiling() =>
        Math.Max(MinScanBaseline, Volatile.Read(ref _highestVerifiedIndex) + GapLimitScanBuffer);

    uint ScanCeilingFor(bool scriptIsAssertedToBeOurs) =>
        scriptIsAssertedToBeOurs ? MaxReasonableIndex : FastScanCeiling();

    internal bool IsOwnScript(Script script, Network network, bool scriptIsAssertedToBeOurs = false,
        CancellationToken cancellationToken = default)
    {
        if (_verifiedScripts.ContainsKey(script)) return true;

        var scanCeiling = ScanCeilingFor(scriptIsAssertedToBeOurs);
        var branches = EnumerateKeychainBranches(network);
        for (uint idx = 0; idx <= scanCeiling; idx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var branch in branches)
            {
                var pubkey = branch.ChainPub.Derive(idx).PubKey;
                if (pubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script ||
                    pubkey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey == script)
                {
                    if (_verifiedScripts.Count < MaxVerifiedScripts)
                        _verifiedScripts.TryAdd(script, 0);
                    InterlockedMax(ref _highestVerifiedIndex, idx);
                    return true;
                }
            }
        }

        return false;
    }

    static void InterlockedMax(ref uint location, uint value)
    {
        uint initial, computed;
        do
        {
            initial = Volatile.Read(ref location);
            if (value <= initial) return;
            computed = value;
        } while (Interlocked.CompareExchange(ref location, computed, initial) != initial);
    }

    void ValidateOutputs(PSBT psbt, Network network, SigningPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.MaxOutputCount.HasValue && psbt.Outputs.Count > policy.MaxOutputCount.Value)
            throw new InvalidOperationException(
                $"PSBT has {psbt.Outputs.Count} outputs, policy allows at most {policy.MaxOutputCount.Value}");

        if (policy.AllowedScripts != null)
        {
            foreach (var script in policy.AllowedScripts)
                if (!IsOwnScript(script, network, scriptIsAssertedToBeOurs: true, cancellationToken))
                    throw new InvalidOperationException(
                        $"AllowedScripts contains address not derivable from wallet keys: {script.GetDestinationAddress(network)?.ToString() ?? script.ToHex()}");
        }

        Script? destScript = null;
        if (!string.IsNullOrEmpty(policy.ExpectedDestination))
        {
            destScript = BitcoinAddress.Create(policy.ExpectedDestination, network).ScriptPubKey;
        }

        long totalToDest = 0;
        long totalUnknown = 0;
        for (int i = 0; i < psbt.Outputs.Count; i++)
        {
            var txOut = psbt.GetGlobalTransaction().Outputs[i];
            var script = txOut.ScriptPubKey;
            var amount = txOut.Value.Satoshi;

            if (!policy.StrictAllowedScriptsOnly && IsOwnOutput(psbt.Outputs[i], script, network)) continue;
            if (policy.AllowedScripts != null && policy.AllowedScripts.Contains(script)) continue;
            if (destScript != null && script == destScript) { totalToDest += amount; continue; }
            if (script.IsUnspendable)
            {
                if (amount > 0)
                    throw new InvalidOperationException(
                        $"PSBT output #{i} is unspendable (OP_RETURN) with nonzero value ({amount} sat) — potential burn attack");
                continue;
            }

            if (amount > policy.MaxUnknownOutputSats)
            {
                var addr = script.GetDestinationAddress(network)?.ToString() ?? script.ToHex();
                throw new InvalidOperationException(
                    $"PSBT output #{i} to unknown address {addr}, amount {amount} sat exceeds policy limit of {policy.MaxUnknownOutputSats} sat");
            }
            totalUnknown += amount;
        }

        if (totalUnknown > policy.MaxUnknownOutputSats)
            throw new InvalidOperationException(
                $"PSBT cumulative unknown output total ({totalUnknown} sat) exceeds policy limit of {policy.MaxUnknownOutputSats} sat");

        if (policy.ExpectedAmountSats.HasValue && destScript != null && totalToDest != policy.ExpectedAmountSats.Value)
            throw new InvalidOperationException(
                $"PSBT total to destination ({totalToDest} sat) does not match expected ({policy.ExpectedAmountSats.Value} sat)");

        long totalInputValue = 0;
        // Resolve every input through GetTxOut(), which is the same accessor NBitcoin signs from.
        // It prefers NonWitnessUtxo, so reading WitnessUtxo first let a PSBT producer declare an
        // understated input value: the fee computed here stayed under MaxFeeSats while the sighash
        // committed to the real, larger amount, and the difference was paid to miners.
        foreach (var input in psbt.Inputs)
        {
            var prevOut = input.GetTxOut();
            if (prevOut != null)
                totalInputValue += prevOut.Value.Satoshi;
        }

        if (totalInputValue == 0 && psbt.Inputs.Count > 0)
            throw new InvalidOperationException("PSBT inputs lack UTXO data — cannot compute fee");

        if (totalInputValue > 0)
        {
            var totalOutputValue = psbt.GetGlobalTransaction().Outputs.Sum(o => o.Value.Satoshi);
            var fee = totalInputValue - totalOutputValue;
            long maxFee;
            if (policy.MaxFeeSats.HasValue && policy.MaxFeeSatsPerAdditionalInput != 0)
            {
                maxFee = policy.MaxFeeSats.Value
                    + policy.MaxFeeSatsPerAdditionalInput * Math.Max(psbt.Inputs.Count - 1, 0);
            }
            else
            {
                maxFee = (long)(totalOutputValue * policy.MaxFeePercent / 100.0);
                if (maxFee < ValueProportionalFeeCeilingFloorSats)
                    maxFee = ValueProportionalFeeCeilingFloorSats;
                if (policy.MaxFeeSats.HasValue && policy.MaxFeeSats.Value < maxFee)
                    maxFee = policy.MaxFeeSats.Value;
            }
            if (fee > maxFee)
                throw new InvalidOperationException(
                    $"PSBT fee ({fee} sat) exceeds max allowed {maxFee} sat");
        }
    }

    // rgb-lib's keychain names, NOT this class's members, which are misleading: the keychain rgb-lib
    // calls "vanilla" — spendable BTC that never carries an allocation — is _coloredAccountKey, and the
    // one it calls "colored", where every RGB allocation lives, is _rgbColoredAccountKey. The BIP84
    // account this class also derives is vestigial: rgb-lib is handed its own generated xpubs and never
    // produces a descriptor for it.
    internal enum PrevoutAccount { RgbLibVanilla, RgbLibColored, UnusedBip84 }

    // Mirrors IsAllowedAccountPath's constraints so a path this classifies is exactly one SignInput
    // could derive a signing key from.
    internal bool TryClassifyAccount(KeyPath path, out PrevoutAccount account)
    {
        account = PrevoutAccount.UnusedBip84;
        if (_allowedAccountPrefixes == null || path.Indexes.Length != 5) return false;
        var chain = path.Indexes[3];
        var index = path.Indexes[4];
        if (chain > 1) return false;
        if ((index & 0x80000000) != 0 || index > MaxReasonableIndex) return false;

        var accountIndexes = path.Indexes.AsSpan()[..3];
        if (accountIndexes.SequenceEqual(_allowedAccountPrefixes[1].Indexes.AsSpan()))
        {
            account = PrevoutAccount.RgbLibVanilla;
            return true;
        }
        if (accountIndexes.SequenceEqual(_allowedAccountPrefixes[2].Indexes.AsSpan()))
        {
            account = PrevoutAccount.RgbLibColored;
            return true;
        }
        if (accountIndexes.SequenceEqual(_allowedAccountPrefixes[0].Indexes.AsSpan()))
        {
            account = PrevoutAccount.UnusedBip84;
            return true;
        }
        return false;
    }

    // Verifies a claimed derivation instead of searching for one: derive exactly the claimed path and
    // test it against the prevout script. A claim that verifies is true whoever supplied it, and a claim
    // that does not is grounds for refusal — so this needs one derivation per entry rather than a scan.
    internal bool TryVerifyClaimedPath(Script script, KeyPath claimed, Network network, out PrevoutAccount account)
    {
        if (!TryClassifyAccount(claimed, out account)) return false;
        if (_masterKey == null) return false;

        var pubKey = _masterKey.Derive(claimed).GetPublicKey();
        return pubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script
            || pubKey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey == script;
    }

    void EnsureInputsAreUnfinalizedWitnessPrograms(PSBT psbt, CancellationToken cancellationToken)
    {
        for (int i = 0; i < psbt.Inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = psbt.Inputs[i];

            if (input.FinalScriptSig != null || input.FinalScriptWitness != null)
                throw new InvalidOperationException(
                    $"PSBT input #{i} already carries producer-supplied final script data — refusing to sign");

            var prevOut = input.GetTxOut();
            if (prevOut == null)
                throw new InvalidOperationException(
                    $"PSBT input #{i} has an unresolvable prevout — refusing to sign");

            if (input.WitnessUtxo != null && input.NonWitnessUtxo != null)
            {
                var n = input.PrevOut.N;
                var declared = n < input.NonWitnessUtxo.Outputs.Count
                    ? input.NonWitnessUtxo.Outputs[(int)n]
                    : null;
                if (declared == null
                    || declared.ScriptPubKey != input.WitnessUtxo.ScriptPubKey
                    || declared.Value != input.WitnessUtxo.Value)
                    throw new InvalidOperationException(
                        $"PSBT input #{i} has conflicting utxo fields — refusing to sign");
            }

            var script = prevOut.ScriptPubKey;
            if (!script.IsScriptType(ScriptType.P2WPKH) && !script.IsScriptType(ScriptType.Taproot))
                throw new InvalidOperationException(
                    $"PSBT input #{i} prevout is not a witness program — refusing to sign");
        }
    }

    void EnsureInputsOnRgbVanillaAccount(PSBT psbt, Network network, CancellationToken cancellationToken)
    {
        for (int i = 0; i < psbt.Inputs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = psbt.Inputs[i];

            // GetTxOut() is the accessor NBitcoin itself signs from, so resolving through it is what
            // guarantees this guard inspects the txout the sighash will commit to.
            var prevOut = input.GetTxOut();
            if (prevOut == null)
                throw new InvalidOperationException(
                    $"PSBT input #{i} has an unresolvable prevout — refusing to sign");

            // NBitcoin prefers NonWitnessUtxo and never checks the two fields agree, so a disagreeing
            // pair would let a producer show this guard one txout while the signature commits to another.
            if (input.WitnessUtxo != null && input.NonWitnessUtxo != null)
            {
                var n = input.PrevOut.N;
                var declared = n < input.NonWitnessUtxo.Outputs.Count
                    ? input.NonWitnessUtxo.Outputs[(int)n]
                    : null;
                if (declared == null
                    || declared.ScriptPubKey != input.WitnessUtxo.ScriptPubKey
                    || declared.Value != input.WitnessUtxo.Value)
                    throw new InvalidOperationException(
                        $"PSBT input #{i} has conflicting utxo fields — refusing to sign");
            }

            // Witness programs only: the pre-segwit sighash algorithm does not commit to the input
            // amount, so the commitment that makes a forged prevout harmless would not hold there.
            var script = prevOut.ScriptPubKey;
            if (!script.IsScriptType(ScriptType.P2WPKH) && !script.IsScriptType(ScriptType.Taproot))
                throw new InvalidOperationException(
                    $"PSBT input #{i} prevout is not a witness program — refusing to sign");

            // Exactly the entries SignInput can derive a signing key from. Foreign fingerprints grant
            // no capability, so ignoring them keeps this set a superset of the signable one.
            var claimed = new List<KeyPath>();
            foreach (var kp in input.HDKeyPaths)
                if (kp.Value.MasterFingerprint.ToString().Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                    claimed.Add(kp.Value.KeyPath);
            foreach (var kp in input.HDTaprootKeyPaths)
                if (kp.Value.RootedKeyPath.MasterFingerprint.ToString().Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                    claimed.Add(kp.Value.RootedKeyPath.KeyPath);

            if (claimed.Count == 0)
                throw new InvalidOperationException(
                    $"PSBT input #{i} carries no qualifying key path for this wallet — refusing to sign. "
                    + $"Every keychain index up to {MaxReasonableIndex} was searched, so this input's "
                    + "script is either not derivable from this wallet's seed at all, or sits above the "
                    + "highest index this signer will accept. Retrying will not change the outcome.");

            foreach (var path in claimed)
            {
                if (!TryVerifyClaimedPath(script, path, network, out var account))
                    throw new InvalidOperationException(
                        $"PSBT input #{i} key path does not match its prevout script — refusing to sign");
                if (account != PrevoutAccount.RgbLibVanilla)
                    throw new InvalidOperationException(
                        $"PSBT input #{i} is on the {account} account, not rgb-lib's vanilla keychain — refusing to sign");
            }
        }
    }

    void PopulateInputKeyPaths(PSBT psbt, Network network, CancellationToken cancellationToken)
    {
        var fingerprint = new HDFingerprint(Convert.FromHexString(MasterFingerprint));
        var inputsAwaitingAPathByScript = new Dictionary<Script, List<PSBTInput>>();
        foreach (var input in psbt.Inputs)
        {
            if (input.HDKeyPaths.Count > 0 || input.HDTaprootKeyPaths.Count > 0) continue;
            if (input.WitnessUtxo == null) continue;
            var script = input.WitnessUtxo.ScriptPubKey;
            if (!inputsAwaitingAPathByScript.TryGetValue(script, out var sharingThisScript))
                inputsAwaitingAPathByScript[script] = sharingThisScript = [];
            sharingThisScript.Add(input);
        }
        if (inputsAwaitingAPathByScript.Count == 0) return;

        var branches = EnumerateKeychainBranches(network);
        var indexIterationsRemaining = MaxIndexIterationsPerPsbt;

        void BindEverythingFoundInRange(uint firstIndex, uint lastIndex)
        {
            for (var idx = firstIndex; idx <= lastIndex && inputsAwaitingAPathByScript.Count > 0; idx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (indexIterationsRemaining-- <= 0) return;

                foreach (var branch in branches)
                {
                    var pubkey = branch.ChainPub.Derive(idx).PubKey;
                    foreach (var candidate in new[]
                             {
                                 pubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey,
                                 pubkey.GetAddress(ScriptPubKeyType.Segwit, network).ScriptPubKey
                             })
                    {
                        if (!inputsAwaitingAPathByScript.TryGetValue(candidate, out var awaiting)) continue;

                        var fullPath = branch.AccountPath.Derive(new KeyPath($"{branch.Chain}/{idx}"));
                        foreach (var input in awaiting)
                            AttachKeyPathForScript(input, candidate, fullPath, network, fingerprint);
                        inputsAwaitingAPathByScript.Remove(candidate);
                        InterlockedMax(ref _highestVerifiedIndex, idx);
                    }
                }
            }
        }

        var fastCeiling = FastScanCeiling();
        BindEverythingFoundInRange(0, fastCeiling);
        if (inputsAwaitingAPathByScript.Count > 0 && fastCeiling < MaxReasonableIndex)
            BindEverythingFoundInRange(fastCeiling + 1, MaxReasonableIndex);
    }

    void AttachKeyPathForScript(
        PSBTInput input, Script script, KeyPath fullPath, Network network, HDFingerprint fingerprint)
    {
        if (_masterKey == null) return;
        var pubkey = _masterKey.Derive(fullPath).GetPublicKey();
        if (pubkey.GetAddress(ScriptPubKeyType.TaprootBIP86, network).ScriptPubKey == script)
            input.HDTaprootKeyPaths.Add(
                pubkey.GetTaprootFullPubKey(), new TaprootKeyPath(new RootedKeyPath(fingerprint, fullPath)));
        else
            input.HDKeyPaths.Add(pubkey, new RootedKeyPath(fingerprint, fullPath));
    }

    void SignInput(PSBT psbt, PSBTInput input)
    {
        if (_masterKey == null) return;

        foreach (var kp in input.HDKeyPaths)
        {
            if (!kp.Value.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.KeyPath)) continue;
            psbt.SignWithKeys(_masterKey.Derive(kp.Value.KeyPath));
        }

        foreach (var kp in input.HDTaprootKeyPaths)
        {
            if (!kp.Value.RootedKeyPath.MasterFingerprint.ToString()
                .Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsAllowedAccountPath(kp.Value.RootedKeyPath.KeyPath)) continue;
            psbt.SignWithKeys(_masterKey.Derive(kp.Value.RootedKeyPath.KeyPath));
        }
    }
    
    public void Dispose()
    {
        if (IsDisposed) return;
        
        lock (_lock)
        {
            if (IsDisposed) return;
            
            ClearKeyMaterial();
            IsDisposed = true;
        }
        
        GC.SuppressFinalize(this);
        _logger?.LogDebug("MemoryWalletSigner disposed");
    }
    
    void ClearKeyMaterial()
    {
        _masterKey = null;
        _vanillaAccountKey = null;
        _coloredAccountKey = null;
        _rgbColoredAccountKey = null;
        _verifiedScripts.Clear();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }
}
