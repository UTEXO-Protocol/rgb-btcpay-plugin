using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreStderrHostPathRedactionTests
{
    const string UploadedBackupPath = "/Users/someone/tmp/rgb-restore-0123456789abcdef.rgb";

    const string StagingDir =
        "/Users/someone/.btcpayserver/Main/rgb-wallets/rgb-restore-staging-w1-0123456789abcdef";

    const string OperatorFallback = "Restore failed. Check server logs for details.";

    const string HelperDll =
        "/Users/someone/.btcpayserver/Plugins/BTCPayServer.Plugins.RgbUtexo/RgbRestoreHelper.dll";

    static string Redact(string childStdErr) =>
        RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
            childStdErr, UploadedBackupPath, StagingDir);

    static string RedactWithTheHelperThePluginExecd(string childStdErr) =>
        RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
            childStdErr, UploadedBackupPath, StagingDir, HelperDll);

    static void AssertNamesNoHostLocation(string shown)
    {
        Assert.DoesNotContain("/Users/", shown);
        Assert.DoesNotContain(".btcpayserver", shown);
        Assert.DoesNotContain("rgb-wallets", shown);
        Assert.DoesNotContain(StagingDir, shown);
        Assert.DoesNotContain(UploadedBackupPath, shown);
    }

    [Fact]
    public void DotnetHostBootstrapFailureNamingTheHelperAssembly_LosesThatPathButKeepsTheDiagnosis()
    {
        var shown = RedactWithTheHelperThePluginExecd($"The application '{HelperDll}' does not exist.");

        AssertNamesNoHostLocation(shown);
        Assert.Equal(
            $"The application '{RgbHelperStderrRedaction.RestoreHelperAssemblyPlaceholder}' does not exist.",
            shown);
    }

    [Fact]
    public void DotnetHostBootstrapFailureNamingTheRuntimeconfigBesideTheHelper_LosesThePluginInstallDirectory()
    {
        var installDir = Path.GetDirectoryName(HelperDll)!;

        var shown = RedactWithTheHelperThePluginExecd(
            "A fatal error occurred. The required library "
            + $"{installDir}/RgbRestoreHelper.runtimeconfig.json was not found. "
            + $"Consider also {installDir}/RgbRestoreHelper.runtimeconfig.dev.json.");

        AssertNamesNoHostLocation(shown);
        Assert.DoesNotContain(installDir, shown);
        Assert.Contains("A fatal error occurred.", shown);
        Assert.Contains(
            RgbHelperStderrRedaction.PluginInstallDirectoryPlaceholder + "/RgbRestoreHelper.runtimeconfig.json",
            shown);
        Assert.Contains(
            RgbHelperStderrRedaction.PluginInstallDirectoryPlaceholder
            + "/RgbRestoreHelper.runtimeconfig.dev.json",
            shown);
    }

    [Fact]
    public void AHelperPathTheRunnerNeverReported_LeavesEveryOtherSubstitutionIntact()
    {
        var shown = Redact($"Empty file: {UploadedBackupPath}");

        Assert.Equal("Empty file: " + RgbHelperStderrRedaction.UploadedBackupFilePlaceholder, shown);
    }

    [Fact]
    public void WalletDirAlreadyExists_LosesTheStagingPathButKeepsTheDiagnosis()
    {
        var shown = Redact(
            $"The specified wallet directory already exists: {StagingDir}/aabbccdd");

        AssertNamesNoHostLocation(shown);
        Assert.Equal(
            "The specified wallet directory already exists: "
            + RgbHelperStderrRedaction.StagingDirectoryPlaceholder + "/aabbccdd",
            shown);
    }

    [Fact]
    public void EmptyFileVariant_LosesTheUploadedBackupPathButKeepsTheDiagnosis()
    {
        var shown = Redact($"Empty file: {UploadedBackupPath}");

        AssertNamesNoHostLocation(shown);
        Assert.Equal("Empty file: " + RgbHelperStderrRedaction.UploadedBackupFilePlaceholder, shown);
    }

    [Fact]
    public void RgbLibsOwnTempFilesUnderTheUploadDirectory_LoseTheirPrefix()
    {
        var shown = Redact(
            "I/O error: Permission denied (os error 13): /Users/someone/tmp/.tmpAbCdEf/backup.enc");

        AssertNamesNoHostLocation(shown);
        Assert.Contains("Permission denied (os error 13)", shown);
        Assert.Contains(RgbHelperStderrRedaction.UploadDirectoryPlaceholder + "/.tmpAbCdEf/backup.enc", shown);
    }

    [Fact]
    public void WalletDataDirectoryNamedWithoutTheStagingLeaf_LosesItsPrefix()
    {
        var shown = Redact(
            "I/O error: No space left on device: /Users/someone/.btcpayserver/Main/rgb-wallets");

        AssertNamesNoHostLocation(shown);
        Assert.Contains("No space left on device", shown);
        Assert.Contains(RgbHelperStderrRedaction.WalletDataDirectoryPlaceholder, shown);
    }

    public static TheoryData<string> ActionableRefusalsRgbLibEmitsWithNoPathAtAll() => new()
    {
        "The provided password is incorrect",
        "Internal error: Zip error: invalid Zip archive: Could not find central directory end",
        "Backup version not supported",
        "restore_backup failed",
        "usage: RgbRestoreHelper <backupPath> <stagingDir> <timeoutMs> <memoryLimitBytes> <cpuLimitSeconds> "
            + "(password on stdin)",
        "no password provided on stdin"
    };

    [Theory]
    [MemberData(nameof(ActionableRefusalsRgbLibEmitsWithNoPathAtAll))]
    public void PathFreeChildDiagnostic_ReachesTheOperatorWordForWord_BecauseItIsHisOnlySelfServiceDiagnosis(
        string childStdErr)
    {
        Assert.Equal(childStdErr, Redact(childStdErr));
    }

    [Theory]
    [InlineData("bk", "Internal error: Zip error: bad bkdr signature")]
    [InlineData("rel/bkup.rgb", "Empty file: rel/bkup.rgb")]
    public void ARelativeHelperArgumentIsNeverSubstituted_BecauseItNamesNoHostLocationToHide(
        string relativeBackupPath, string childStdErr)
    {
        var shown = RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
            childStdErr, relativeBackupPath, StagingDir);

        Assert.Equal(childStdErr, shown);
    }

    [Fact]
    public void AFilesystemRootIsNeverSubstituted_SoASeparatorCannotBeReplacedEverywhere()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var shown = RgbHelperStderrRedaction.ReplaceOnlyTheAbsolutePathsThePluginItselfHandedTheRestoreHelper(
            "I/O error: No space left on device", root, root);

        Assert.Equal("I/O error: No space left on device", shown);
    }

    sealed class FakeRunner : IRestoreProcessRunner
    {
        public string StdErr = "";
        public string? HelperDllItExecd;

        public Task<RestoreRunResult> RunAsync(string backupPath, string stagingDir, string password,
            RestoreLimits limits, CancellationToken ct)
            => Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 1, StdErr, ChildReaped: true,
                HelperDllHandedToTheDotnetHost: HelperDllItExecd));
    }

    sealed class CapturingLogger : ILogger<RestoreExecutor>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    static async Task<(string ShownToTheStoreOwner, List<string> LogMessages)> RunFailingRestore(
        string childStdErr, string backupPath, string stagingDir, string? helperDll = null)
    {
        var log = new CapturingLogger();
        var exec = new RestoreExecutor(new FakeRunner { StdErr = childStdErr, HelperDllItExecd = helperDll },
            new RGBConfiguration(Path.GetTempPath()), log);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exec.ExecuteAsync(backupPath, stagingDir, "pw", CancellationToken.None));

        return (RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(ex, OperatorFallback),
            log.Messages);
    }

    [Fact]
    public async Task StagingPathBearingChildFailure_ReachesTheStoreOwnerWithoutTheHostPath_AndStaysInTheServerLog()
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"rgb-redaction-test-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(Path.GetTempPath(), $"rgb-redaction-upload-{Guid.NewGuid():N}.rgb");
        Directory.CreateDirectory(stagingDir);
        try
        {
            var (shown, logMessages) = await RunFailingRestore(
                $"The specified wallet directory already exists: {stagingDir}/aabbccdd",
                backupPath, stagingDir);

            Assert.DoesNotContain(stagingDir, shown);
            Assert.Contains("Restore failed: The specified wallet directory already exists: "
                + RgbHelperStderrRedaction.StagingDirectoryPlaceholder, shown);
            Assert.Contains(logMessages, m => m.Contains(stagingDir, StringComparison.Ordinal)
                && m.Contains("aabbccdd", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
    }

    [Fact]
    public async Task WrongBackupPassword_StillTellsTheStoreOwnerExactlyWhatToFix_WithoutShellAccess()
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"rgb-redaction-test-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(Path.GetTempPath(), $"rgb-redaction-upload-{Guid.NewGuid():N}.rgb");
        Directory.CreateDirectory(stagingDir);
        try
        {
            var (shown, _) = await RunFailingRestore(
                "The provided password is incorrect", backupPath, stagingDir);

            Assert.Equal("Restore failed: The provided password is incorrect", shown);
            Assert.NotEqual(OperatorFallback, shown);
        }
        finally { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
    }

    [Fact]
    public async Task HostBootstrapStderr_ReachesTheStoreOwnerWithoutThePluginInstallPath_AndStaysInTheServerLog()
    {
        var stagingDir = Path.Combine(Path.GetTempPath(), $"rgb-redaction-test-{Guid.NewGuid():N}");
        var backupPath = Path.Combine(Path.GetTempPath(), $"rgb-redaction-upload-{Guid.NewGuid():N}.rgb");
        var installDir = Path.Combine(Path.GetTempPath(), $"rgb-redaction-plugins-{Guid.NewGuid():N}");
        var helperDll = Path.Combine(installDir, "RgbRestoreHelper.dll");
        Directory.CreateDirectory(stagingDir);
        try
        {
            var (shown, logMessages) = await RunFailingRestore(
                $"A fatal error occurred. The required library {installDir}"
                + $"{Path.DirectorySeparatorChar}RgbRestoreHelper.runtimeconfig.json was not found.",
                backupPath, stagingDir, helperDll);

            Assert.DoesNotContain(installDir, shown);
            Assert.DoesNotContain(helperDll, shown);
            Assert.Contains("A fatal error occurred.", shown);
            Assert.Contains(RgbHelperStderrRedaction.PluginInstallDirectoryPlaceholder, shown);
            Assert.Contains(logMessages, m => m.Contains(installDir, StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); }
    }
}
