using System.Reflection;
using BTCPayServer;
using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbStoreScopeGuardTests
{
    const string AttackerStore = "AttackerStoreId";
    const string VictimStore = "VictimStoreId";

    static ActionExecutingContext Context(
        string? routeStoreId, string? authorizedStoreId, object? model = null)
    {
        var http = new DefaultHttpContext();
        if (authorizedStoreId != null)
            http.SetStoreData(new StoreData { Id = authorizedStoreId });

        var routeData = new RouteData();
        if (routeStoreId != null)
            routeData.Values[RgbAuthorizedStoreScope.RouteStoreIdKey] = routeStoreId;

        var arguments = new Dictionary<string, object?>();
        if (model != null) arguments["model"] = model;

        return new ActionExecutingContext(
            new ActionContext(http, routeData, new ActionDescriptor()),
            new List<IFilterMetadata>(),
            arguments,
            controller: new object());
    }

    static void Run(ActionExecutingContext context) =>
        new RefuseUnlessRouteStoreIsTheAuthorizedStoreAttribute().OnActionExecuting(context);

    [Fact]
    public void AFormShadowedStoreIdCannotReachTheActionBecauseTheModelPropertyIsNotBindable()
    {
        var property = typeof(StoreViewModel).GetProperty(nameof(StoreViewModel.StoreId))!;
        Assert.NotNull(property.GetCustomAttribute<BindNeverAttribute>());
        Assert.NotNull(property.GetCustomAttribute<ValidateNeverAttribute>());
    }

    [Fact]
    public void EveryRgbActionBindsStoreIdFromTheRouteSoTheFormCannotChooseTheTargetStore()
    {
        var parameters = typeof(RGBController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetParameters().Select(p => (Method: m, Parameter: p)))
            .Where(x => x.Parameter.Name == RgbAuthorizedStoreScope.RouteStoreIdKey)
            .ToList();

        Assert.True(parameters.Count >= 20,
            $"expected the RGB controller to expose many storeId-scoped actions, found {parameters.Count} — "
            + "a shrunken set means this pin no longer covers the takeover surface");

        var unbound = parameters
            .Where(x => x.Parameter.GetCustomAttribute<FromRouteAttribute>() == null)
            .Select(x => x.Method.Name)
            .ToList();
        Assert.True(unbound.Count == 0,
            "these actions bind storeId through the default provider chain, where the FORM beats the "
            + "ROUTE, so a request body can retarget them at another merchant's wallet: "
            + string.Join(", ", unbound));
    }

    [Fact]
    public void TheControllerCarriesTheStoreScopeGuardSoNoActionCanForgetIt()
    {
        Assert.NotNull(typeof(RGBController)
            .GetCustomAttribute<RefuseUnlessRouteStoreIsTheAuthorizedStoreAttribute>());
    }

    [Fact]
    public void ARouteStoreOtherThanTheAuthorizedStoreIsRefusedWith403AndNeverReachesTheAction()
    {
        var context = Context(routeStoreId: VictimStore, authorizedStoreId: AttackerStore);
        Run(context);

        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public void AMissingAuthorizedStoreIsRefusedRatherThanTrusted()
    {
        var context = Context(routeStoreId: VictimStore, authorizedStoreId: null);
        Run(context);

        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public void AMissingRouteStoreIsRefusedRatherThanFallingBackToTheRequestBody()
    {
        var context = Context(routeStoreId: null, authorizedStoreId: AttackerStore);
        Run(context);

        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Fact]
    public void TheMatchingRouteStoreIsAdmittedAndStampedOntoTheModelSoViewsStillRenderTheirFormActions()
    {
        var model = new RGBSendAssetViewModel();
        var context = Context(routeStoreId: AttackerStore, authorizedStoreId: AttackerStore, model);
        Run(context);

        Assert.Null(context.Result);
        Assert.Equal(AttackerStore, model.StoreId);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("s1", null)]
    [InlineData(null, "s1")]
    [InlineData("s1", "")]
    [InlineData("", "s1")]
    [InlineData("s1", "s2")]
    [InlineData("s1", "S1")]
    public void AStoreScopeIsOnlyAcceptedOnAnExactNonEmptyMatch(string? routeStoreId, string? authorizedStoreId)
    {
        Assert.False(
            RgbAuthorizedStoreScope.RouteMatchesAuthorizedStore(routeStoreId, authorizedStoreId),
            $"'{routeStoreId}' must not be treated as the authorized store '{authorizedStoreId}'");
    }

    [Fact]
    public void AnIdenticalStoreScopeIsAccepted()
    {
        Assert.True(RgbAuthorizedStoreScope.RouteMatchesAuthorizedStore(AttackerStore, AttackerStore));
    }
}
