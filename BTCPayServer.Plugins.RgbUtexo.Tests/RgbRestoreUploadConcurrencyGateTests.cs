using System.Reflection;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

sealed class ResponseFeatureThatRecordsCompletionCallbacks : IHttpResponseFeature
{
    readonly List<(Func<object, Task> Callback, object State)> _onCompleted = new();

    public int StatusCode { get; set; } = 200;
    public string? ReasonPhrase { get; set; }
    public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
    public Stream Body { get; set; } = Stream.Null;
    public bool HasStarted => false;

    public void OnStarting(Func<object, Task> callback, object state) { }

    public void OnCompleted(Func<object, Task> callback, object state) => _onCompleted.Add((callback, state));

    public async Task FireCompletionAsync()
    {
        foreach (var (callback, state) in _onCompleted)
            await callback(state);
    }
}

[Collection("RestoreSerial")]
public class RgbRestoreUploadConcurrencyGateTests
{
    static RgbUploadConcurrencySlots FreshSlots() => new();

    static RGBConfiguration ConfigurationWithConcurrentUploadsCappedAt(int max) =>
        new(Path.Combine(Path.GetTempPath(), "rgb-restore-upload-concurrency-gate-tests"))
        {
            RestoreUploadMaxConcurrentUploads = max
        };

