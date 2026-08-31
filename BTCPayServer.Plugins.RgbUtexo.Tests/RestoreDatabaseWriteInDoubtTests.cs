using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RestoreDatabaseWriteInDoubtTests
{
    public RestoreDatabaseWriteInDoubtTests() => ResetRestoreCooldown();

    const string SyntheticMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string FakeLibFingerprint = "00000000";

    static void ResetRestoreCooldown() =>
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);

    [Fact]
    public async Task AnInsertWhoseOutcomeCannotBeReadBackKeepsTheRestoredWalletData()
    {
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-indoubt-{Guid.NewGuid():N}"));
        try
        {
            var svc = BuildServiceWhoseDatabaseCannotBeReached(cfg);
            using var backup = new TempBackup();

            await Assert.ThrowsAnyAsync<Exception>(
                () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));

            var walletsParent = Path.GetDirectoryName(cfg.GetWalletDataDir("probe", "signet"))!;
            var survivors = Directory.Exists(walletsParent)
                ? Directory.GetDirectories(walletsParent)
                : [];

            Assert.True(survivors.Length == 1,
                "the insert of the restored wallet row failed against an unreachable database, and the "
                + "read-back that would say whether it nevertheless committed failed for the same "
                + $"reason, yet {survivors.Length} wallet data directory/directories survive. An "
                + "in-doubt commit — Postgres applies the INSERT and the connection drops before the "
                + "acknowledgement reaches Npgsql — throws with the row already written, and that row "
                + "carries IsActive and NeedsRecovery. Deleting the wallet data under it hands rgb-lib "
                + "a data dir with nothing at the fingerprint it joins, which setup_new_wallet answers "
                + "by creating a fresh empty wallet and reporting success. The store then presents that "
                + "empty wallet as the restored one, the operator cannot retry (the controller "
                + "redirects while any row exists for the store) and cannot delete it (DeleteWalletAsync "
                + "refuses a NeedsRecovery row), so the assets stay only in the backup");

            Assert.True(
                File.Exists(Path.Combine(survivors[0], FakeLibFingerprint, "rgb_lib_db")),
                "the surviving directory must still hold the unpacked wallet database at the "
                + "fingerprint rgb-lib joins; an empty shell there is the same silent empty wallet");
        }
        finally { try { Directory.Delete(cfg.RgbBaseDir, true); } catch { } }
    }

    static RGBWalletService BuildServiceWhoseDatabaseCannotBeReached(RGBConfiguration cfg)
    {
        var rgbLib = new FakeRgbLib(cfg, FakeLibFingerprint);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=99999;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(
            new StagingShapedLikeAGenuineBackupRunner(), cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    sealed class StagingShapedLikeAGenuineBackupRunner : IRestoreProcessRunner
    {
        public Task<RestoreRunResult> RunAsync(
            string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)
        {
            var walletDir = Path.Combine(stagingDir, FakeLibFingerprint);
            Directory.CreateDirectory(Path.Combine(walletDir, "assets"));
            Directory.CreateDirectory(Path.Combine(walletDir, "media_files"));
            File.WriteAllBytes(Path.Combine(walletDir, "rgb_lib_db"), new byte[256]);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"rgb-indoubt-backup-{Guid.NewGuid():N}.rgb");
            using var fs = File.Create(Path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            using (var enc = zip.CreateEntry("backup.enc").Open())
                enc.Write(new byte[16]);
            using var pub = new StreamWriter(zip.CreateEntry("backup.pub_data").Open());
            pub.Write("""{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""");
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }
}
