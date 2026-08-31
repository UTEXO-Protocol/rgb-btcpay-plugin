using BTCPayServer.Plugins.RgbUtexo.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

public static class RgbAuthorizedStoreScope
{
    public const string RouteStoreIdKey = "storeId";

    public static bool RouteMatchesAuthorizedStore(string? routeStoreId, string? authorizedStoreId)
        => !string.IsNullOrEmpty(routeStoreId)
           && !string.IsNullOrEmpty(authorizedStoreId)
           && string.Equals(routeStoreId, authorizedStoreId, StringComparison.Ordinal);

    public static string? RouteStoreId(RouteValueDictionary routeValues)
        => routeValues.TryGetValue(RouteStoreIdKey, out var value) ? value as string : null;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RefuseUnlessRouteStoreIsTheAuthorizedStoreAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var routeStoreId = RgbAuthorizedStoreScope.RouteStoreId(context.RouteData.Values);
        var authorizedStoreId = context.HttpContext.GetStoreDataOrNull()?.Id;

        if (!RgbAuthorizedStoreScope.RouteMatchesAuthorizedStore(routeStoreId, authorizedStoreId))
        {
            context.HttpContext.RequestServices?.GetService<ILoggerFactory>()
                ?.CreateLogger("BTCPayServer.Plugins.RgbUtexo.StoreScope")
                .LogWarning(
                    "Refusing RGB request on route store {RouteStoreId}: the authorization pipeline approved store {AuthorizedStoreId}",
                    routeStoreId, authorizedStoreId);
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is StoreViewModel model)
                model.StoreId = routeStoreId!;
        }

        base.OnActionExecuting(context);
    }
}