    static (DefaultHttpContext Http, ResponseFeatureThatRecordsCompletionCallbacks Response) HttpContextFor(
        RGBConfiguration cfg)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        var response = new ResponseFeatureThatRecordsCompletionCallbacks();
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Features.Set<IHttpResponseFeature>(response);
        return (http, response);
    }

    static AuthorizationFilterContext AuthorizationContext(HttpContext http) =>
        new(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>());

    static ResourceExecutingContext ResourceContext(HttpContext http) =>
        new(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new List<Microsoft.AspNetCore.Mvc.ModelBinding.IValueProviderFactory>());

    static ResourceExecutionDelegate NextThatRecordsInvocationAndReturns(HttpContext http, Action onInvoked) => () =>
    {
        onInvoked();
        return Task.FromResult(new ResourceExecutedContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>()));
    };

    static ResourceExecutionDelegate NextThatThrows(Exception ex) => () => throw ex;

    [Fact]
    public void TheGateAdmitsExactlyTheConfiguredNumberOfConcurrentUploadsThenRefuses()
    {
        var slots = FreshSlots();

        Assert.True(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 2));
        Assert.True(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 2));
        Assert.False(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 2),
            "a third sequential entry was admitted against a configured limit of 2, so nothing bounds how "
            + "many backup uploads buffer to disk at once");
    }

    [Fact]
    public void ExitingASlotLetsTheNextUploadIn()
    {
        var slots = FreshSlots();
        Assert.True(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 1));
        Assert.False(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 1),
            "the gate admitted a second upload while the first slot was still held");

        RgbRestoreUploadConcurrencyGate.Exit(slots);

        Assert.True(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 1),
            "releasing the only held slot did not free it for the next restore upload, which would strand "
            + "every future restore behind one that already finished — a permanent false reject on the "
            + "recovery path");
    }

    [Fact]
    public void ReleasingOneLeaseTwiceReturnsExactlyOneSlot()
    {
        var slots = FreshSlots();
        var lease = RgbRestoreUploadConcurrencyGate.TryLease(slots, maxConcurrentUploads: 1);
        Assert.NotNull(lease);

        lease!.Release();
        lease.Release();

        Assert.True(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 1));
        Assert.False(RgbRestoreUploadConcurrencyGate.TryEnter(slots, maxConcurrentUploads: 1),
            "one lease released twice returned two slots, so the request-completion callback and the "
            + "resource filter — which both release the same lease on the ordinary path — inflate the "
            + "counter above the configured limit and the bound stops bounding anything");
    }

    [Fact]
    public void TheConcurrencyBoundIsAcquiredInTheAuthorizationStageAheadOfTheFilterThatReadsTheMultipartForm()
    {
        var attribute = new BoundRgbBackupUploadConcurrencyAttribute();

        Assert.True(attribute is IAuthorizationFilter,
            "[BoundRgbBackupUploadConcurrency] no longer acquires its slot in the authorization stage. "
            + "Measured on this runtime: RGBController carries [AutoValidateAntiforgeryToken], whose filter "
            + "reads the whole multipart body (all 200,260 bytes of a probe upload) to find the form token. "
            + "Every later stage — resource filters included — therefore runs after the upload has already "
            + "spooled to disk, so a bound applied there bounds nothing");

        var antiforgeryOrder = ((IOrderedFilter)new AutoValidateAntiforgeryTokenAttribute()).Order;
        Assert.True(attribute.Order < antiforgeryOrder,
            $"the concurrency bound runs at Order {attribute.Order}, not before the antiforgery filter at "
            + $"Order {antiforgeryOrder}. Authorization filters run in Order sequence, so at this Order the "
            + "multipart body is read and spooled to disk before the bound is consulted");

        Assert.True(attribute.Order < 0,
            $"the concurrency bound runs at Order {attribute.Order}, which does not beat an Order-0 "
            + "authorization filter registered GLOBALLY. BTCPay registers exactly such a filter — "
            + "UIControllerAntiforgeryTokenAttribute, which implements IAsyncAuthorizationFilter but not "
            + "IOrderedFilter, so it defaults to Order 0 — and it calls ValidateRequestAsync, which reads "
            + "the whole multipart body. At equal Order, MVC sorts global scope before controller before "
            + "action, so that filter would run first. It is inert here only because IsEffectivePolicy "
            + "defers to RGBController's own controller-scoped [AutoValidateAntiforgeryToken]; a negative "
            + "Order makes the bound independent of which antiforgery filter happens to be effective");
    }

    [Fact]
    public async Task TheRealFilterRefusesInTheAuthorizationStageWhenTheGateIsFull()
    {
        var cfg = ConfigurationWithConcurrentUploadsCappedAt(1);
        var attribute = new BoundRgbBackupUploadConcurrencyAttribute();

        var (firstHttp, firstResponse) = HttpContextFor(cfg);
        attribute.OnAuthorization(AuthorizationContext(firstHttp));
        try
        {
            var (secondHttp, _) = HttpContextFor(cfg);
            var second = AuthorizationContext(secondHttp);

            attribute.OnAuthorization(second);

            var refusal = Assert.IsType<ContentResult>(second.Result);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
            Assert.Equal(RgbRestoreUploadConcurrencyGate.RefusalMessage(1), refusal.Content);
            Assert.False(string.IsNullOrEmpty(refusal.ContentType),
                "the refusal carries no Content-Type, so BTCPay's UseStatusCodePagesWithReExecute(\"/errors/{0}\") "
                + "re-executes the request into a generic error page and replaces this body — measured against "
                + "the shipped StatusCodePagesMiddleware, it skips re-execution only when Content-Type or "
                + "Content-Length is set. Without it the operator is told nothing about what to wait for, what "
                + "to retry, or which environment variable raises the limit");
            Assert.False(secondHttp.Items.ContainsKey(RgbRestoreUploadConcurrencyGate.LeaseItemKey),
                "a refused request was still given a slot lease, so refusing costs a slot and the gate "
                + "deadlocks itself under load");
        }
        finally
        {
            await firstResponse.FireCompletionAsync();
        }
    }

    [Fact]
    public async Task TheSlotIsReturnedWhenTheResponseCompletesEvenIfNoResourceFilterEverRuns()
    {
        var cfg = ConfigurationWithConcurrentUploadsCappedAt(1);
        var attribute = new BoundRgbBackupUploadConcurrencyAttribute();

        var (http, response) = HttpContextFor(cfg);
        attribute.OnAuthorization(AuthorizationContext(http));

        await response.FireCompletionAsync();

        var (nextHttp, nextResponse) = HttpContextFor(cfg);
        var next = AuthorizationContext(nextHttp);
        attribute.OnAuthorization(next);

        Assert.Null(next.Result);
        Assert.True(nextHttp.Items.ContainsKey(RgbRestoreUploadConcurrencyGate.LeaseItemKey),
            "the slot taken in the authorization stage was never returned when the request ended without a "
            + "resource filter — the path a rejected antiforgery token takes, since that filter short-circuits "
            + "the pipeline before any resource filter runs. Leaking there refuses every later restore on "
            + "this process forever");

        await nextResponse.FireCompletionAsync();
    }

    [Fact]
    public async Task TheSlotIsReturnedByTheResourceFilterOnSuccess()
    {
        var cfg = ConfigurationWithConcurrentUploadsCappedAt(1);
        var attribute = new BoundRgbBackupUploadConcurrencyAttribute();

        var (http, response) = HttpContextFor(cfg);
        attribute.OnAuthorization(AuthorizationContext(http));
        var nextInvoked = false;
        await attribute.OnResourceExecutionAsync(
            ResourceContext(http), NextThatRecordsInvocationAndReturns(http, () => nextInvoked = true));

        Assert.True(nextInvoked, "the concurrency filter refused an upload even though a slot was free");

        var (secondHttp, secondResponse) = HttpContextFor(cfg);
        var second = AuthorizationContext(secondHttp);
        attribute.OnAuthorization(second);

        Assert.Null(second.Result);

        await response.FireCompletionAsync();
        await secondResponse.FireCompletionAsync();
    }

    [Fact]
    public async Task TheSlotIsReturnedByTheResourceFilterEvenWhenNextThrows()
    {
        var cfg = ConfigurationWithConcurrentUploadsCappedAt(1);
        var attribute = new BoundRgbBackupUploadConcurrencyAttribute();

        var (http, response) = HttpContextFor(cfg);
        attribute.OnAuthorization(AuthorizationContext(http));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            attribute.OnResourceExecutionAsync(
                ResourceContext(http), NextThatThrows(new InvalidOperationException("model binding blew up"))));

        var (secondHttp, secondResponse) = HttpContextFor(cfg);
        var second = AuthorizationContext(secondHttp);
        attribute.OnAuthorization(second);

        Assert.Null(second.Result);
        Assert.True(secondHttp.Items.ContainsKey(RgbRestoreUploadConcurrencyGate.LeaseItemKey),
            "a restore upload that faulted mid-request (aborted upload, model-binding failure, or any other "
            + "exception past this filter) left its slot held, which is exactly the leaked-semaphore shape "
            + "that already turned a sibling gate into a fund-loss bug on this recovery path");

        await response.FireCompletionAsync();
        await secondResponse.FireCompletionAsync();
    }

    [Fact]
    public void TheRestoreFromBackupActionCarriesTheConcurrencyBoundAttribute()
    {
        var method = typeof(RGBController).GetMethod(nameof(RGBController.RestoreFromBackup));
        Assert.NotNull(method);
        Assert.True(method!.GetCustomAttribute<BoundRgbBackupUploadConcurrencyAttribute>() != null,
            "RGBController.RestoreFromBackup no longer carries [BoundRgbBackupUploadConcurrency], so nothing "
            + "bounds how many backup-restore uploads buffer to disk at once again");
    }

    [Fact]
    public void TheConfiguredDefaultIsWithinTheMinAndMaxBounds()
    {
        var cfg = new RGBConfiguration();
        var resolved = RgbRestoreUploadConcurrencyGate.ResolveMaxConcurrentUploads(cfg);
        Assert.True(resolved >= RGBConfiguration.RestoreUploadMaxConcurrentUploadsMin
            && resolved <= RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax);
    }

    [Fact]
    public void AConfiguredValueBelowTheFloorIsClampedNotHonoured()
    {
        var cfg = new RGBConfiguration { RestoreUploadMaxConcurrentUploads = 0 };
        Assert.Equal(RGBConfiguration.RestoreUploadMaxConcurrentUploadsMin,
            RgbRestoreUploadConcurrencyGate.ResolveMaxConcurrentUploads(cfg));
    }

    [Fact]
    public void AConfiguredValueAboveTheCeilingIsClampedNotHonoured()
    {
        var cfg = new RGBConfiguration { RestoreUploadMaxConcurrentUploads = int.MaxValue };
        Assert.Equal(RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax,
            RgbRestoreUploadConcurrencyGate.ResolveMaxConcurrentUploads(cfg));
    }

    [Fact]
    public void TheBoundIsReachableFromTheEnvironmentSoAFalseRejectIsRecoverable()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == RgbRestoreUploadConcurrencyGate.EnvironmentVariableName ? "9" : null);

        Assert.Equal(9, RgbRestoreUploadConcurrencyGate.ResolveMaxConcurrentUploads(cfg));
    }

    [Fact]
    public void AnEnvironmentBoundAboveTheCeilingIsClampedNotIgnored()
    {
        var high = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(high, name =>
            name == RgbRestoreUploadConcurrencyGate.EnvironmentVariableName ? "999999" : null);

        Assert.Equal(RGBConfiguration.RestoreUploadMaxConcurrentUploadsMax, high.RestoreUploadMaxConcurrentUploads);
    }

    [Fact]
    public void AZeroOrNegativeEnvironmentBoundIsIgnoredRatherThanDisablingTheGate()
    {
        var zero = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(zero, name =>
            name == RgbRestoreUploadConcurrencyGate.EnvironmentVariableName ? "0" : null);
        var negative = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(negative, name =>
            name == RgbRestoreUploadConcurrencyGate.EnvironmentVariableName ? "-3" : null);

        Assert.Equal(RGBConfiguration.RestoreUploadMaxConcurrentUploadsDefault, zero.RestoreUploadMaxConcurrentUploads);
        Assert.Equal(RGBConfiguration.RestoreUploadMaxConcurrentUploadsDefault, negative.RestoreUploadMaxConcurrentUploads);
    }
}
