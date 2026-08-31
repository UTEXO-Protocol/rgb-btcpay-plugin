using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Services.Rates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[CollectionDefinition("RestoreSerial", DisableParallelization = true)]
public sealed class RestoreSerialCollection { }

[Collection("RestoreSerial")]
public class RestoreGateTests
{
    public RestoreGateTests()
    {
        // The production gate is process-wide; each test starts a fresh process-state scenario.
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
    }

    sealed class BlockingRunner : IRestoreProcessRunner
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Entered;
        public async Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            Started.TrySetResult();
            await Release.Task;
            return new RestoreRunResult(RestoreOutcome.Exited, 0, "", true);
        }
    }

    sealed class ThrowingRunner : IRestoreProcessRunner
    {
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    sealed class SuccessfulRunner : IRestoreProcessRunner
    {
        public int Entered;
        public Task<RestoreRunResult> RunAsync(string b, string s, string p, RestoreLimits l, CancellationToken ct)
        {
            Interlocked.Increment(ref Entered);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    static RGBWalletService BuildService(IRestoreProcessRunner runner)
    {
        var cfg = new RGBConfiguration(Path.Combine(Path.GetTempPath(), $"rgb-gate-{Guid.NewGuid():N}"));
        var rgbLib = new FakeRgbLib(cfg);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    const string Mnemonic = "trophy hire lady move shuffle quit explain track praise twenty walnut awful";

    // These tests used the literal path "bk", which never existed on disk. That was harmless while the
    // restore path touched the file only inside the child process, and became a HANG the moment a
    // pre-flight file check was added ahead of the single-flight gate: the first restore threw before
    // reaching the runner, so `await runner.Started.Task` waited forever. Using a real minimal archive
    // keeps these tests aimed at the gate instead of accidentally depending on where file IO happens.
    //
    // The pub_data below carries rgb-lib's own honest scrypt parameters (log_n 17, r 8, p 1, len 32 =
    // 128MB), read out of a real beta.30 backup, so RgbBackupScryptGuard accepts it.
    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rgb-gate-backup-{Guid.NewGuid():N}.rgb");
            using var fs = File.Create(Path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            using (var enc = zip.CreateEntry("backup.enc").Open())
                enc.Write(new byte[16]);
            using var pub = new StreamWriter(zip.CreateEntry("backup.pub_data").Open());
            pub.Write("""{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""");
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    [Fact]
    public async Task SecondConcurrentRestore_IsRejected()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        using var backup = new TempBackup();
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet");
        await runner.Started.Task;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, backup.Path, "pw", "signet"));
        Assert.Contains("already in progress", ex.Message);

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task ConcurrentMalformedBackup_IsRejectedBeforeParentArchiveParsing()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        using var backup = new TempBackup();
        var malformed = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rgb-malformed-{Guid.NewGuid():N}.rgb");
        await File.WriteAllTextAsync(malformed, "not a zip");
        try
        {
            var first = svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet");
            await runner.Started.Task;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RestoreFromBackupAsync("store2", Mnemonic, malformed, "pw", "signet"));
            Assert.Contains("already in progress", ex.Message);

            runner.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<Exception>(() => first);
        }
        finally { File.Delete(malformed); }
    }

    [Fact]
    public async Task RejectPath_DoesNotOverReleaseGate()
    {
        var runner = new BlockingRunner();
        var svc = BuildService(runner);
        using var backup = new TempBackup();
        var first = svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet");
        await runner.Started.Task;
        Assert.Equal(1, runner.Entered);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, backup.Path, "pw", "signet"));

        var third = svc.RestoreFromBackupAsync("store3", Mnemonic, backup.Path, "pw", "signet");
        var finished = await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.True(finished == third,
            "third restore entered the runner — the reject path over-released the gate");
        var ex3 = await Assert.ThrowsAsync<InvalidOperationException>(() => third);
        Assert.Contains("already in progress", ex3.Message);
        Assert.Equal(1, runner.Entered);

        runner.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => first);
    }

    [Fact]
    public async Task GateReleased_AfterMidRunThrow_ButCooldownBlocksNextRestore()
    {
        var svc = BuildService(new ThrowingRunner());
        using var backup = new TempBackup();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet"));
        Assert.DoesNotContain("already in progress", ex.Message);
        Assert.Contains("attempted recently", ex.Message);
    }

    [Fact]
    public async Task SuccessfulNativeAttemptAlsoArmsCooldown()
    {
        var runner = new SuccessfulRunner();
        var svc = BuildService(runner);
        using var backup = new TempBackup();

        // The fake child succeeds; later wallet-shape validation fails because it produced no staging
        // tree. The cooldown must already be armed by the native attempt itself, independent of outcome.
        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.RestoreFromBackupAsync("store1", Mnemonic, backup.Path, "pw", "signet"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store2", Mnemonic, backup.Path, "pw", "signet"));

        Assert.Contains("attempted recently", ex.Message);
        Assert.Equal(1, runner.Entered);
    }
}
