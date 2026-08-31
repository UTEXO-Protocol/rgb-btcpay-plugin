using System.Runtime.InteropServices;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbStockDurability
{
    internal static readonly IReadOnlyList<string> StockFiles = ["index.dat", "stash.dat", "state.dat"];
    // rgb-lib beta.30 selects this BDK append-log filename when the wallet is
    // watch-only (mnemonic == null), which is how the plugin always opens wallets.
    // This filename is independent from the append log's b"bdk_db" file magic.
    internal const string WatchOnlyBdkStoreFileName = "bdk_db_watch_only";

    public static string ResolveStockDir(string walletDataDir, string fingerprint)
    {
        var direct = Path.Combine(walletDataDir, fingerprint, "rgb");
        if (Directory.Exists(direct)) return direct;
        var lower = Path.Combine(walletDataDir, fingerprint.ToLowerInvariant(), "rgb");
        if (Directory.Exists(lower)) return lower;
        return direct;
    }

    public static void FsyncStockDats(string stockDir)
    {
        // WHY: fail-closed durability barrier. If the Stock dir or any .dat is absent the
        // caller must NOT clear the quarantine marker without a real fsync of the real
        // Stock files, so a missing dir/file throws rather than silently no-op'ing.
        if (!Directory.Exists(stockDir))
            throw new DirectoryNotFoundException($"RGB stock dir not found, cannot fsync: {stockDir}");
        foreach (var name in StockFiles)
        {
            var path = Path.Combine(stockDir, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"RGB stock file not found, cannot fsync: {path}");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            fs.Flush(true);
        }
    }

    public static string SnapshotStock(string stockDir)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"rgb-stock-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Hardened(tempDir);
        foreach (var name in StockFiles)
        {
            var src = Path.Combine(stockDir, name);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(tempDir, name));
        }
        return tempDir;
    }

    public static RgbVerificationSnapshot SnapshotVerificationState(string stockDir, string walletDir)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rgb-verify-snap-{Guid.NewGuid():N}");
        var stockSnapshot = Path.Combine(root, "rgb");
        Directory.CreateDirectory(stockSnapshot);
        Hardened(root);
        Hardened(stockSnapshot);
        try
        {
            foreach (var name in StockFiles)
            {
                var source = Path.Combine(stockDir, name);
                if (!File.Exists(source))
                    throw new FileNotFoundException(
                        $"RGB verification snapshot source is missing: {source}", source);
                File.Copy(source, Path.Combine(stockSnapshot, name));
            }

            var bdkSource = Path.Combine(walletDir, WatchOnlyBdkStoreFileName);
            if (!File.Exists(bdkSource))
                throw new FileNotFoundException(
                    $"RGB verification BDK snapshot source is missing: {bdkSource}", bdkSource);
            var bdkSnapshot = Path.Combine(root, WatchOnlyBdkStoreFileName);
            File.Copy(bdkSource, bdkSnapshot);
            return new RgbVerificationSnapshot(root, stockSnapshot, bdkSnapshot);
        }
        catch
        {
            DeleteSnapshot(root);
            throw;
        }
    }

    public static void DeleteSnapshot(string? tempDir)
    {
        if (string.IsNullOrEmpty(tempDir)) return;
        try { Directory.Delete(tempDir, true); } catch { }
    }

    static void Hardened(string dir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch { }
    }
}

public sealed record RgbVerificationSnapshot(string RootDir, string StockDir, string BdkStorePath);
