namespace RgbRestoreHelper;

public static class Program
{
    public static Action<long, int> ApplyResourceLimits { get; set; } = NativeSendResourceLimiter.Apply;

    public static int Main(string[] args)
        => Run(args, Console.In, Console.Out, Console.Error);

    public static int Run(string[] args, TextReader stdin, TextWriter stderr)
        => Run(args, stdin, TextWriter.Null, stderr);

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 4 && args[0] is "send-begin" or "send-end"
            && int.TryParse(args[1], out var selfTimeoutMs) && selfTimeoutMs is >= 100 and <= 600_000
            && long.TryParse(args[2], out var memoryLimitBytes) && memoryLimitBytes > 0
            && int.TryParse(args[3], out var cpuLimitSeconds) && cpuLimitSeconds is >= 1 and <= 600)
        {
            using var watchdog = new Timer(_ => Environment.FailFast("native send helper self-timeout"),
                null, selfTimeoutMs, Timeout.Infinite);
            try
            {
                // Apply the hard limit before reading the request. No attacker-influenced native wallet
                // construction or network work can begin unless the platform accepted the limit.
                ApplyResourceLimits(memoryLimitBytes, cpuLimitSeconds);
                var request = stdin.ReadToEnd();
                stdout.Write(RgbNativeSend.Invoke(args[0], request));
                return 0;
            }
            catch (Exception ex)
            {
                stderr.WriteLine(ex.GetBaseException().Message);
                return 1;
            }
        }

        // The restore child gets the same two containments as the send child. Without them its only
        // bounds were parent-side — a polling wall-clock kill plus prlimit --cpu on Linux only — so an
        // orphan left by a dead parent, or by a Kill that failed, kept decrypting and inflating an
        // attacker-supplied archive with no wall-clock and no memory bound on any platform. The
        // budgets are the RESTORE limits, not the send ones: scrypt plus inner-ZIP inflation
        // legitimately needs more time and more memory than a send.
        if (args.Length != 5
            || !int.TryParse(args[2], out var restoreTimeoutMs)
            || restoreTimeoutMs is < 100 or > 3_600_000
            || !long.TryParse(args[3], out var restoreMemoryLimitBytes)
            || restoreMemoryLimitBytes <= 0
            || !int.TryParse(args[4], out var restoreCpuLimitSeconds)
            || restoreCpuLimitSeconds is < 1 or > 3_600)
        {
            stderr.WriteLine("usage: RgbRestoreHelper <backupPath> <stagingDir> <timeoutMs> "
                + "<memoryLimitBytes> <cpuLimitSeconds> (password on stdin)");
            return 2;
        }

        using var restoreWatchdog = new Timer(_ => Environment.FailFast("restore helper self-timeout"),
            null, restoreTimeoutMs, Timeout.Infinite);
        try
        {
            ApplyResourceLimits(restoreMemoryLimitBytes, restoreCpuLimitSeconds);
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.GetBaseException().Message);
            return 4;
        }

        var password = stdin.ReadLine();
        if (string.IsNullOrWhiteSpace(password))
        {
            stderr.WriteLine("no password provided on stdin");
            return 3;
        }

        try
        {
            var rc = RgbRestoreNative.Restore(args[0], args[1], password, out var error);
            if (rc != 0) stderr.WriteLine(error);
            return rc;
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }
    }
}
