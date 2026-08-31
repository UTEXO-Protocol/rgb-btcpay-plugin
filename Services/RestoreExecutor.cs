using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class RestoreExecutor
{
    readonly IRestoreProcessRunner _runner;
    readonly RGBConfiguration _cfg;
    readonly ILogger<RestoreExecutor> _log;

    public RestoreExecutor(IRestoreProcessRunner runner, RGBConfiguration cfg, ILogger<RestoreExecutor> log)
    {
        _runner = runner;
        _cfg = cfg;
        _log = log;
    }

    public async Task ExecuteAsync(string backupPath, string stagingDir, string password, CancellationToken ct)
    {
        var limits = _cfg.ToRestoreLimits();
        var result = await _runner.RunAsync(backupPath, stagingDir, password, limits, ct);

        if (result.Outcome == RestoreOutcome.Exited && result.ExitCode == 0 && result.ChildReaped)
            return;

        if (result.ChildReaped)
            TryDeleteStaging(stagingDir);
        else
            _log.LogWarning("Restore child not confirmed reaped — leaving staging dir {Dir} for the startup sweep", stagingDir);

        if (result.Outcome == RestoreOutcome.Exited)
        {
            _log.LogError(
                "Restore helper exited with code {ExitCode}; unredacted helper stderr: {StdErr} "
                + "(backup file {BackupPath}, staging dir {StagingDir})",
                result.ExitCode, result.StdErr, backupPath, stagingDir);
            var redactedStdErr = RgbHelperStderrRedaction
                .ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
                    result.StdErr, backupPath, stagingDir, result.HelperDllHandedToTheDotnetHost);
            throw new InvalidOperationException("Restore failed: "
                + (string.IsNullOrWhiteSpace(redactedStdErr)
                    || RgbBoundRefusal.AnExitStatusNoHelperInThisPluginReturnsOfItsOwnAccord(
                        result.ExitCode)
                    ? RefusalForAHelperWhoseExitCannotRuleOutTheBudgetsThisPluginGaveIt(
                        result.ExitCode, redactedStdErr, limits.RamCapBytes)
                    : redactedStdErr));
        }

        if (result.Outcome is RestoreOutcome.KilledDisk)
            throw new RestoreAbortedException(
                RefusalForAWalletDirectoryThatOutgrewTheStagingBudget(limits.DiskCapBytes));

        if (result.Outcome is RestoreOutcome.KilledRam)
            throw new RestoreAbortedException(
                RefusalForARestoreThatReachedItsMemoryBudget(limits.RamCapBytes));

        if (result.Outcome is RestoreOutcome.KilledEntries)
            throw new RestoreAbortedException(
                RefusalForAWalletDirectoryHoldingMoreStagingEntriesThanTheWatchdogWillWalk(
                    limits.MaxStagingEntries));

        throw new RestoreAbortedException(RefusalForARestoreThatRanOutOfTime(limits.Timeout));
    }

    internal static string RefusalForAWalletDirectoryThatOutgrewTheStagingBudget(long diskCapBytes) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"The restored wallet data reached the {diskCapBytes / (1024 * 1024)} MB staging size limit, "
            + "so the restore was stopped and nothing was kept.",
            "That limit is measured over the wallet directory AFTER it is decompressed, while the "
            + "upload limit and backup validation measure the compressed, encrypted backup file, so a "
            + "backup file those accepted can still reach this one.",
            "The backup file is undamaged: keep it.",
            "RGB_RESTORE_DISK_CAP_BYTES",
            $"maximum {RGBConfiguration.RestoreDiskCapMaxBytes / (1024 * 1024)} MB",
            "retry the restore");

    internal static string RefusalForARestoreThatRanOutOfTime(TimeSpan timeout) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"Backup restore timed out after {(int)timeout.TotalSeconds} seconds and was stopped; "
            + "nothing was kept.",
            "A large wallet directory can need longer than the shipped limit to decompress.",
            "The backup file is undamaged: keep it.",
            "RGB_RESTORE_TIMEOUT_SECONDS",
            $"maximum {RGBConfiguration.RestoreSecondsMax} seconds",
            "retry the restore");

    internal static string RefusalForARestoreThatReachedItsMemoryBudget(long ramCapBytes) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"Backup restore reached the {ramCapBytes / (1024 * 1024)} MB memory limit and was stopped; "
            + "nothing was kept.",
            "That limit bounds the whole restore helper process, while the pre-flight check bounds only "
            + "the key-derivation cost declared inside the backup file, so a backup that check accepted "
            + "can still reach this one. A backup written by a later rgb-lib with a higher "
            + "key-derivation cost is the case that does; a backup this plugin wrote stays well under "
            + "the shipped limit.",
            "No wallet was created on this server and the backup file is undamaged: keep it.",
            "RGB_RESTORE_RAM_CAP_BYTES",
            $"maximum {RGBConfiguration.RestoreRamMaxBytes / (1024 * 1024)} MB",
            "retry the restore");

    internal static string RefusalForAWalletDirectoryHoldingMoreStagingEntriesThanTheWatchdogWillWalk(
        int maxStagingEntries) =>
        RgbBoundRefusal.ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
            $"The restored wallet data reached the {maxStagingEntries} staging entry limit, so the "
            + "restore was stopped and nothing was kept. That limit counts directories as well as "
            + "files, so it is not a count of files alone.",
            "It is counted over the wallet directory AFTER it is decompressed, while backup validation "
            + "counts the entries of the outer archive, which holds a single encrypted file, so a "
            + "backup that validation accepted can still reach this one. rgb-lib never prunes the "
            + "per-transfer files and directories it writes, so a wallet that has received or sent many "
            + "thousands of transfers reaches this count legitimately while staying far under the "
            + "staging size limit.",
            "The backup file is undamaged: keep it.",
            "RGB_RESTORE_MAX_STAGING_ENTRIES",
            "this build puts no ceiling on that variable; the staging size limit and the restore "
            + "deadline still bound the restore",
            "retry the restore");

    static string RefusalForAHelperWhoseExitCannotRuleOutTheBudgetsThisPluginGaveIt(
        int? exitCode, string whatTheHelperPrintedBeforeItStopped, long ramCapBytes) =>
        "the restore helper stopped with "
        + RgbBoundRefusal.DescribeExitStatusForAnOperatorWithoutShellAccess(exitCode)
        + (string.IsNullOrWhiteSpace(whatTheHelperPrintedBeforeItStopped)
            ? " and wrote no error output at all."
            : $", after writing: {whatTheHelperPrintedBeforeItStopped.Trim()}")
        + " Nothing on this server was changed and no wallet was created. The restore helper applies "
        + "this plugin's own memory and CPU budgets to itself before it opens the backup, and an "
        + $"allocation the {ramCapBytes / (1024 * 1024)} MB memory budget refuses is answered inside the "
        + "helper, which ends there without this server's watchdog ever seeing it grow — so an exit like "
        + "this one does not rule the memory budget out. A helper killed from outside, by the memory the "
        + "host or container allows BTCPay, ends the same way. The limits to raise first are therefore "
        + $"the restore memory limit (RGB_RESTORE_RAM_CAP_BYTES, maximum "
        + $"{RGBConfiguration.RestoreRamMaxBytes / (1024 * 1024)} MB) and the restore CPU limit "
        + "(RGB_RESTORE_CPU_LIMIT_SECONDS); restart BTCPay after changing either, then restore the same "
        + "backup again. Not every exit that reaches this message is a budget, though: a status the "
        + "helper never returns of its own accord is also what a helper that could not start at all "
        + "leaves behind, which an incomplete or mismatched BTCPay installation causes and which no "
        + "limit will fix. The BTCPay server log records this attempt in full, and that entry is what "
        + "separates the two.";

    void TryDeleteStaging(string stagingDir)
    {
        try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
        catch (Exception ex) { _log.LogDebug(ex, "Failed to clean up staging dir {Dir}", stagingDir); }
    }
}

