using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class RestoreProcessRunner : IRestoreProcessRunner
{
    readonly ILogger<RestoreProcessRunner> _log;
    readonly Func<ProcessStartInfo, IChildHandle> _handleFactory;
    readonly Func<string> _resolveHelperDll;
    readonly Func<string> _resolveDotnetHost;

    public RestoreProcessRunner(ILogger<RestoreProcessRunner> log,
        Func<ProcessStartInfo, IChildHandle>? handleFactory = null,
        Func<string>? resolveHelperDll = null,
        Func<string>? resolveDotnetHost = null)
    {
        _log = log;
        _handleFactory = handleFactory ?? (psi => new RealChildHandle(psi));
        _resolveHelperDll = resolveHelperDll ?? (() => Path.Combine(
            Path.GetDirectoryName(typeof(RestoreProcessRunner).Assembly.Location)!,
            "RgbRestoreHelper.dll"));
        _resolveDotnetHost = resolveDotnetHost ?? DefaultDotnetHost;
    }

    public async Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct)
    {
        if (ContainsALineBreakTheSingleLineStdinTransportCannotCarry(password))
            throw new InvalidOperationException(
                "The restore password contains a line break (CR or LF). It is handed to the restore "
                + "helper as a single line on standard input, which truncates it at the break, so "
                + "decryption could only ever fail. The wallet was not restored.");

        var helperDll = _resolveHelperDll();
        if (!File.Exists(helperDll))
            throw new InvalidOperationException(
                $"Restore helper not found at {helperDll}. The wallet was not restored.");

        var psi = BuildStartInfo(helperDll, backupPath, stagingDir, limits);
        // Starts before the process exists and before its password is delivered: the helper begins
        // native restore as soon as ReadLine sees the newline, so starting this after the stdin write
        // allowed child work (and even a full self-exit) to precede both Elapsed and the deadline.
        var sw = Stopwatch.StartNew();
        IChildHandle child;
        try
        {
            child = _handleFactory(psi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to launch the restore helper process. The wallet was not restored.", ex);
        }

        // Dispose kills a still-running child (see RealChildHandle.Dispose), so ANY exception
        // escaping this block — e.g. a broken-pipe write — terminates the child rather than
        // leaking a live, unbounded restore. That leak would reintroduce the very DoS this fixes.
        using (child)
        {
            await child.WriteStdinLineAndCloseAsync(password);

            var killedReason = RestoreKillReason.None;
            var killed = false;
            var deadline = limits.Timeout;

            while (!child.HasExited)
            {
                if (sw.Elapsed >= deadline) { killed = true; break; }
                var usage = MeasureStaging(stagingDir, limits.DiskCapBytes, limits.MaxStagingEntries);
                var reason = RestoreWatchdog.ShouldKill(
                    usage.Bytes, SafeWorkingSet(child), limits, usage.Entries);
                if (reason != RestoreKillReason.None) { killed = true; killedReason = reason; break; }
                // Re-checked AFTER the measurement, not only before it. MeasureStaging walks a tree the
                // restored file's author controls, so it consumes real time; checking the deadline only
                // on the way in let the kill land at deadline + scan time, which is precisely the
                // "work continues after the timeout" shape this whole child-process design exists to
                // remove. MeasureStaging is bounded, so this cannot be pushed out indefinitely, but the
                // deadline must still be the deadline.
                if (sw.Elapsed >= deadline) { killed = true; break; }
                try { await Task.Delay(limits.Poll, ct); }
                catch (OperationCanceledException) { killed = true; break; }
            }

            if (!killed && child.HasExited)
            {
                // One final measurement after a self-exit. The loop awaits a full poll interval before
                // re-checking, so a child that inflated fast and exited inside that window was never
                // measured at all: the caps were tripwires an attacker could step over between samples.
                var finalUsage = MeasureStaging(stagingDir, limits.DiskCapBytes, limits.MaxStagingEntries);
                var finalReason = RestoreWatchdog.ShouldKill(
                    finalUsage.Bytes, 0, limits, finalUsage.Entries);
                if (finalReason != RestoreKillReason.None)
                    return new RestoreRunResult(
                        finalReason == RestoreKillReason.Disk ? RestoreOutcome.KilledDisk : RestoreOutcome.KilledEntries,
                        child.ExitCode, "", true, sw.Elapsed, helperDll);

                return new RestoreRunResult(
                    RestoreOutcome.Exited, child.ExitCode, await child.ReadStdErrAsync(), true, sw.Elapsed,
                    helperDll);
            }

            child.Kill(true);
            var reaped = await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None);
            var outcome = killedReason switch
            {
                RestoreKillReason.Disk => RestoreOutcome.KilledDisk,
                RestoreKillReason.Ram => RestoreOutcome.KilledRam,
                RestoreKillReason.Entries => RestoreOutcome.KilledEntries,
                _ => RestoreOutcome.TimedOut
            };
            return new RestoreRunResult(outcome, null, "", reaped, sw.Elapsed, helperDll);
        }
    }

    public static bool ContainsALineBreakTheSingleLineStdinTransportCannotCarry(string? password)
    {
        if (password == null) return false;
        foreach (var c in password)
            if (c is '\n' or '\r') return true;
        return false;
    }

    public const string BackupPasswordLineBreakRefusal =
        "Backup password must not contain a line break (CR or LF). Restore hands the password to its "
        + "helper as a single line on standard input, so a password containing a line break is truncated "
        + "at the break before it is ever used and the backup could never be decrypted again. Spaces, "
        + "tabs, punctuation and non-ASCII letters are all fine.";

    // Framework-dependent hosting (BTCPay prod runs `dotnet BTCPayServer.dll`, dev runs `dotnet run`)
    // makes the current process host the dotnet muxer, so `<host> exec <helper.dll>` launches the
    // helper. A self-contained/apphost deploy would make the host BTCPayServer itself, and passing
    // `exec <helper.dll>` would spawn a second BTCPayServer — so fail closed unless the host is dotnet.
    static string DefaultDotnetHost() => ResolveDotnetHost(
        Environment.ProcessPath,
        RuntimeEnvironment.GetRuntimeDirectory(),
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        File.Exists,
        OperatingSystem.IsWindows());

    // Locate the dotnet muxer to run `dotnet exec RgbRestoreHelper.dll`. Environment.ProcessPath is
    // NOT reliably the muxer: `dotnet run` and any apphost deployment make it the BTCPayServer apphost,
    // and exec'ing THAT would spawn a second BTCPayServer. So resolve the real muxer, and fail closed
    // if it cannot be found rather than exec an unknown host.
    public static string ResolveDotnetHost(string? processPath, string? runtimeDir, string? dotnetRoot,
        Func<string, bool> fileExists, bool isWindows)
    {
        var muxer = isWindows ? "dotnet.exe" : "dotnet";

        if (!string.IsNullOrEmpty(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        // Shared-framework layout: <root>/shared/Microsoft.NETCore.App/<ver>/ -> <root>/<muxer>.
        if (!string.IsNullOrEmpty(runtimeDir))
        {
            var derived = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", "..", muxer));
            if (fileExists(derived)) return derived;
        }

        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var fromRoot = Path.Combine(dotnetRoot, muxer);
            if (fileExists(fromRoot)) return fromRoot;
        }

        throw new InvalidOperationException(
            "Could not locate the dotnet host to launch the restore helper. The wallet was not restored.");
    }

    ProcessStartInfo BuildStartInfo(string helperDll, string backupPath, string stagingDir, RestoreLimits limits)
    {
        var dotnet = _resolveDotnetHost();

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        var prlimit = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? ResolvePrlimitPath() : null;
        if (prlimit != null)
        {
            // prlimit sets the rlimit on itself then execvp()s the target IN PLACE (same PID),
            // and `dotnet exec` loads the CLR + helper + native rgblibcffi in-process (no fork).
            // So the tracked PID is the real restore process: WorkingSet64/Kill/WaitForExit all
            // observe/terminate the native work, not a wrapper. Use the verified absolute path (not a
            // bare name) so the launched binary never depends on PATH resolution.
            psi.FileName = prlimit;
            psi.ArgumentList.Add($"--cpu={(int)limits.CpuLimit.TotalSeconds}");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(dotnet);
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _log.LogWarning("prlimit unavailable on this Linux host — restore CPU is bounded only by the wall-clock kill");
            psi.FileName = dotnet;
        }
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(helperDll);
        psi.ArgumentList.Add(backupPath);
        psi.ArgumentList.Add(stagingDir);
        // Handed to the child so it can bound ITSELF: a wall-clock FailFast and a hard address-space
        // plus CPU rlimit that survive the parent dying or failing to kill. Clamped to the ranges the
        // child accepts, so a configuration the child would reject can never be produced here.
        psi.ArgumentList.Add(
            Math.Clamp((int)Math.Ceiling(limits.Timeout.TotalMilliseconds), 100, 3_600_000).ToString());
        psi.ArgumentList.Add(Math.Max(limits.RamCapBytes, 1).ToString());
        psi.ArgumentList.Add(
            Math.Clamp((int)Math.Ceiling(limits.CpuLimit.TotalSeconds), 1, 3_600).ToString());
        return psi;
    }

    internal static string? ResolvePrlimitPath()
    {
        foreach (var p in new[] { "/usr/bin/prlimit", "/bin/prlimit" })
            if (File.Exists(p)) return p;
        return null;
    }

    static long SafeWorkingSet(IChildHandle child)
    {
        try { return child.WorkingSet64; } catch { return 0; }
    }

    internal readonly record struct StagingUsage(long Bytes, int Entries);

    // Bounded on BOTH axes, and it stops at the first breach rather than totalling the tree: the
    // watchdog only needs to know THAT a cap is exceeded, never by how much. The unbounded
    // `EnumerateFiles(...).Sum(...)` this replaces did one stat per file per poll over
    // attacker-created content, so a hostile archive inflating to very many small files stayed under
    // the byte cap while making the parent's own scan the expensive part — work the child's
    // prlimit --cpu does not cover, because it runs in the BTCPay process.
    internal static StagingUsage MeasureStaging(string dir, long byteCap, int entryCap)
    {
        long bytes = 0;
        var entries = 0;
        try
        {
            // FileSystemInfos, not Files: directories cost a stat to walk and a recursive delete to
            // clean up, so counting only files let an output dominated by empty directories stay at
            // entries = 0 and bytes = 0 while re-imposing exactly the unbounded parent-side scan this
            // measurement exists to bound (measured: 100k empty dirs = 1.3 s per poll, 4.8 s to delete,
            // and no kill).
            foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                entries++;
                if (item is FileInfo file) bytes += file.Length;
                // Strictly greater, to agree with RestoreWatchdog.ShouldKill: returning here must
                // hand back a value that still trips the same comparison.
                if (bytes > byteCap || entries > entryCap)
                    return new StagingUsage(bytes, entries);
            }
        }
        catch { return new StagingUsage(bytes, entries); }
        return new StagingUsage(bytes, entries);
    }

    internal sealed class RealChildHandle : IChildHandle
    {
        const int StdErrCapChars = 8192;

        readonly Process _p;
        readonly Task<string> _stdout;
        readonly Task<string> _stderr;
        volatile bool _stdOutTruncated;

        // Only the native-send supervisor consults this: its stdout carries a PSBT or a txid, so a
        // silently truncated prefix would be parsed as a value. Restore's stdout is never read and
        // its stderr is only a diagnostic string, so dropped overflow there stays harmless.
        public bool StdOutTruncated => _stdOutTruncated;

        public RealChildHandle(ProcessStartInfo psi, int stdOutCapChars = StdErrCapChars)
        {
            Process? started = null;
            try
            {
                started = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                _p = started;
                _stdout = DrainCappedAsync(_p.StandardOutput, stdOutCapChars,
                    () => _stdOutTruncated = true);
                // Drain stderr concurrently from the start so a child that writes more than the OS pipe
                // buffer cannot block mid-restore (which would convert every restore into a timeout kill).
                // Retain only a capped prefix so a noisy child cannot shift the DoS into parent memory by
                // spewing unbounded stderr — the pipe is still fully drained, the overflow is discarded.
                _stderr = DrainCappedAsync(_p.StandardError, StdErrCapChars, onOverflow: null);
            }
            catch
            {
                if (started != null)
                {
                    try { if (!started.HasExited) started.Kill(entireProcessTree: true); } catch { }
                    var reaped = false;
                    try { reaped = started.WaitForExit(5_000); } catch { }
                    try { started.Dispose(); } catch { }
                    if (!reaped) throw new NativeSendChildUnreapedException();
                }
                throw;
            }
        }

        static async Task<string> DrainCappedAsync(StreamReader reader, int cap, Action? onOverflow)
        {
            var sb = new StringBuilder();
            var buf = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var room = cap - sb.Length;
                if (n > room) onOverflow?.Invoke();
                if (room > 0) sb.Append(buf, 0, Math.Min(n, room));
            }
            return sb.ToString();
        }
        public long WorkingSet64 { get { _p.Refresh(); return _p.WorkingSet64; } }
        public bool HasExited => _p.HasExited;
        public int ExitCode => _p.ExitCode;
        public void Kill(bool entireProcessTree)
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree); } catch { }
        }
        public async Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(grace);
            try { await _p.WaitForExitAsync(cts.Token); return true; }
            catch (OperationCanceledException) { return _p.HasExited; }
        }
        public Task<string> ReadStdErrAsync() => _stderr;
        public Task<string> ReadStdOutAsync() => _stdout;
        public async Task WriteStdinLineAndCloseAsync(string line)
        {
            try
            {
                await _p.StandardInput.WriteLineAsync(line);
                await _p.StandardInput.FlushAsync();
                _p.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Child already exited (broken pipe): the supervise loop observes HasExited and
                // reports the real exit code — do not fault the whole restore on a closed stdin.
            }
        }
        public void Dispose()
        {
            try { if (!_p.HasExited) _p.Kill(true); } catch { }
            try { if (!_p.HasExited) _p.WaitForExit(5_000); } catch { }
            try { _p.Dispose(); } catch { }
        }
    }
}
