using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RGB.Services;

public class MnemonicProtectionService
{
    readonly IDataProtector _protector;
    readonly ILogger<MnemonicProtectionService> _log;
    const string Purpose = "BTCPayServer.Plugins.RGB.MnemonicProtection.v1";

    public MnemonicProtectionService(IDataProtectionProvider provider, ILogger<MnemonicProtectionService> log)
    {
        _protector = provider.CreateProtector(Purpose);
        _log = log;
    }

    public string Protect(string mnemonic)
    {
        if (string.IsNullOrEmpty(mnemonic))
            return mnemonic;
        
        return _protector.Protect(mnemonic);
    }

    public string Unprotect(string protectedMnemonic)
    {
        if (string.IsNullOrEmpty(protectedMnemonic))
            return protectedMnemonic;

        try
        {
            return _protector.Unprotect(protectedMnemonic);
        }
        catch (Exception ex)
        {
            if (IsValidBip39Mnemonic(protectedMnemonic))
            {
                _log.LogWarning("Found unencrypted mnemonic in database - this wallet was created before encryption was enabled. Re-save wallet to encrypt.");
                return protectedMnemonic;
            }
            
            _log.LogError(ex, "Failed to decrypt mnemonic - data may be corrupted or DataProtection keys may have changed");
            throw new InvalidOperationException("Failed to decrypt mnemonic. The DataProtection keys may have changed or the data is corrupted.", ex);
        }
    }

    static bool IsValidBip39Mnemonic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
            
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
            return false;
        
        foreach (var word in words)
        {
            if (word.Length < 3 || word.Length > 8)
                return false;
            
            foreach (var c in word)
            {
                if (c < 'a' || c > 'z')
                    return false;
            }
        }
        
        return true;
    }
}