internal static class RgbBoundRefusal
{
    internal static string ForABoundAnOperatorMustBeAbleToRaiseWithoutHostShellAccess(
        string whatWasStoppedWithTheQuantityAndItsUnit,
        string whyWorkThatIsNotHostileCanReachThisBound,
        string whatThisAttemptLeftBehind,
        string environmentVariableThatRaisesIt,
        string whatThatVariableAccepts,
        string theNextActionToTake) =>
        $"{whatWasStoppedWithTheQuantityAndItsUnit} {whyWorkThatIsNotHostileCanReachThisBound} "
        + $"{whatThisAttemptLeftBehind} Raise the limit by setting the "
        + $"{environmentVariableThatRaisesIt} environment variable ({whatThatVariableAccepts}) and "
        + $"restarting BTCPay, then {theNextActionToTake}.";

    internal const int HighestExitStatusAnyHelperInThisPluginReturnsOfItsOwnAccord = 4;

    internal static bool AnExitStatusNoHelperInThisPluginReturnsOfItsOwnAccord(int? exitCode) =>
        exitCode is int status
        && status is < 0 or > HighestExitStatusAnyHelperInThisPluginReturnsOfItsOwnAccord;

    internal static string DescribeExitStatusForAnOperatorWithoutShellAccess(int? exitCode) =>
        exitCode is null
            ? "an exit status the supervisor could not read"
            : AnExitStatusNoHelperInThisPluginReturnsOfItsOwnAccord(exitCode)
                ? $"exit status {exitCode} (not a status this plugin's own helper ever returns, so what "
                  + "ended it cannot be read off the status)"
                : $"exit status {exitCode}";
}
