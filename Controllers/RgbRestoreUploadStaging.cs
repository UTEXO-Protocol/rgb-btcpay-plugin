using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

public static class RgbRestoreUploadStaging
{
    public const string UploadedBackupFileName = "backup.rgb";

    public static string DirectoryNameForAttempt(Guid attempt) =>
        $"{RGBWalletService.RestoreStagingPrefix}upload-{attempt:N}";

    public static string ResolveDirectoryForAttempt(RGBConfiguration cfg, string selectedNetwork, Guid attempt) =>
        Path.Combine(
            cfg.RgbBaseDir,
            RGBConfiguration.MapNetworkFolder(selectedNetwork),
            "rgb-wallets",
            DirectoryNameForAttempt(attempt));

    public static string CreateDirectoryForAttempt(RGBConfiguration cfg, string selectedNetwork)
    {
        var directory = ResolveDirectoryForAttempt(cfg, selectedNetwork, Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void DeleteDirectoryForAttemptWithEverythingRgbLibLeftInside(string? directory, ILogger log)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Failed to clean up restore upload dir {Dir}; the startup sweep will retry it", directory);
        }
    }
}
