using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreUploadBoundTests
{
    [Fact]
    public void TheShippedUploadBoundAcceptsEveryArchiveBackupValidationWouldAccept()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration());

        Assert.True(bound >= RgbBackupValidator.MaxTotalUncompressedBytes,
            $"the restore upload bound is {bound} bytes while backup validation admits "
            + $"{RgbBackupValidator.MaxTotalUncompressedBytes} bytes of content: an rgb-lib backup is a "
            + "ZIP of encrypted, incompressible blobs, so its compressed size tracks its uncompressed "
            + "size and this plugin would refuse to restore a backup it produced itself");
    }

    [Fact]
    public void TheShippedUploadBoundLeavesRoomForZipFramingOnTopOfTheValidatedContent()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration());

        Assert.True(bound > RgbBackupValidator.MaxTotalUncompressedBytes,
            "a bound exactly equal to the validated content budget refuses a full-size archive by the "
            + "width of its own local headers and central directory");
    }

    [Fact]
    public void TheUploadBoundStaysUnderTheMultipartFormBodyCeilingAtEveryReachableSetting()
    {
        Assert.True(
            RGBConfiguration.RestoreUploadBoundMaxBytes
                < RGBConfiguration.MultipartFormBodyLengthCeilingBytes,
            "raising the bound past FormOptions.MultipartBodyLengthLimit would swap this endpoint's "
            + "actionable refusal for an opaque form-parsing failure the operator cannot act on");
    }

    [Fact]
    public void AnUploadBoundFromTheConfigurationFileBelowTheValidatedContentBudgetIsFlooredNotHonoured()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration { RestoreUploadMaxBytes = 1 });

        Assert.Equal(RGBConfiguration.RestoreUploadBoundMinBytes, bound);
    }

    [Fact]
    public void AnUploadBoundFromTheConfigurationFileAboveTheCeilingIsCappedNotHonoured()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(
            new RGBConfiguration { RestoreUploadMaxBytes = long.MaxValue });

        Assert.Equal(RGBConfiguration.RestoreUploadBoundMaxBytes, bound);
    }

    [Fact]
    public void TheUploadBoundIsReachableFromTheEnvironmentSoAFalseRejectIsRecoverable()
    {
        var cfg = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(cfg, name =>
            name == RgbRestoreUploadBound.EnvironmentVariableName ? "83886080" : null);

        Assert.Equal(83_886_080, RgbRestoreUploadBound.ResolveBytes(cfg));
    }

    [Fact]
    public void AnEnvironmentUploadBoundOutsideTheRangeIsClampedNotIgnored()
    {
        var low = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(low, name =>
            name == RgbRestoreUploadBound.EnvironmentVariableName ? "1024" : null);
        var high = new RGBConfiguration();
        RGBPlugin.ApplyEnvironmentOverrides(high, name =>
            name == RgbRestoreUploadBound.EnvironmentVariableName ? "999999999999" : null);

        Assert.Equal(RGBConfiguration.RestoreUploadBoundMinBytes, low.RestoreUploadMaxBytes);
        Assert.Equal(RGBConfiguration.RestoreUploadBoundMaxBytes, high.RestoreUploadMaxBytes);
    }

    [Fact]
    public void AnUploadAtTheBoundIsAcceptedAndOneByteOverIsRefused()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration());

        Assert.False(RgbRestoreUploadBound.IsOverBound(bound, bound));
        Assert.True(RgbRestoreUploadBound.IsOverBound(bound + 1, bound));
    }

    [Fact]
    public void AnUnknownUploadLengthIsNotTreatedAsOversized()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration());

        Assert.False(RgbRestoreUploadBound.IsOverBound(null, bound),
            "a chunked upload has no Content-Length and must reach the request-body size feature "
            + "rather than being refused on a guess");
    }

    [Fact]
    public void TheUploadBoundJsonKeyReachesTheKnobSoRgbJsonIsNotSilentlyIgnored()
    {
        var cfg = System.Text.Json.JsonSerializer.Deserialize<RGBConfiguration>(
            """{ "restore_upload_max_bytes": 62914560 }""");

        Assert.NotNull(cfg);
        Assert.Equal(62_914_560, RgbRestoreUploadBound.ResolveBytes(cfg!));
    }

    [Fact]
    public void TheRefusalMessageNamesTheLimitAndHowAnOperatorRaisesIt()
    {
        var bound = RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration());
        var message = RgbRestoreUploadBound.RefusalMessage(bound);

        Assert.Contains(RgbRestoreUploadBound.EnvironmentVariableName, message);
        Assert.Contains($"{bound / 1024 / 1024} MB", message);
        Assert.Contains($"{RGBConfiguration.RestoreUploadBoundMaxBytes / 1024 / 1024} MB", message);
    }
}
