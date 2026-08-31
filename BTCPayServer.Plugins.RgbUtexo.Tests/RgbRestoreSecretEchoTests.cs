using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RgbRestoreSecretEchoTests
{
    const string ValidBip39TestVector =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    const string BackupPassword = "not-the-real-backup-password";

    static RGBController BuildController()
    {
        var controller = new RGBController(
            wallets: new FakeRGBWalletService(),
            stores: null!,
            handlers: null!,
            db: null!,
            log: NullLogger<RGBController>.Instance,
            userManager: null!,
            events: null!,
            cache: null!,
            btcPayOptions: Options.Create(new BTCPayServerOptions()),
            rateSource: null!,
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-restore-secret-echo-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static RGBSetupViewModel AssertRedisplayCarriesNoRecoverySecrets(IActionResult result, string branch)
    {
        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Setup", view.ViewName);
        var model = Assert.IsType<RGBSetupViewModel>(view.Model);
        Assert.True(string.IsNullOrEmpty(model.Mnemonic),
            $"the {branch} redisplay still carries the recovery phrase on the view model, so the "
            + "restore-error HTML would echo the operator's BIP39 phrase back in a 200 response body");
        Assert.True(string.IsNullOrEmpty(model.BackupPassword),
            $"the {branch} redisplay still carries the backup password on the view model");
        return model;
    }

    static void AssertModelStateHoldsNoSubmittedSecretValue(RGBController controller, string branch)
    {
        foreach (var field in new[] { nameof(RGBSetupViewModel.Mnemonic), nameof(RGBSetupViewModel.BackupPassword) })
        {
            if (!controller.ModelState.TryGetValue(field, out var entry) || entry == null)
                continue;
            Assert.True(entry.AttemptedValue == null,
                $"the {branch} redisplay left the submitted {field} in ModelState, so any tag helper "
                + "bound to that field would re-render the secret even though the view model was cleared");
            Assert.True(entry.RawValue == null,
                $"the {branch} redisplay left the raw submitted {field} in ModelState");
        }
    }

    [Fact]
    public async Task RestoreWallet_WithoutConsent_DoesNotEchoRecoveryPhrase()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = false,
            Mnemonic = ValidBip39TestVector
        };

        var result = await controller.RestoreWallet(storeId: "test-store", model);

        var returned = AssertRedisplayCarriesNoRecoverySecrets(result, "missing-consent");
        Assert.True(returned.IsRestore);
        AssertModelStateHoldsNoSubmittedSecretValue(controller, "missing-consent");
    }

    [Fact]
    public async Task RestoreWallet_WithInvalidPhrase_KeepsTheErrorButDropsThePhrase()
    {
        var controller = BuildController();
        controller.ModelState.SetModelValue(nameof(RGBSetupViewModel.Mnemonic),
            "these words are not a bip39 phrase", "these words are not a bip39 phrase");
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = "these words are not a bip39 phrase"
        };

        var result = await controller.RestoreWallet(storeId: "test-store", model);

        AssertRedisplayCarriesNoRecoverySecrets(result, "invalid-phrase");
        AssertModelStateHoldsNoSubmittedSecretValue(controller, "invalid-phrase");
        Assert.True(
            controller.ModelState.TryGetValue(nameof(RGBSetupViewModel.Mnemonic), out var entry)
            && entry!.Errors.Count > 0,
            "dropping the submitted phrase also destroyed the validation error, so the operator would "
            + "get an empty form with no explanation of why the restore was refused");
    }

    [Fact]
    public async Task RestoreWallet_WithInvalidNetworkSelection_DoesNotEchoRecoveryPhrase()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = ValidBip39TestVector,
            SelectedNetwork = "not-a-network"
        };

        var result = await controller.RestoreWallet(storeId: "test-store", model);

        AssertRedisplayCarriesNoRecoverySecrets(result, "invalid-network-selection");
        AssertModelStateHoldsNoSubmittedSecretValue(controller, "invalid-network-selection");
    }

    [Fact]
    public async Task RestoreFromBackup_WithoutBackupFile_DropsPhraseAndBackupPassword()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = ValidBip39TestVector,
            BackupPassword = BackupPassword,
            BackupFile = null
        };

        var result = await controller.RestoreFromBackup(storeId: "test-store", model);

        var returned = AssertRedisplayCarriesNoRecoverySecrets(result, "missing-backup-file");
        Assert.True(returned.IsBackupRestore);
        AssertModelStateHoldsNoSubmittedSecretValue(controller, "missing-backup-file");
    }

    [Fact]
    public async Task RestoreFromBackup_WithoutPassword_KeepsTheErrorButDropsThePhrase()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = ValidBip39TestVector,
            BackupPassword = null,
            BackupFile = MinimalValidBackupArchive()
        };

        var result = await controller.RestoreFromBackup(storeId: "test-store", model);

        AssertRedisplayCarriesNoRecoverySecrets(result, "missing-backup-password");
        AssertModelStateHoldsNoSubmittedSecretValue(controller, "missing-backup-password");
        Assert.True(controller.ModelState.ErrorCount > 0,
            "the missing-password redisplay lost every error, so the operator gets no reason for the refusal");
    }

    static IFormFile MinimalValidBackupArchive()
    {
        var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("backup-marker").Open())
            entry.Write(new byte[] { 1, 2, 3, 4 });
        var bytes = buffer.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "BackupFile", "wallet.rgb");
    }

    [Fact]
    public void EverySetupRedisplayRoutesThroughTheSecretDroppingHelper()
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, "Controllers", "RGBController.cs");
        Assert.True(File.Exists(path), "Controllers/RGBController.cs is missing; it holds the restore redisplay paths");
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path);
        var root = tree.GetRoot();

        foreach (var actionName in new[] { "SetupWallet", "RestoreWallet", "RestoreFromBackup" })
        {
            var action = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .SingleOrDefault(m => m.Identifier.Text == actionName);
            Assert.True(action != null, $"{actionName} is absent from RGBController; the redisplay pin cannot be checked");

            var redisplays = action!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(i => i.ToString().StartsWith("View(\"Setup\", model)", StringComparison.Ordinal));
            var drops = action.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(i => i.ToString().StartsWith("PopulateSetupModelAndDropRecoverySecrets(model)", StringComparison.Ordinal));

            Assert.True(redisplays == drops,
                $"{actionName} re-renders the Setup view with the posted model {redisplays} time(s) but only "
                + $"clears the recovery phrase and backup password {drops} time(s); every redisplay of a posted "
                + "model must route through PopulateSetupModelAndDropRecoverySecrets or the phrase is echoed "
                + "back in the response body");
        }
    }

    [Fact]
    public void BothRestoreActionsForbidResponseStorage()
    {
        foreach (var actionName in new[] { "SetupWallet", "RestoreWallet", "RestoreFromBackup" })
        {
            var action = typeof(RGBController).GetMethod(actionName);
            Assert.True(action != null, $"{actionName} is absent from RGBController");
            var cache = action!.GetCustomAttributes(typeof(ResponseCacheAttribute), inherit: false)
                .Cast<ResponseCacheAttribute>().SingleOrDefault();
            Assert.True(cache != null,
                $"{actionName} has no ResponseCache attribute; its error redisplay is a 200 whose body once "
                + "carried the operator's recovery phrase and nothing tells response-side layers not to retain it");
            Assert.True(cache!.NoStore,
                $"{actionName} does not set NoStore, so its response may be retained by caches and proxies");
            Assert.True(cache.Location == ResponseCacheLocation.None,
                $"{actionName} does not set Location=None, so a shared cache may still store the response");
        }
    }
}
