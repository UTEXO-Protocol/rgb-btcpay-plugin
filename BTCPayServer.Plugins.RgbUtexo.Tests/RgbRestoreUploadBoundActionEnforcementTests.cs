using System.IO.Compression;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RgbRestoreUploadBoundActionEnforcementTests
{
    const string ValidBip39TestVector =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    static RGBConfiguration ConfigurationWithDefaultUploadBound() =>
        new(Path.Combine(Path.GetTempPath(), "rgb-restore-upload-bound-action-tests"));

    static RGBController BuildControllerOnAHostWithNoRequestBodySizeFeature(RGBConfiguration cfg)
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
            cfg: cfg,
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        Assert.Null(httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    static IFormFile BackupArchiveWhoseMultipartHeaderDeclares(long declaredLength)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("backup-marker").Open())
            entry.Write(new byte[] { 1, 2, 3, 4 });
        var bytes = buffer.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, declaredLength, "BackupFile", "wallet.rgb");
    }

    static string? BackupFileErrorMessage(RGBController controller) =>
        controller.ModelState.TryGetValue(nameof(RGBSetupViewModel.BackupFile), out var entry)
        && entry != null && entry.Errors.Count > 0
            ? entry.Errors[0].ErrorMessage
            : null;

    [Fact]
    public async Task RestoreFromBackupItselfRefusesAnOversizedUploadWhenNoFrameworkBoundCanFire()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var bound = RgbRestoreUploadBound.ResolveBytes(cfg);
        var controller = BuildControllerOnAHostWithNoRequestBodySizeFeature(cfg);
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = ValidBip39TestVector,
            BackupPassword = null,
            BackupFile = BackupArchiveWhoseMultipartHeaderDeclares(bound + 1)
        };

        var result = await controller.RestoreFromBackup(storeId: "test-store", model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Setup", view.ViewName);
        var observed = BackupFileErrorMessage(controller);
        Assert.True(
            observed == RgbRestoreUploadBound.RefusalMessage(bound),
            $"RestoreFromBackup answered an upload declaring {bound + 1} bytes against a {bound}-byte bound "
            + $"with BackupFile error [{observed ?? "no BackupFile error at all"}] instead of the bound's "
            + "refusal message, so the action itself no longer "
            + "consults RgbRestoreUploadBound. The BoundRgbBackupUploadToConfiguredLimit filter cannot cover "
            + "this: it reads IHttpMaxRequestBodySizeFeature, which is absent on hosts that do not supply it "
            + "and useless when a reverse proxy rewrote the body so the framework-level bound never fired. "
            + "Without the in-action check those operators get an opaque framework failure instead of a "
            + "message naming the limit and the environment variable that raises it.");
    }

    [Fact]
    public async Task RestoreFromBackupDoesNotRefuseAnUploadExactlyAtTheBound()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var bound = RgbRestoreUploadBound.ResolveBytes(cfg);
        var controller = BuildControllerOnAHostWithNoRequestBodySizeFeature(cfg);
        var model = new RGBSetupViewModel
        {
            AcknowledgesCustodialRisk = true,
            Mnemonic = ValidBip39TestVector,
            BackupPassword = null,
            BackupFile = BackupArchiveWhoseMultipartHeaderDeclares(bound)
        };

        var result = await controller.RestoreFromBackup(storeId: "test-store", model);

        Assert.IsType<ViewResult>(result);
        Assert.Null(BackupFileErrorMessage(controller));
        Assert.True(
            controller.ModelState.TryGetValue(nameof(RGBSetupViewModel.BackupPassword), out var password)
            && password != null && password.Errors.Count > 0,
            $"an upload of exactly {bound} bytes did not reach the checks that follow the upload bound, so "
            + "the in-action bound refuses a backup at the limit the refusal message tells operators is "
            + "allowed and raising the limit would not help them");
    }
}
