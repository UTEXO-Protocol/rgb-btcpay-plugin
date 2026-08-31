using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RestoreWalletDirectoryRootTests
{
    public RestoreWalletDirectoryRootTests() => ResetRestoreCooldown();

    const string SyntheticMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string FakeLibFingerprint = "00000000";

    const string NoWalletDirectoryMarker = "holds no wallet directory for the recovery phrase";
    const string UnusableFingerprintMarker = "did not yield a usable master fingerprint";
    const string LetterCaseMarker = "in different letter case";
    const string MnemonicMismatchRefusal =
        "Backup could not be loaded with the supplied mnemonic. The mnemonic does not match the keys in this backup.";

    static void ResetRestoreCooldown() =>
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);

    [Fact]
    public async Task ABackupWhoseOnlyTopLevelDirectoryIsNotTheDerivedFingerprint_IsRefused()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
        {
            Directory.CreateDirectory(Path.Combine(staging, "wallet-data"));
            File.WriteAllBytes(Path.Combine(staging, "wallet-data", "rgb_lib_db"), new byte[128]);
        });

        Assert.True(outcome.RefusedWith(NoWalletDirectoryMarker),
            $"a backup rooted at \"wallet-data\" instead of the derived master fingerprint was not refused; the "
            + $"restore ended as {outcome.Describe()}. rgb-lib's setup_new_wallet joins the stored master "
            + "fingerprint onto the wallet data dir and, finding no directory there, silently creates a fresh "
            + "empty wallet with no error, so accepting this tree reports a successful restore while the "
            + "backed-up RGB stock stays unreachable under the other root name");
        outcome.AssertNothingWasFinalized();
    }

    [Fact]
    public async Task ABackupWithNoTopLevelDirectoryAtAll_IsRefused()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
            File.WriteAllBytes(Path.Combine(staging, "rgb_lib_db"), new byte[128]));

        Assert.True(outcome.RefusedWith(NoWalletDirectoryMarker),
            $"a backup that unpacked to loose files with no wallet directory at all was not refused; the restore "
            + $"ended as {outcome.Describe()}. This is the shape the old fingerprint-directory count guard "
            + "skipped outright, and publishing it hands rgb-lib a data dir with nothing at the fingerprint it "
            + "expects, which it answers by creating an empty wallet and reporting success");
        outcome.AssertNothingWasFinalized();
    }

    [Fact]
    public async Task ABackupCarryingTheDerivedFingerprintAsARegularFileRatherThanADirectory_IsRefused()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
            File.WriteAllBytes(Path.Combine(staging, FakeLibFingerprint), new byte[128]));

        Assert.True(outcome.RefusedWith(NoWalletDirectoryMarker),
            $"a backup carrying a regular file where the wallet directory belongs was not refused; the restore "
            + $"ended as {outcome.Describe()}. Directory.GetDirectories never sees a regular file, so the old "
            + "guard counted zero fingerprint directories and skipped itself, and the path rgb-lib joins is then "
            + "occupied by something it can neither open nor replace");
        outcome.AssertNothingWasFinalized();
    }

    [Fact]
    public async Task AKeyDerivationThatYieldsNoUsableFingerprint_IsRefusedRatherThanTreatedAsAMatch()
    {
        var outcome = await RestoreWithStagingShapedAs(
            staging => Directory.CreateDirectory(Path.Combine(staging, FakeLibFingerprint)),
            masterFingerprintKeyDerivationYields: "");

        Assert.True(outcome.RefusedWith(UnusableFingerprintMarker),
            $"a restore whose key derivation produced an empty master fingerprint was not refused; the restore "
            + $"ended as {outcome.Describe()}. An empty fingerprint makes Path.Combine collapse onto the wallet "
            + "data dir itself, so an existence check on the joined path is trivially satisfied and every backup "
            + "would pass a guard that can no longer locate anything");
        outcome.AssertNothingWasFinalized();
    }

    [Fact]
    public async Task ABackupWhoseFingerprintDirectoryIsADifferentFingerprint_KeepsTheMnemonicMismatchRefusal()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
            Directory.CreateDirectory(Path.Combine(staging, "11111111")));

        Assert.True(outcome.Thrown is InvalidOperationException
                    && outcome.Thrown.Message == MnemonicMismatchRefusal,
            $"a backup whose wallet directory is a different master fingerprint ended as {outcome.Describe()}; "
            + "that shape has one true cause and the operator must keep being told it — the recovery phrase "
            + "belongs to a different wallet than this backup. Replacing it with the generic missing-directory "
            + "wording would send the operator hunting for a corrupted archive instead of the right phrase");
        outcome.AssertNothingWasFinalized();
    }

    [Fact]
    public async Task ABackupRootedAtExactlyTheDerivedFingerprint_IsNotRefusedByThisGuard()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
        {
            var walletDir = Path.Combine(staging, FakeLibFingerprint);
            Directory.CreateDirectory(Path.Combine(walletDir, "assets"));
            Directory.CreateDirectory(Path.Combine(walletDir, "media_files"));
            File.WriteAllBytes(Path.Combine(walletDir, "rgb_lib_db"), new byte[256]);
        });

        var message = outcome.Thrown?.Message ?? "";
        Assert.True(!message.Contains(NoWalletDirectoryMarker, StringComparison.Ordinal)
                    && !message.Contains(UnusableFingerprintMarker, StringComparison.Ordinal)
                    && !message.Contains(LetterCaseMarker, StringComparison.Ordinal)
                    && message != MnemonicMismatchRefusal,
            $"the exact shape rgb-lib's backup() produces — one top-level directory named for the master "
            + $"fingerprint, holding the wallet — was refused with \"{message}\". rgb-lib zips wallet_dir with "
            + "keep_last_path_component set, so every entry in a genuine backup is prefixed with that one "
            + "directory name. Refusing it would make a funded wallet unrestorable, which is a worse failure "
            + "than the silent empty wallet this guard exists to stop");
    }

    [Fact]
    public async Task ABackupWhoseFingerprintDirectoryDiffersOnlyInLetterCase_IsHandledTheWayThisFilesystemWill()
    {
        const string derived = "aabbccdd";
        const string repacked = "AABBCCDD";

        var probeDir = Path.Combine(Path.GetTempPath(), $"rgb-case-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(probeDir, repacked));
        bool thisFilesystemResolvesACaseVariantToTheSameDirectory;
        try
        {
            thisFilesystemResolvesACaseVariantToTheSameDirectory =
                Directory.Exists(Path.Combine(probeDir, derived));
        }
        finally { try { Directory.Delete(probeDir, true); } catch { } }

        var outcome = await RestoreWithStagingShapedAs(
            staging => Directory.CreateDirectory(Path.Combine(staging, repacked)),
            masterFingerprintKeyDerivationYields: derived);

        if (thisFilesystemResolvesACaseVariantToTheSameDirectory)
        {
            Assert.False(outcome.RefusedWith(LetterCaseMarker),
                "this filesystem resolves a case variant to the same directory, so rgb-lib's own wallet_dir "
                + "existence check will open the restored data under the repacked name and the wallet works. "
                + $"Refusing it here anyway ({outcome.Describe()}) would strand a funded wallet on exactly the "
                + "filesystems where nothing is wrong. Note that the case divergence this guard closes cannot be "
                + "exhibited on this machine at all; BTCPay production is linux-x64 on a case-sensitive "
                + "filesystem, which takes the other branch");
        }
        else
        {
            Assert.True(outcome.RefusedWith(LetterCaseMarker),
                $"this filesystem treats the two letter cases as different directories, yet the restore ended as "
                + $"{outcome.Describe()}. rgb-lib joins the stored master fingerprint verbatim, so it would "
                + "create a fresh empty wallet at the derived name while the backed-up data sat beside it under "
                + "the repacked name — and a guard that compares the two names case-insensitively cannot see "
                + "that at all");
            outcome.AssertNothingWasFinalized();
        }
    }

    [Fact]
    public async Task TheMissingWalletDirectoryRefusalIsIdenticalForTwoDifferentFingerprints_SoItLeaksNeitherOfThem()
    {
        var first = await RestoreWithStagingShapedAs(
            staging => Directory.CreateDirectory(Path.Combine(staging, "wallet-data")),
            masterFingerprintKeyDerivationYields: "0123abcd");
        var second = await RestoreWithStagingShapedAs(
            staging => Directory.CreateDirectory(Path.Combine(staging, "wallet-data")),
            masterFingerprintKeyDerivationYields: "fedc9876");

        Assert.Equal(first.Thrown?.Message, second.Thrown?.Message);
        var message = first.Thrown?.Message ?? "";
        Assert.True(message.Contains(NoWalletDirectoryMarker, StringComparison.Ordinal),
            $"the refusal read \"{message}\"; the two runs must both reach the missing-directory refusal for the "
            + "equality above to prove anything about what it discloses");
        Assert.DoesNotContain("0123abcd", message);
        Assert.DoesNotContain("fedc9876", message);
    }

    [Fact]
    public async Task TheMissingWalletDirectoryRefusalNamesNoHostPathAndTellsTheOperatorWhatToDoWithoutShellAccess()
    {
        var outcome = await RestoreWithStagingShapedAs(staging =>
            Directory.CreateDirectory(Path.Combine(staging, "wallet-data")));
        var message = outcome.Thrown?.Message ?? "";

        Assert.DoesNotContain("/Users/", message);
        Assert.DoesNotContain("/var/", message);
        Assert.DoesNotContain(".btcpayserver", message);
        Assert.DoesNotContain("rgb-wallets", message);
        Assert.DoesNotContain(":\\", message);
        Assert.Contains("No wallet was created on the server", message);
        Assert.Contains("your backup file is unchanged", message);
        Assert.Contains("Restore an unmodified backup taken by this plugin", message);
    }

    [Fact]
    public void AForeignRootedTreePublishedAsIsLeavesTheFingerprintPathEmpty_WhichIsWhyTheRefusalMustPrecedeTheMove()
    {
        var walletDataDir = Path.Combine(Path.GetTempPath(), $"rgb-strand-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(walletDataDir, "wallet-data"));
            File.WriteAllBytes(Path.Combine(walletDataDir, "wallet-data", "rgb_lib_db"), new byte[128]);

            var pathRgbLibJoins = Path.Combine(walletDataDir, FakeLibFingerprint);
            Assert.False(Directory.Exists(pathRgbLibJoins),
                "the published tree must not carry a directory at the joined fingerprint path; that absence is "
                + "exactly what rgb-lib answers by creating a new empty wallet");

            Directory.CreateDirectory(pathRgbLibJoins);
            Directory.CreateDirectory(Path.Combine(pathRgbLibJoins, "assets"));
            Directory.CreateDirectory(Path.Combine(pathRgbLibJoins, "media_files"));

            Assert.Empty(Directory.GetFiles(pathRgbLibJoins));
            Assert.True(File.Exists(Path.Combine(walletDataDir, "wallet-data", "rgb_lib_db")),
                "the backed-up database is still on disk, untouched and unreachable — the wallet the operator is "
                + "shown is the empty one rgb-lib just created, and no refusal, log line or status ever says so. "
                + "Nothing in this plugin or in rgb-lib later reconciles the two, so the only remedy is "
                + "filesystem access, which the operator does not have");
        }
        finally { try { Directory.Delete(walletDataDir, true); } catch { } }
    }

    sealed record RestoreAttemptOutcome(
        Exception? Thrown, string WalletsParent, string[] DirectoriesLeftUnderTheWalletsParent)
    {
        public bool RefusedWith(string marker) =>
            Thrown is InvalidOperationException && Thrown.Message.Contains(marker, StringComparison.Ordinal);

        public string Describe() =>
            Thrown == null ? "a completed restore" : $"{Thrown.GetType().Name}: \"{Thrown.Message}\"";

        public void AssertNothingWasFinalized() =>
            Assert.True(DirectoriesLeftUnderTheWalletsParent.Length == 0,
                $"{DirectoriesLeftUnderTheWalletsParent.Length} directory/directories survive under "
                + $"{WalletsParent}; a refused restore must leave neither a staging tree nor a finalized wallet "
                + "data dir behind, or the next attempt collides with the leftovers and the operator cannot clear "
                + "them without shell access");
    }

    static async Task<RestoreAttemptOutcome> RestoreWithStagingShapedAs(
        Action<string> shape, string masterFingerprintKeyDerivationYields = FakeLibFingerprint)
    {
        ResetRestoreCooldown();
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-wallet-dir-root-{Guid.NewGuid():N}"));
        var svc = BuildService(new StagingShapingRunner(shape), cfg, masterFingerprintKeyDerivationYields);
        using var backup = new TempBackup();
        try
        {
            var thrown = await Record.ExceptionAsync(
                () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));
            var walletsParent = Path.GetDirectoryName(cfg.GetWalletDataDir("probe", "signet"))!;
            return new RestoreAttemptOutcome(thrown, walletsParent,
                Directory.Exists(walletsParent) ? Directory.GetDirectories(walletsParent) : []);
        }
        finally { try { Directory.Delete(cfg.RgbBaseDir, true); } catch { } }
    }

    static RGBWalletService BuildService(
        IRestoreProcessRunner runner, RGBConfiguration cfg, string masterFingerprintKeyDerivationYields)
    {
        var rgbLib = new FakeRgbLib(cfg, masterFingerprintKeyDerivationYields);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=99999;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    sealed class StagingShapingRunner : IRestoreProcessRunner
    {
        readonly Action<string> _shape;

        public StagingShapingRunner(Action<string> shape) => _shape = shape;

        public Task<RestoreRunResult> RunAsync(
            string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)
        {
            Directory.CreateDirectory(stagingDir);
            _shape(stagingDir);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"rgb-wallet-dir-root-backup-{Guid.NewGuid():N}.rgb");
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
