using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

internal sealed class RgbUploadConcurrencySlots
{
    internal int InFlight;
}

internal sealed class RgbUploadSlotLease
{
    readonly RgbUploadConcurrencySlots _slots;
    int _released;

    internal RgbUploadSlotLease(RgbUploadConcurrencySlots slots) => _slots = slots;

    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            RgbRestoreUploadConcurrencyGate.Exit(_slots);
    }
}

public static class RgbRestoreUploadConcurrencyGate
{
    public const string EnvironmentVariableName = "RGB_RESTORE_UPLOAD_MAX_CONCURRENT_UPLOADS";

    internal const string LeaseItemKey = "RgbRestoreUploadSlotLease";

    internal const int OrderThatRunsBeforeEveryFilterThatCouldReadTheMultipartForm = -1_000;

    static readonly RgbUploadConcurrencySlots ProcessWideSlots = new();

    public static int ResolveMaxConcurrentUploads(RGBConfiguration cfg) => Math.Clamp(
        cfg.RestoreUploadMaxConcurrentUploads,
        RGBConfiguration.RestoreUploadMaxConcurrentUploadsMin,
        RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax);

    public static string RefusalMessage(int maxConcurrentUploads) =>
        "Too many wallet restore uploads are already being processed at once (the limit is "
        + $"{maxConcurrentUploads} at a time), so this one was refused before its file was parsed or "
        + "spooled to disk. Wait for "
        + "one of the others to finish or fail, then retry — this refusal clears itself and does not need "
        + $"an operator to act. Raise the limit by setting the {EnvironmentVariableName} environment "
        + $"variable (maximum {RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax}) and restarting BTCPay.";

    public static bool TryEnter(int maxConcurrentUploads) => TryEnter(ProcessWideSlots, maxConcurrentUploads);

    public static void Exit() => Exit(ProcessWideSlots);

    internal static RgbUploadSlotLease? TryLease(RgbUploadConcurrencySlots slots, int maxConcurrentUploads) =>
        TryEnter(slots, maxConcurrentUploads) ? new RgbUploadSlotLease(slots) : null;

    internal static RgbUploadSlotLease? TryLease(int maxConcurrentUploads) =>
        TryLease(ProcessWideSlots, maxConcurrentUploads);

    internal static bool TryEnter(RgbUploadConcurrencySlots slots, int maxConcurrentUploads)
    {
        while (true)
        {
            var observed = Volatile.Read(ref slots.InFlight);
            if (observed >= maxConcurrentUploads)
                return false;
            if (Interlocked.CompareExchange(ref slots.InFlight, observed + 1, observed) == observed)
                return true;
        }
    }

    internal static void Exit(RgbUploadConcurrencySlots slots) => Interlocked.Decrement(ref slots.InFlight);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class BoundRgbBackupUploadConcurrencyAttribute
    : Attribute, IAuthorizationFilter, IAsyncResourceFilter, IOrderedFilter
{
    public int Order { get; set; } =
        RgbRestoreUploadConcurrencyGate.OrderThatRunsBeforeEveryFilterThatCouldReadTheMultipartForm;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var cfg = context.HttpContext.RequestServices.GetRequiredService<RGBConfiguration>();
        var maxConcurrentUploads = RgbRestoreUploadConcurrencyGate.ResolveMaxConcurrentUploads(cfg);

        var lease = RgbRestoreUploadConcurrencyGate.TryLease(maxConcurrentUploads);
        if (lease == null)
        {
            context.Result = new ContentResult
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                ContentType = "text/plain",
                Content = RgbRestoreUploadConcurrencyGate.RefusalMessage(maxConcurrentUploads)
            };
            return;
        }

        context.HttpContext.Items[RgbRestoreUploadConcurrencyGate.LeaseItemKey] = lease;
        context.HttpContext.Response.OnCompleted(static state =>
        {
            ((RgbUploadSlotLease)state).Release();
            return Task.CompletedTask;
        }, lease);
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var lease = context.HttpContext.Items.TryGetValue(RgbRestoreUploadConcurrencyGate.LeaseItemKey, out var stored)
            ? stored as RgbUploadSlotLease
            : null;

        try
        {
            await next();
        }
        finally
        {
            lease?.Release();
        }
    }
}
