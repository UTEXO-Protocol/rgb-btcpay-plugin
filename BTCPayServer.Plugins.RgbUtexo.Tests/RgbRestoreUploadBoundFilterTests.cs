using BTCPayServer.Plugins.RgbUtexo.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreUploadBoundFilterTests
{
    sealed class RequestBodySizeFeatureThatRefusesWritesWhenReadOnly : IHttpMaxRequestBodySizeFeature
    {
        long? _maxRequestBodySize;

        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize
        {
            get => _maxRequestBodySize;
            set
            {
                if (IsReadOnly)
                    throw new InvalidOperationException(
                        "MaxRequestBodySize cannot be modified after the request body has started being read");
                _maxRequestBodySize = value;
            }
        }
    }

    static RGBConfiguration ConfigurationWithDefaultUploadBound() =>
        new(Path.Combine(Path.GetTempPath(), "rgb-restore-upload-bound-filter-tests"));

    static AuthorizationFilterContext FilterContext(
        RGBConfiguration cfg, long? contentLength, IHttpMaxRequestBodySizeFeature? bodySizeFeature)
    {
        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Request.ContentLength = contentLength;
        if (bodySizeFeature != null)
            httpContext.Features.Set(bodySizeFeature);
        return new AuthorizationFilterContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());
    }

    [Fact]
    public void TheFilterLowersTheHostRequestBodyLimitToTheConfiguredBound()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var feature = new RequestBodySizeFeatureThatRefusesWritesWhenReadOnly { MaxRequestBodySize = long.MaxValue };
        var context = FilterContext(cfg, contentLength: null, feature);

        new BoundRgbBackupUploadToConfiguredLimitAttribute().OnAuthorization(context);

        Assert.True(feature.MaxRequestBodySize == RgbRestoreUploadBound.ResolveBytes(cfg),
            $"the filter left the host body limit at {feature.MaxRequestBodySize} instead of the configured "
            + $"{RgbRestoreUploadBound.ResolveBytes(cfg)}-byte bound, so a chunked upload with no Content-Length "
            + "streams unbounded into the BTCPay process, which is the only bound that can fire on one");
        Assert.Null(context.Result);
    }

    [Fact]
    public void AReadOnlyHostRequestBodyLimitIsLeftAloneRatherThanThrownAgainst()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var feature = new RequestBodySizeFeatureThatRefusesWritesWhenReadOnly { IsReadOnly = true };
        var context = FilterContext(cfg, contentLength: null, feature);

        var thrown = Record.Exception(
            () => new BoundRgbBackupUploadToConfiguredLimitAttribute().OnAuthorization(context));

        Assert.True(thrown == null,
            "the filter wrote to a read-only IHttpMaxRequestBodySizeFeature and the host threw "
            + $"[{thrown?.Message}], which turns every restore on such a host into a permanent failure the "
            + "operator cannot clear without shell access");
        Assert.Null(context.Result);
    }

    [Fact]
    public void AnUploadDeclaringMoreThanTheBoundIsRefusedBeforeTheBodyIsReadAndIsToldHowToRaiseTheLimit()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var bound = RgbRestoreUploadBound.ResolveBytes(cfg);
        var context = FilterContext(cfg, contentLength: bound + 1, bodySizeFeature: null);

        new BoundRgbBackupUploadToConfiguredLimitAttribute().OnAuthorization(context);

        var refusal = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, refusal.StatusCode);
        Assert.Equal(RgbRestoreUploadBound.RefusalMessage(bound), refusal.Content);
    }

    [Fact]
    public void AnUploadWithNoDeclaredLengthIsNotRefusedOnAGuess()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var context = FilterContext(cfg, contentLength: null, bodySizeFeature: null);

        new BoundRgbBackupUploadToConfiguredLimitAttribute().OnAuthorization(context);

        Assert.True(context.Result == null,
            "a chunked restore upload declares no Content-Length, and refusing it here would make the recovery "
            + "path permanently unusable behind any proxy that re-chunks the body");
    }

    [Fact]
    public void TheRefusalMessageDescribesTheQuantityTheFilterActuallyMeasures()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(ConfigurationWithDefaultUploadBound());
        var message = RgbRestoreUploadBound.RefusalMessage(bound);

        Assert.True(message.Contains("whole upload", StringComparison.Ordinal),
            "the refusal message names a limit on the backup file while the filter compares the multipart "
            + $"request Content-Length, which also carries the form framing and the other fields; message was "
            + $"[{message}]. An operator told the limit applies to the file alone will size a backup right up "
            + "to it and be refused again with the same message, and will read that as the limit being ignored");
    }

    [Fact]
    public void AnUploadExactlyAtTheBoundIsNotRefused()
    {
        var cfg = ConfigurationWithDefaultUploadBound();
        var bound = RgbRestoreUploadBound.ResolveBytes(cfg);
        var context = FilterContext(cfg, contentLength: bound, bodySizeFeature: null);

        new BoundRgbBackupUploadToConfiguredLimitAttribute().OnAuthorization(context);

        Assert.True(context.Result == null,
            $"an upload of exactly {bound} bytes was refused although the refusal message tells operators that "
            + "much is allowed, so raising the limit would not help them");
    }
}
