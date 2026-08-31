using System.Runtime.InteropServices;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbLibWrapperStringFreeTests
{
    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgblib_string_free(IntPtr ptr);

    const string TestVectorMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    static (RgbLibService Svc, List<IntPtr> Freed) BuildAgainstTheRealNativeLibrary()
    {
        var freed = new List<IntPtr>();
        var svc = RgbLibServiceTestFactory.Create(
            typeof(CResultString),
            p => { freed.Add(p); rgblib_string_free(p); },
            Marshal.PtrToStringUTF8);
        return (svc, freed);
    }

    [Fact]
    public void GenerateKeysFreesTheNativeMnemonicStringExactlyOnce()
    {
        var (svc, freed) = BuildAgainstTheRealNativeLibrary();

        var keys = svc.GenerateKeys("Regtest");

        Assert.False(string.IsNullOrWhiteSpace(keys.Mnemonic));
        Assert.False(string.IsNullOrWhiteSpace(keys.AccountXpubVanilla));
        Assert.Single(freed);
    }

    [Fact]
    public void RestoreKeysFromMnemonicFreesTheNativeKeyMaterialStringExactlyOnce()
    {
        var (svc, freed) = BuildAgainstTheRealNativeLibrary();

        var keys = svc.RestoreKeysFromMnemonic(TestVectorMnemonic, "Regtest");

        Assert.False(string.IsNullOrWhiteSpace(keys.AccountXpubVanilla));
        Assert.False(string.IsNullOrWhiteSpace(keys.AccountXpubColored));
        Assert.Single(freed);
    }

    [Fact]
    public void BackupFreesTheNativeErrorStringExactlyOnceOnFailure()
    {
        var (svc, freed) = BuildAgainstTheRealNativeLibrary();

        using var keys = JsonDocument.Parse(RgbLibWallet.GenerateKeys("Regtest"));
        var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-backup-leak-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            var walletConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["data_dir"] = dataDir,
                ["bitcoin_network"] = "Regtest",
                ["database_type"] = "Sqlite",
                ["max_allocations_per_utxo"] = 5,
                ["supported_schemas"] = new[] { "Nia", "Cfa" }
            });
            var keysConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["account_xpub_vanilla"] = keys.RootElement.GetProperty("account_xpub_vanilla").GetString(),
                ["account_xpub_colored"] = keys.RootElement.GetProperty("account_xpub_colored").GetString(),
                ["master_fingerprint"] = keys.RootElement.GetProperty("master_fingerprint").GetString(),
                ["vanilla_keychain"] = (int?)null,
                ["mnemonic"] = (string?)null
            });

            using var wallet = new RgbLibWallet(walletConfig, keysConfig);
            var backupPathThatIsActuallyADirectory = Path.Combine(dataDir, "backup.rgb");
            Directory.CreateDirectory(backupPathThatIsActuallyADirectory);

            var ex = Assert.Throws<BTCPayServer.Plugins.RgbUtexo.Services.RgbLibException>(
                () => svc.Backup(wallet, backupPathThatIsActuallyADirectory, "irrelevant-password"));

            Assert.Equal("Failed to backup", ex.Message);
            Assert.Single(freed);
        }
        finally
        {
            Directory.Delete(dataDir, true);
        }
    }

    [Fact]
    public void GoOnlineWritesTheReflectedNativeResultOntoTheWalletsOnlineJsonField()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var model = plugin.Model(tree);
        var method = RoslynPins.Method(tree, "RgbLibService", "GoOnline");
        var body = RoslynPins.BodyOf(method);

        var setCalls = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => model.GetSymbolInfo(i).Symbol is IMethodSymbol
            {
                Name: "SetValue",
                ContainingType.Name: "FieldInfo"
            } && i.Expression is MemberAccessExpressionSyntax member
                     && member.Expression is IdentifierNameSyntax { Identifier.ValueText: "_onlineJsonField" })
            .ToList();
        Assert.True(setCalls.Count == 1,
            $"GoOnline must write the wallet's _onlineJson field exactly once, found {setCalls.Count} call(s) "
            + "— every one of the nine other call sites in this file reads that field and throws \"Wallet is "
            + "offline\" if it is null, so a missed write-back is a total functional break of every wallet");

        var writtenValue = Assert.IsType<IdentifierNameSyntax>(
            setCalls[0].ArgumentList.Arguments[1].Expression).Identifier.ValueText;

        var declarator = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .SingleOrDefault(v => v.Identifier.ValueText == writtenValue);
        Assert.True(declarator?.Initializer != null,
            $"'{writtenValue}' is written onto _onlineJsonField but is not a locally-initialized variable in GoOnline");

        var initializer = declarator!.Initializer!.Value;
        var requireCall = Assert.IsType<InvocationExpressionSyntax>(initializer);
        Assert.True(model.GetSymbolInfo(requireCall).Symbol is IMethodSymbol { Name: "Require" },
            $"'{writtenValue}' must come from Require(...), so a native error or an empty payload throws instead "
            + $"of being written onto _onlineJson, found '{requireCall}'");

        var readNativeResultArgument = requireCall.ArgumentList.Arguments[0].Expression;
        var readNativeResultCall = Assert.IsType<InvocationExpressionSyntax>(readNativeResultArgument);
        Assert.True(model.GetSymbolInfo(readNativeResultCall).Symbol is IMethodSymbol { Name: "ReadNativeResult" },
            "Require's argument in GoOnline must be a direct ReadNativeResult(...) call, so the reflected "
            + "rgblib_go_online payload is read and freed through the file's one matched deallocator rather "
            + "than through the leaking package wrapper");
    }
}
