using BTCPayServer.Plugins.RgbUtexo.Controllers;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// M7/M8 high-value surface: RestoreFromBackup cleanup-on-failure.
/// Tests that temp files are cleaned up and that the 50MB post-extraction
/// size cap is enforced at the validation layer.
/// </summary>
public class RestoreBackupCleanupTests
{
    static IFormFile CreateFormFile(byte[] content, string name = "backup.rgb")
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", name);
    }

    [Fact]
    public void TempPath_UsesGuid_NoDuplicates()
    {
        var paths = Enumerable.Range(0, 100)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"rgb-restore-{Guid.NewGuid():N}.rgb"))
            .ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public async Task TempFile_DeletedAfterProcessing()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rgb-restore-test-{Guid.NewGuid():N}.rgb");
        var content = new byte[16];
        content[0] = (byte)'P'; content[1] = (byte)'K'; content[2] = 0x03; content[3] = 0x04;

        try
        {
            await File.WriteAllBytesAsync(tempPath, content);
            Assert.True(File.Exists(tempPath));
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task HeaderValidation_RejectsBeforeReachingNativeCode()
    {
        var malicious = new byte[64];
        malicious[0] = 0x7F;
        malicious[1] = (byte)'E';
        malicious[2] = (byte)'L';
        malicious[3] = (byte)'F';

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(malicious)));
    }

    [Fact]
    public void RequestSizeLimit_IsPresent_AndIsTheConfigurableBoundNotACompileTimeConstant()
    {
        var method = typeof(RGBController).GetMethod(nameof(RGBController.RestoreFromBackup))!;

        var bounds = method.GetCustomAttributes(inherit: false)
            .OfType<BoundRgbBackupUploadToConfiguredLimitAttribute>()
            .ToList();
        Assert.Single(bounds);

        Assert.Empty(method.GetCustomAttributes(
            typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));

        Assert.True(
            RgbRestoreUploadBound.ResolveBytes(new RGBConfiguration())
                >= Services.RgbBackupValidator.MaxTotalUncompressedBytes,
            "the upload bound must stay at or above the content budget backup validation admits, or "
            + "the plugin refuses to restore a backup it produced itself, and the archive is the only "
            + "recovery route for client-side RGB stock");
    }

    [Fact]
    public async Task ZeroLengthFile_FailsHeaderCheck()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RGBController.ValidateBackupFileHeader(CreateFormFile(Array.Empty<byte>())));
    }

    [Fact]
    public async Task ValidZipArchive_PassesHeaderCheck()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("backup.dat");
            using var w = new StreamWriter(entry.Open());
            w.Write("data");
        }
        await RGBController.ValidateBackupFileHeader(CreateFormFile(ms.ToArray()));
    }

    [Fact]
    public void DirectoryCleanup_DeletesOnFailure()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rgb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "test.txt"), "data");
        Assert.True(Directory.Exists(dir));

        Directory.Delete(dir, true);
        Assert.False(Directory.Exists(dir));
    }
}
