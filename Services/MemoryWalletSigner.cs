using NBitcoin;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RGB.Services;

public class MemoryWalletSigner : IRgbWalletSigner
{
    private ExtKey? _masterKey;
    private ExtKey? _vanillaAccountKey;
    private ExtKey? _coloredAccountKey;
    private readonly ILogger? _logger;
    
    private const string VanillaPathMainnet = "m/84'/0'/0'";
    private const string VanillaPathTestnet = "m/84'/1'/0'";
    private const string ColoredPathMainnet = "m/86'/0'/0'";
    private const string ColoredPathTestnet = "m/86'/1'/0'";
    
    public string MasterFingerprint { get; }
    public string XpubVanilla { get; }
    public string XpubColored { get; }
    public bool IsDisposed { get; private set; }
    
    public MemoryWalletSigner(string mnemonic, Network network, ILogger? logger = null)
    {
        _logger = logger;
        
        var mnemonicObj = new Mnemonic(mnemonic);
        _masterKey = mnemonicObj.DeriveExtKey();
        
        MasterFingerprint = _masterKey.GetPublicKey().GetHDFingerPrint().ToString().ToLowerInvariant();
        
        var isTestnet = network != Network.Main;
        var vanillaPath = new KeyPath(isTestnet ? VanillaPathTestnet : VanillaPathMainnet);
        var coloredPath = new KeyPath(isTestnet ? ColoredPathTestnet : ColoredPathMainnet);
        
        _vanillaAccountKey = _masterKey.Derive(vanillaPath);
        _coloredAccountKey = _masterKey.Derive(coloredPath);
        
        XpubVanilla = _vanillaAccountKey.Neuter().ToString(network);
        XpubColored = _coloredAccountKey.Neuter().ToString(network);
    }
    
    public Task<string> SignPsbtAsync(string psbtBase64, Network network, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        
        if (_masterKey == null || _vanillaAccountKey == null || _coloredAccountKey == null)
            throw new InvalidOperationException("Signer keys not available");
        
        var psbt = PSBT.Parse(psbtBase64.Trim('"'), network);
        
        foreach (var input in psbt.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (input.HDKeyPaths.Count > 0)
            {
                foreach (var hdKeyPath in input.HDKeyPaths)
                {
                    var fingerprint = hdKeyPath.Value.MasterFingerprint;
                    var path = hdKeyPath.Value.KeyPath;
                    
                    if (fingerprint.ToString().Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        var derivedKey = _masterKey.Derive(path);
                        psbt.SignWithKeys(derivedKey);
                    }
                }
            }
            else
            {
                TrySignWithAccountKeys(psbt, input, network);
            }
        }
        
        psbt.TryFinalize(out _);
        
        return Task.FromResult(psbt.ToBase64());
    }
    
    private void TrySignWithAccountKeys(PSBT psbt, PSBTInput input, Network network)
    {
        if (_vanillaAccountKey == null || _coloredAccountKey == null) return;
        
        for (int i = 0; i < 100; i++)
        {
            var vanillaReceiving = _vanillaAccountKey.Derive(new KeyPath($"0/{i}"));
            psbt.SignWithKeys(vanillaReceiving);
            
            var vanillaChange = _vanillaAccountKey.Derive(new KeyPath($"1/{i}"));
            psbt.SignWithKeys(vanillaChange);
            
            var coloredReceiving = _coloredAccountKey.Derive(new KeyPath($"0/{i}"));
            psbt.SignWithKeys(coloredReceiving);
            
            var coloredChange = _coloredAccountKey.Derive(new KeyPath($"1/{i}"));
            psbt.SignWithKeys(coloredChange);
            
            if (input.PartialSigs.Count > 0 || input.FinalScriptSig != null || input.FinalScriptWitness != null)
            {
                break;
            }
        }
    }
    
    public void Dispose()
    {
        if (IsDisposed) return;
        
        _masterKey = null;
        _vanillaAccountKey = null;
        _coloredAccountKey = null;
        IsDisposed = true;
        
        GC.SuppressFinalize(this);
        _logger?.LogDebug("MemoryWalletSigner disposed");
    }
}
