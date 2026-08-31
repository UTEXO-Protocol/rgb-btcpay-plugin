using BTCPayServer;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SetupConsentGateTests
{
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
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-controller-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    [Fact]
    public async Task SetupWallet_WithoutConsent_ReturnsViewWithError()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel { AcknowledgesCustodialRisk = false };

        var result = await controller.SetupWallet(storeId: "test-store", model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Setup", view.ViewName);
        var error = controller.TempData[WellKnownTempData.ErrorMessage] as string;
        Assert.NotNull(error);
        Assert.Contains("acknowledge", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreWallet_WithoutConsent_ReturnsViewWithError()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel { AcknowledgesCustodialRisk = false };

        var result = await controller.RestoreWallet(storeId: "test-store", model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Setup", view.ViewName);
        var returnedModel = Assert.IsType<RGBSetupViewModel>(view.Model);
        Assert.True(returnedModel.IsRestore);
        var error = controller.TempData[WellKnownTempData.ErrorMessage] as string;
        Assert.NotNull(error);
        Assert.Contains("acknowledge", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreFromBackup_WithoutConsent_ReturnsViewWithError()
    {
        var controller = BuildController();
        var model = new RGBSetupViewModel { AcknowledgesCustodialRisk = false };

        var result = await controller.RestoreFromBackup(storeId: "test-store", model);

        var view = Assert.IsType<ViewResult>(result);
        Assert.Equal("Setup", view.ViewName);
        var returnedModel = Assert.IsType<RGBSetupViewModel>(view.Model);
        Assert.True(returnedModel.IsBackupRestore);
        var error = controller.TempData[WellKnownTempData.ErrorMessage] as string;
        Assert.NotNull(error);
        Assert.Contains("acknowledge", error, StringComparison.OrdinalIgnoreCase);
    }
}
