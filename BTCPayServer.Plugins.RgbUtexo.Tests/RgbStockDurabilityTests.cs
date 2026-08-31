using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbStockDurabilityTests
{
    static string MakeStock(out byte[] index, out byte[] stash, out byte[] state)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rgb-stock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        index = RandomNumberGenerator.GetBytes(256);
        stash = RandomNumberGenerator.GetBytes(512);
        state = RandomNumberGenerator.GetBytes(128);
        File.WriteAllBytes(Path.Combine(dir, "index.dat"), index);
        File.WriteAllBytes(Path.Combine(dir, "stash.dat"), stash);
        File.WriteAllBytes(Path.Combine(dir, "state.dat"), state);
        return dir;
    }

    [Fact]
    public void Snapshot_CopiesThreeDatsByteForByte()
    {
        var stock = MakeStock(out var index, out var stash, out var state);
        var snap = RgbStockDurability.SnapshotStock(stock);
        try
        {
            Assert.NotEqual(Path.GetFullPath(stock), Path.GetFullPath(snap));
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(snap, "index.dat")));
            Assert.Equal(stash, File.ReadAllBytes(Path.Combine(snap, "stash.dat")));
            Assert.Equal(state, File.ReadAllBytes(Path.Combine(snap, "state.dat")));
        }
        finally
        {
            RgbStockDurability.DeleteSnapshot(snap);
            Directory.Delete(stock, true);
        }
    }

    [Fact]
    public void Snapshot_DoesNotMutateSource()
    {
        var stock = MakeStock(out var index, out _, out _);
        var snap = RgbStockDurability.SnapshotStock(stock);
        try
        {
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(stock, "index.dat")));
        }
        finally
        {
            RgbStockDurability.DeleteSnapshot(snap);
            Directory.Delete(stock, true);
        }
    }

    [Fact]
    public void VerificationSnapshot_CopiesStockAndBdkByteForByte()
    {
        var stock = MakeStock(out var index, out var stash, out var state);
        var wallet = Path.Combine(Path.GetTempPath(), $"rgb-wallet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(wallet);
        var bdk = RandomNumberGenerator.GetBytes(1024);
        File.WriteAllBytes(Path.Combine(wallet, RgbStockDurability.WatchOnlyBdkStoreFileName), bdk);
        RgbVerificationSnapshot? snapshot = null;
        try
        {
            snapshot = RgbStockDurability.SnapshotVerificationState(stock, wallet);
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(snapshot.StockDir, "index.dat")));
            Assert.Equal(stash, File.ReadAllBytes(Path.Combine(snapshot.StockDir, "stash.dat")));
            Assert.Equal(state, File.ReadAllBytes(Path.Combine(snapshot.StockDir, "state.dat")));
            Assert.Equal(bdk, File.ReadAllBytes(snapshot.BdkStorePath));
        }
        finally
        {
            if (snapshot != null) RgbStockDurability.DeleteSnapshot(snapshot.RootDir);
            Directory.Delete(stock, true);
            Directory.Delete(wallet, true);
        }
    }

    [Fact]
    public void PinnedRgbLibBeta30_WatchOnlyWalletCreatesProductionBdkLayout()
    {
        // Artifact-compatibility test using the actual pinned rgb-lib binding. This is intentionally
        // offline and is not a send E2E test: it proves the filename and serialized descriptor
        // branches that SnapshotVerificationState and the native verifier must authenticate.
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        var restoredJson = RgbLibWallet.RestoreKeys("Regtest", mnemonic);
        using var restored = JsonDocument.Parse(restoredJson);
        var restoredRoot = restored.RootElement;
        var fingerprint = restoredRoot.GetProperty("master_fingerprint").GetString()!;
        var vanillaAccountXpub = restoredRoot.GetProperty("account_xpub_vanilla").GetString()!;
        var coloredAccountXpub = restoredRoot.GetProperty("account_xpub_colored").GetString()!;
        var dataDir = Path.Combine(Path.GetTempPath(), $"rgb-real-layout-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);

        var configJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data_dir"] = dataDir,
            ["bitcoin_network"] = "Regtest",
            ["database_type"] = "Sqlite",
            ["max_allocations_per_utxo"] = 5,
            ["supported_schemas"] = new[] { "Nia", "Cfa" }
        });
        var keysJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["account_xpub_vanilla"] = vanillaAccountXpub,
            ["account_xpub_colored"] = coloredAccountXpub,
            ["master_fingerprint"] = fingerprint,
            ["vanilla_keychain"] = (int?)null,
            ["mnemonic"] = (string?)null
        });

        try
        {
            using (var wallet = new RgbLibWallet(configJson, keysJson)) { }

            var walletDir = Path.Combine(dataDir, fingerprint);
            var watchOnlyStore = Path.Combine(walletDir, RgbStockDurability.WatchOnlyBdkStoreFileName);
            Assert.True(File.Exists(watchOnlyStore));
            Assert.False(File.Exists(Path.Combine(walletDir, "bdk_db")));

            // BDK's append log contains its public descriptors as UTF-8. Match only the derivation
            // origins, never xpubs or fingerprints, so no host/user wallet material enters fixtures.
            var persisted = Encoding.UTF8.GetString(File.ReadAllBytes(watchOnlyStore));
            Assert.Contains("86'/827167'/0'/0", persisted);
            Assert.Contains("86'/1'/0'/0", persisted);
            Assert.DoesNotContain("86'/1'/0'/1", persisted);
            Assert.Contains(ExtPubKey.Parse(coloredAccountXpub, Network.RegTest).Derive(0).ToString(Network.RegTest), persisted);
            Assert.Contains(ExtPubKey.Parse(vanillaAccountXpub, Network.RegTest).Derive(0).ToString(Network.RegTest), persisted);
            Assert.DoesNotContain(ExtPubKey.Parse(vanillaAccountXpub, Network.RegTest).Derive(1).ToString(Network.RegTest), persisted);
        }
        finally
        {
            Directory.Delete(dataDir, true);
        }
    }

    [Fact]
    public void VerificationSnapshot_MissingRequiredFileFailsClosedAndCleansUp()
    {
        var stock = MakeStock(out _, out _, out _);
        var wallet = Path.Combine(Path.GetTempPath(), $"rgb-wallet-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(wallet);
        File.Delete(Path.Combine(stock, "state.dat"));
        try
        {
            Assert.Throws<FileNotFoundException>(() =>
                RgbStockDurability.SnapshotVerificationState(stock, wallet));
        }
        finally
        {
            Directory.Delete(stock, true);
            Directory.Delete(wallet, true);
        }
    }

    [Fact]
    public void FsyncStockDats_OnExistingFiles_DoesNotThrowOrCorrupt()
    {
        var stock = MakeStock(out var index, out var stash, out var state);
        try
        {
            RgbStockDurability.FsyncStockDats(stock);
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(stock, "index.dat")));
            Assert.Equal(stash, File.ReadAllBytes(Path.Combine(stock, "stash.dat")));
            Assert.Equal(state, File.ReadAllBytes(Path.Combine(stock, "state.dat")));
        }
        finally { Directory.Delete(stock, true); }
    }

    [Fact]
    public void FsyncStockDats_MissingDir_Throws()
    {
        // Fail-closed: ClearNeedsRecovery must not clear the quarantine marker without a real
        // fsync of the real Stock files. A missing stock dir must throw, never silently no-op.
        var dir = Path.Combine(Path.GetTempPath(), $"rgb-absent-{Guid.NewGuid():N}");
        Assert.Throws<DirectoryNotFoundException>(() => RgbStockDurability.FsyncStockDats(dir));
    }

    [Fact]
    public void FsyncStockDats_MissingDatFile_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rgb-partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "index.dat"), RandomNumberGenerator.GetBytes(16));
        File.WriteAllBytes(Path.Combine(dir, "stash.dat"), RandomNumberGenerator.GetBytes(16));
        // state.dat intentionally absent -> must fail closed rather than partially fsync + clear.
        try { Assert.Throws<FileNotFoundException>(() => RgbStockDurability.FsyncStockDats(dir)); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveStockDir_FallsBackToLowercaseWhenExactCaseAbsent()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rgb-resolve-{Guid.NewGuid():N}");
        var lower = Path.Combine(baseDir, "abcd1234", "rgb");
        Directory.CreateDirectory(lower);
        try
        {
            var resolved = RgbStockDurability.ResolveStockDir(baseDir, "ABCD1234");
            // Filesystem may be case-insensitive (macOS): what matters is the resolved dir exists
            // and points at the real stock dir regardless of the fingerprint casing on disk.
            Assert.True(Directory.Exists(resolved));
            Assert.Equal(Path.GetFullPath(lower), Path.GetFullPath(resolved),
                ignoreCase: true, ignoreLineEndingDifferences: false, ignoreWhiteSpaceDifferences: false);
        }
        finally { Directory.Delete(baseDir, true); }
    }

    [Fact]
    public void ResolveStockDir_MissingEntirely_ReturnsDirectCandidate()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rgb-resolve-{Guid.NewGuid():N}");
        var resolved = RgbStockDurability.ResolveStockDir(baseDir, "ff00ff00");
        Assert.Equal(Path.Combine(baseDir, "ff00ff00", "rgb"), resolved);
    }
}
