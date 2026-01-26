using System.Runtime.CompilerServices;
using NBitcoin;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class MemoryWalletSigner : IRgbWalletSigner
{
    ExtKey? _masterKey;
    ExtKey? _vanillaAccountKey;
    ExtKey? _coloredAccountKey;
    readonly Dictionary<string, ExtKey> _derivedKeys = new();
    readonly ILogger? _logger;
    readonly object _lock = new();
    
    const int PreDeriveCount = 20;
    
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
        var vanillaPath = new KeyPath(isTestnet ? "m/84'/1'/0'" : "m/84'/0'/0'");
        var coloredPath = new KeyPath(isTestnet ? "m/86'/1'/0'" : "m/86'/0'/0'");
        
        _vanillaAccountKey = _masterKey.Derive(vanillaPath);
        _coloredAccountKey = _masterKey.Derive(coloredPath);
        
        XpubVanilla = _vanillaAccountKey.Neuter().ToString(network);
        XpubColored = _coloredAccountKey.Neuter().ToString(network);
        
        PreDeriveKeys();
        
        ClearMnemonicFromMemory(mnemonic);
    }
    
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    static void ClearMnemonicFromMemory(string mnemonic)
    {
        unsafe
        {
            fixed (char* ptr = mnemonic)
            {
                for (int i = 0; i < mnemonic.Length; i++)
                    ptr[i] = '\0';
            }
        }
    }
    
    void PreDeriveKeys()
    {
        for (int i = 0; i < PreDeriveCount; i++)
        {
            CacheKey(_vanillaAccountKey, $"0/{i}");
            CacheKey(_vanillaAccountKey, $"1/{i}");
            CacheKey(_coloredAccountKey, $"0/{i}");
            CacheKey(_coloredAccountKey, $"1/{i}");
        }
    }
    
    void CacheKey(ExtKey? accountKey, string subPath)
    {
        if (accountKey == null) return;
        var fullPath = $"{accountKey.GetPublicKey().GetHDFingerPrint()}/{subPath}";
        _derivedKeys[fullPath] = accountKey.Derive(new KeyPath(subPath));
    }
    
    public Task<string> SignPsbtAsync(string psbtBase64, Network network, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        
        var psbt = PSBT.Parse(psbtBase64.Trim('"'), network);
        
        foreach (var input in psbt.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SignInput(psbt, input);
        }
        
        psbt.TryFinalize(out _);
        return Task.FromResult(psbt.ToBase64());
    }
    
    void SignInput(PSBT psbt, PSBTInput input)
    {
        if (input.HDKeyPaths.Count > 0)
        {
            SignWithHDPaths(psbt, input);
        }
        else
        {
            SignWithPreDerivedKeys(psbt, input);
        }
    }
    
    void SignWithHDPaths(PSBT psbt, PSBTInput input)
    {
        if (_masterKey == null) return;
        
        foreach (var hdKeyPath in input.HDKeyPaths)
        {
            var fingerprint = hdKeyPath.Value.MasterFingerprint.ToString();
            
            if (!fingerprint.Equals(MasterFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            
            var derivedKey = _masterKey.Derive(hdKeyPath.Value.KeyPath);
            psbt.SignWithKeys(derivedKey);
        }
    }
    
    void SignWithPreDerivedKeys(PSBT psbt, PSBTInput input)
    {
        foreach (var key in _derivedKeys.Values)
        {
            psbt.SignWithKeys(key);
            
            if (InputIsSigned(input))
                return;
        }
        
        if (!InputIsSigned(input))
        {
            ExtendAndSign(psbt, input);
        }
    }
    
    void ExtendAndSign(PSBT psbt, PSBTInput input)
    {
        if (_vanillaAccountKey == null || _coloredAccountKey == null) return;
        
        for (int i = PreDeriveCount; i < PreDeriveCount + 10; i++)
        {
            var paths = new[] { $"0/{i}", $"1/{i}" };
            var accounts = new[] { _vanillaAccountKey, _coloredAccountKey };
            
            foreach (var account in accounts)
            {
                foreach (var path in paths)
                {
                    var key = account.Derive(new KeyPath(path));
                    psbt.SignWithKeys(key);
                    
                    if (InputIsSigned(input))
                        return;
                }
            }
        }
    }
    
    static bool InputIsSigned(PSBTInput input) =>
        input.PartialSigs.Count > 0 || input.FinalScriptSig != null || input.FinalScriptWitness != null;
    
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
        _derivedKeys.Clear();
        _masterKey = null;
        _vanillaAccountKey = null;
        _coloredAccountKey = null;
        
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
    }
}
