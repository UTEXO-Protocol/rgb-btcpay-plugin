namespace BTCPayServer.Plugins.RgbUtexo.Services;

public enum RestoreOutcome { Exited, TimedOut, KilledDisk, KilledRam, KilledEntries }

public enum RestoreKillReason { None, Disk, Ram, Entries }

// MaxStagingEntries carries a default so every existing construction site keeps compiling; a call
// that omits it opts out of the entry bound rather than silently getting a bound of zero.
public sealed record RestoreLimits(
    TimeSpan Timeout,
    long DiskCapBytes,
    long RamCapBytes,
    TimeSpan CpuLimit,
    TimeSpan Poll,
    TimeSpan ReapGrace,
    int MaxStagingEntries = int.MaxValue);

// Elapsed covers process creation and password delivery as well as the poll loop. It is returned so
// tests can pin that the watchdog's deadline starts before any child work, and so abort diagnostics
// can report the actual supervised lifetime without maintaining a second clock.
public sealed record RestoreRunResult(
    RestoreOutcome Outcome,
    int? ExitCode,
    string StdErr,
    bool ChildReaped,
    TimeSpan Elapsed = default,
    string? HelperDllHandedToTheDotnetHost = null);

public interface IRestoreProcessRunner
{
    Task<RestoreRunResult> RunAsync(
        string backupPath, string stagingDir, string password,
        RestoreLimits limits, CancellationToken ct);
}

public interface IChildHandle : IDisposable
{
    long WorkingSet64 { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill(bool entireProcessTree);
    Task<bool> WaitForExitAsync(TimeSpan grace, CancellationToken ct);
    Task<string> ReadStdOutAsync();
    Task<string> ReadStdErrAsync();
    Task WriteStdinLineAndCloseAsync(string line);
    bool StdOutTruncated => false;
}

public static class RestoreWatchdog
{
    // stagingEntries defaults to 0 so pre-existing two-metric callers keep their exact behaviour:
    // zero entries can never exceed a positive cap.
    public static RestoreKillReason ShouldKill(
        long dirSizeBytes, long rssBytes, RestoreLimits limits, int stagingEntries = 0)
    {
        if (dirSizeBytes > limits.DiskCapBytes) return RestoreKillReason.Disk;
        if (rssBytes > limits.RamCapBytes) return RestoreKillReason.Ram;
        if (stagingEntries > limits.MaxStagingEntries) return RestoreKillReason.Entries;
        return RestoreKillReason.None;
    }
}
