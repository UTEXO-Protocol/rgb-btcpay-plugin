using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

public static class RgbRestoreUploadBound
{
    public const string EnvironmentVariableName = "RGB_RESTORE_UPLOAD_MAX_BYTES";

    public static long ResolveBytes(RGBConfiguration cfg) => Math.Clamp(
        cfg.RestoreUploadMaxBytes,
        RGBConfiguration.RestoreUploadBoundMinBytes,
        RGBConfiguration.RestoreUploadBoundMaxBytes);

    public static bool IsOverBound(long? uploadBytes, long boundBytes) =>
        uploadBytes.HasValue && uploadBytes.Value > boundBytes;

    public static string RefusalMessage(long boundBytes) =>
        $"This wallet restore upload is larger than the {boundBytes / 1024 / 1024} MB limit, so it was "
        + "refused before being read. The limit is measured over the whole upload, not the backup file "
        + "alone, so a backup file just under it can still be refused. The backup file is undamaged: "
        + $"keep it. Raise the limit by setting the {EnvironmentVariableName} environment variable (maximum "
        + $"{RGBConfiguration.RestoreUploadBoundMaxBytes / 1024 / 1024} MB) and restarting BTCPay, "
        + "then retry the restore. Archives whose contents exceed "
        + $"{RgbBackupValidator.MaxTotalUncompressedBytes / 1024 / 1024}MB uncompressed are refused by "
        + "backup validation regardless of this limit.";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class BoundRgbBackupUploadToConfiguredLimitAttribute
    : Attribute, IAuthorizationFilter, IOrderedFilter
{
    public int Order { get; set; }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var cfg = context.HttpContext.RequestServices.GetRequiredService<RGBConfiguration>();
        var boundBytes = RgbRestoreUploadBound.ResolveBytes(cfg);

        var bodySizeFeature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is { IsReadOnly: false })
            bodySizeFeature.MaxRequestBodySize = boundBytes;

        if (!RgbRestoreUploadBound.IsOverBound(context.HttpContext.Request.ContentLength, boundBytes))
            return;

        context.Result = new ContentResult
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
            ContentType = "text/plain",
            Content = RgbRestoreUploadBound.RefusalMessage(boundBytes)
        };
    }
}
