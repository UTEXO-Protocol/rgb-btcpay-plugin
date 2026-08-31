using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class SigningPolicy
{
    public string? ExpectedDestination { get; set; }
    public long? ExpectedAmountSats { get; set; }
    public long MaxUnknownOutputSats { get; set; } = 546;
    public double MaxFeePercent { get; set; } = 10.0;
    public long? MaxFeeSats { get; set; }
    public long MaxFeeSatsPerAdditionalInput { get; set; }
    public HashSet<Script>? AllowedScripts { get; set; }
    public int? MaxOutputCount { get; set; }

    /// <summary>
    /// When true, outputs are accepted ONLY if they match AllowedScripts, ExpectedDestination,
    /// or are zero-value OP_RETURN. The "any wallet-derived output is OK" shortcut is disabled.
    /// Use for paths where the caller constructs the PSBT and knows every legitimate output
    /// up-front (e.g. SendBtc). Do NOT use for paths where rgb-lib generates the PSBT and may
    /// emit wallet-derived outputs at indices not known in advance (e.g. SendAsset, CreateUtxos).
    /// </summary>
    public bool StrictAllowedScriptsOnly { get; set; }

    /// <summary>
    /// When true, every PSBT input must be provably owned by rgb-lib's VANILLA keychain
    /// (the account rgb-lib returns as AccountXpubVanilla), proven by re-deriving from this
    /// wallet's own keys against the input's prevout script. Inputs on rgb-lib's colored
    /// keychain — the only place RGB allocations live — are refused, and the refusal aborts the
    /// whole PSBT because SignWithKeys applies a key to every input, so per-input refusal is not
    /// expressible.
    /// Use on paths that sign a PSBT they did not build and that must never spend an
    /// allocation-bearing UTXO (CreateUtxos, SendBtc). Do NOT use on SendAsset, whose whole
    /// purpose is spending colored inputs and which is protected instead by the pre-sign intent
    /// gate's independent Stock scan.
    /// </summary>
    public bool RequireRgbVanillaKeychainInputs { get; set; }

    public bool RequireUnfinalizedWitnessProgramInputs { get; set; }
}

public interface IRgbWalletSigner : IDisposable
{
    Task<string> SignPsbtAsync(string psbt, Network network, SigningPolicy policy, CancellationToken cancellationToken = default);
    string MasterFingerprint { get; }
    string XpubRgbLibVanilla { get; }
    bool IsDisposed { get; }
}

public interface IRgbWalletSignerProvider
{
    Task<bool> CanHandleAsync(string walletId, CancellationToken cancellationToken = default);
    Task<IRgbWalletSigner?> GetSignerAsync(string walletId, CancellationToken cancellationToken = default);
}
