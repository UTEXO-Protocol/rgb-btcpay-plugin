using System.Runtime.InteropServices;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbReflectedKeyDerivationParityTests
{
    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgblib_string_free(IntPtr ptr);

    const string TestVectorMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    static RgbLibService BuildAgainstTheRealNativeLibrary() =>
        RgbLibServiceTestFactory.Create(
            typeof(CResultString), rgblib_string_free, Marshal.PtrToStringUTF8);

    [Theory]
    [InlineData("Regtest", "Regtest")]
    [InlineData("Testnet", "Testnet")]
    [InlineData("Mainnet", "Mainnet")]
    [InlineData("Signet", "Signet")]
    [InlineData("utexo", "Signet")]
    public void RestoreKeysReflectedWithAnExplicitWitnessVersion_DerivesIdenticallyToThePackageOverloadItReplaced(
        string network, string rgbLibNetwork)
    {
        using var packageKeys = JsonDocument.Parse(
            RgbLibWallet.RestoreKeys(rgbLibNetwork, TestVectorMnemonic));
        var packageVanilla = packageKeys.RootElement.GetProperty("account_xpub_vanilla").GetString();
        var packageColored = packageKeys.RootElement.GetProperty("account_xpub_colored").GetString();
        var packageFingerprint = packageKeys.RootElement.GetProperty("master_fingerprint").GetString();

        var reflected = BuildAgainstTheRealNativeLibrary().RestoreKeysFromMnemonic(TestVectorMnemonic, network);

        Assert.True(packageVanilla == reflected.AccountXpubVanilla,
            $"{network} (rgb-lib {rgbLibNetwork}): the reflected rgblib_restore_keys call derived vanilla account xpub "
            + $"'{reflected.AccountXpubVanilla}' where the package overload it replaced derives "
            + $"'{packageVanilla}'. The reflected call passes an EXPLICIT witness_version literal that the "
            + "package supplied implicitly; if that literal ever stops matching, every restored wallet "
            + "derives a different account and cannot see its own funds.");
        Assert.True(packageColored == reflected.AccountXpubColored,
            $"{network} (rgb-lib {rgbLibNetwork}): reflected colored account xpub '{reflected.AccountXpubColored}' does not match the "
            + $"package overload's '{packageColored}' — same witness_version divergence, same fund loss.");
        Assert.True(packageFingerprint == reflected.MasterFingerprint,
            $"{network} (rgb-lib {rgbLibNetwork}): reflected master fingerprint '{reflected.MasterFingerprint}' does not match the "
            + $"package overload's '{packageFingerprint}'.");
    }

    [Fact]
    public void GenerateKeysReflectedWithAnExplicitWitnessVersion_ProducesAMnemonicThatRestoresToTheSameAccounts()
    {
        var generated = BuildAgainstTheRealNativeLibrary().GenerateKeys("Regtest");

        using var restoredByPackage = JsonDocument.Parse(
            RgbLibWallet.RestoreKeys("Regtest", generated.Mnemonic));

        Assert.True(
            restoredByPackage.RootElement.GetProperty("account_xpub_vanilla").GetString()
                == generated.AccountXpubVanilla,
            "the reflected rgblib_generate_keys call returned an account xpub that the package's own "
            + "restore of the very same mnemonic does not reproduce, so generate and restore are deriving "
            + "under different witness versions — a wallet created this way could never be restored.");
        Assert.True(
            restoredByPackage.RootElement.GetProperty("account_xpub_colored").GetString()
                == generated.AccountXpubColored,
            "the reflected rgblib_generate_keys call returned a colored account xpub that the package's "
            + "restore of the same mnemonic does not reproduce.");
    }
}
