using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public enum SelfCheckState
{
    Absent = 1,
    PresentButUnloadable = 2,
    WrongVersion = 3,
    Healthy = 4,
    SelfCheckFailed = 5
}

/// <summary>
/// The normative operator-facing tokens. They are fixed strings here rather than derived from the
/// implementation: a token the test computes from the code under test asserts nothing.
/// </summary>
internal static class SelfCheckTokens
{
    internal const string Consequence = "All RGB asset sends will be rejected";
    internal const string ReceivingUnaffected = "Receiving RGB assets and the rest of the plugin are unaffected";
    internal const string IssuesUrl = "https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues";
    internal const string BuildScript = "build-native.sh";

    internal const string LoadFailedOpening = "The RGB pre-sign verification library could not be loaded.";
    internal const string WrongVersionOpening = "The RGB pre-sign verification library loaded but is the wrong version.";
    internal const string SelfCheckFailedOpening = "The RGB pre-sign verification self-check failed.";

    internal const string CouldNotLoadFragment = "could not be loaded";
    internal const string WrongVersionFragment = "is the wrong version";
    internal const string SelfCheckFailedFragment = "self-check failed";

    internal const string Absent = "is absent from this build";
    internal const string PackagingDefect = "known packaging defect";
    internal const string Unloadable = "exists but could not be loaded";
    internal const string ExpectedSymbol = "expected symbol";
    internal const string ArchitectureMismatch = "architecture mismatch";
    internal const string SystemLibraries = "incompatible system libraries";
    internal const string AbiMismatch = "ABI/version mismatch";

    internal const string NeverUnsupported = "unsupported";
    internal const string NeverInstallFixedBuild = "install a fixed build";
    internal const string NeverPackScript = "pack-rgbverify.sh";
    internal const string NeverPackage = "RgbVerifyCffi";

    internal static readonly string[] RequiredExports =
    [
        "rgbverify_decode_invoice",
        "rgbverify_validate",
        "rgbverify_commitment_check",
        "rgbverify_validate_v2",
        "rgbverify_string_free"
    ];

    // Clauses every failure state carries. Without them in each row the two never-name rows and the
    // developer-remediation-last requirement bind nothing in that state.
    internal static void AssertEveryStateClauses(string message)
    {
        Assert.Contains(Consequence, message, StringComparison.Ordinal);
        Assert.Contains(ReceivingUnaffected, message, StringComparison.Ordinal);

        Assert.Contains(IssuesUrl, message, StringComparison.Ordinal);
        Assert.Contains(BuildScript, message, StringComparison.Ordinal);
        Assert.True(message.IndexOf(BuildScript, StringComparison.Ordinal)
                    > message.IndexOf(IssuesUrl, StringComparison.Ordinal),
            "developer remediation must come after the reporting channel an operator can actually use");

        Assert.DoesNotContain(NeverPackScript, message, StringComparison.Ordinal);
        Assert.DoesNotContain(NeverPackage, message, StringComparison.Ordinal);
        Assert.DoesNotContain(NeverUnsupported, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(NeverInstallFixedBuild, message, StringComparison.OrdinalIgnoreCase);
    }

    // Consequence-first ordering: an operator who reads one line must learn what breaks before any
    // diagnostic detail. Only detail tokens actually present are compared — IndexOf returns -1 for
    // the ones a given state legitimately has no value for.
    internal static void AssertConsequencePrecedesDetails(string message, params string?[] detailTokens)
    {
        var consequence = message.IndexOf(Consequence, StringComparison.Ordinal);
        Assert.True(consequence >= 0, "the consequence line is missing entirely");

        var present = detailTokens
            .Where(token => !string.IsNullOrEmpty(token))
            .Select(token => message.IndexOf(token!, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .ToList();
        Assert.NotEmpty(present);
        Assert.True(consequence < present.Min(),
            "the consequence must be stated before the first diagnostic detail");
    }

    // Full mutual exclusion: each state carries its own tokens and none of the other three states'.
    internal static void AssertStateTokens(string message, SelfCheckState state)
    {
        switch (state)
        {
            case SelfCheckState.Absent:
            case SelfCheckState.PresentButUnloadable:
                Assert.StartsWith(LoadFailedOpening, message, StringComparison.Ordinal);
                break;
            case SelfCheckState.WrongVersion:
                Assert.StartsWith(WrongVersionOpening, message, StringComparison.Ordinal);
                break;
            case SelfCheckState.SelfCheckFailed:
                Assert.StartsWith(SelfCheckFailedOpening, message, StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "not a failure state");
        }

        AssertFragment(message, CouldNotLoadFragment,
            state is SelfCheckState.Absent or SelfCheckState.PresentButUnloadable);
        AssertFragment(message, WrongVersionFragment, state is SelfCheckState.WrongVersion);
        AssertFragment(message, SelfCheckFailedFragment, state is SelfCheckState.SelfCheckFailed);

        AssertFragment(message, Absent, state is SelfCheckState.Absent);
        AssertFragment(message, PackagingDefect, state is SelfCheckState.Absent);
        AssertFragment(message, Unloadable, state is SelfCheckState.PresentButUnloadable);
        AssertFragment(message, ArchitectureMismatch, state is SelfCheckState.PresentButUnloadable);
        AssertFragment(message, SystemLibraries, state is SelfCheckState.PresentButUnloadable);
        AssertFragment(message, ExpectedSymbol, state is SelfCheckState.WrongVersion);
        AssertFragment(message, AbiMismatch, state is SelfCheckState.WrongVersion);
    }

    // The RID and the expected file name are written out literally by the caller, never taken from
    // the helpers under test, or the clause restates the code it is meant to pin.
    internal static void AssertPlatformDetail(string message, string runtimeIdentifier, string fileName)
    {
        Assert.Contains(runtimeIdentifier, message, StringComparison.Ordinal);
        Assert.Contains(fileName, message, StringComparison.Ordinal);
    }

    static void AssertFragment(string message, string token, bool expected)
    {
        if (expected)
            Assert.Contains(token, message, StringComparison.Ordinal);
        else
            Assert.DoesNotContain(token, message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The failure fixtures shared by every clause that inspects a reported message. Kept in one place
/// so the thrown text, the logged text and the writer text are all held to the same clauses.
/// </summary>
internal static class SelfCheckCases
{
    internal static readonly string[] Searched =
    [
        "/tmp/rgb-probe-one",
        "/tmp/rgb-probe-two",
        "/tmp/rgb-probe-three"
    ];

    // Deliberately not a member of Searched: with the realistic winningPath the clause naming the
    // loaded library is implied by the searched list and asserts nothing.
    internal const string LoadedElsewhere = "/tmp/rgb-loaded-from-somewhere-else";

    internal static readonly Exception Fault = new InvalidOperationException("the probe blew up");

    // Written out literally rather than taken from the helpers under test.
    internal static string HostRuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;

    internal static string HostFileName => OperatingSystem.IsWindows() ? "rgbverifycffi.dll"
        : OperatingSystem.IsMacOS() ? "librgbverifycffi.dylib"
        : "librgbverifycffi.so";

    internal sealed record FailureCase(
        NativeProbe Probe,
        Func<IntPtr, string, bool> HasExport,
        RecordingExports? Exports,
        string?[] Details);

    internal static FailureCase Build(SelfCheckState state, bool exportCheckThrows = false)
    {
        switch (state)
        {
            case SelfCheckState.Absent:
            {
                var exports = new RecordingExports(SelfCheckTokens.RequiredExports);
                return new FailureCase(SelfCheckProbes.Failing(Searched, Array.Empty<string>()),
                    exports.Has, exports, Searched);
            }
            case SelfCheckState.PresentButUnloadable:
            {
                var exports = new RecordingExports(SelfCheckTokens.RequiredExports);
                return new FailureCase(SelfCheckProbes.Failing(Searched, new[] { Searched[1] }),
                    exports.Has, exports, Searched);
            }
            case SelfCheckState.WrongVersion:
            {
                var exports = new RecordingExports(
                    "rgbverify_decode_invoice", "rgbverify_validate", "rgbverify_commitment_check");
                return new FailureCase(
                    SelfCheckProbes.Loading((IntPtr)7, LoadedElsewhere, Searched, Array.Empty<string>()),
                    exports.Has, exports, ["rgbverify_validate_v2", LoadedElsewhere]);
            }
            case SelfCheckState.SelfCheckFailed when exportCheckThrows:
                // State 5's second trigger: the load succeeded and the export check faulted, so
                // searched and winningPath are assigned but the diagnosis still is not.
                return new FailureCase(
                    SelfCheckProbes.Loading((IntPtr)7, LoadedElsewhere, Searched, Array.Empty<string>()),
                    new ThrowingExports(Fault).Has, null, [Fault.GetType().FullName]);
            case SelfCheckState.SelfCheckFailed:
                return new FailureCase(SelfCheckProbes.Throwing(Fault),
                    new RecordingExports(SelfCheckTokens.RequiredExports).Has, null, [Fault.GetType().FullName]);
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "not a failure state");
        }
    }

    internal static void AssertReported(string text, SelfCheckState state, FailureCase reported) =>
        AssertReported(text, state, reported.Details);

    internal static void AssertReported(string text, SelfCheckState state, string?[] details)
    {
        SelfCheckTokens.AssertEveryStateClauses(text);
        SelfCheckTokens.AssertStateTokens(text, state);
        SelfCheckTokens.AssertPlatformDetail(text, HostRuntimeIdentifier, HostFileName);
        SelfCheckTokens.AssertConsequencePrecedesDetails(text, details);

        foreach (var detail in details)
            Assert.Contains(detail!, text, StringComparison.Ordinal);

        // The token table holds only fixed strings, so the variable content has to be asserted
        // separately or a bare one-line summary satisfies every clause.
        if (state != SelfCheckState.SelfCheckFailed)
            Assert.All(Searched, path => Assert.Contains(path, text, StringComparison.Ordinal));

        // Clause (d) is only a real assertion while the fixture's own paths carry neither value.
        Assert.All(Searched, path => Assert.DoesNotContain(HostRuntimeIdentifier, path, StringComparison.Ordinal));
        Assert.All(Searched, path => Assert.DoesNotContain(HostFileName, path, StringComparison.Ordinal));
    }

    // Attribution in state 2: the failure must be pinned on the path that actually failed. Mere
    // presence is tautological, since existedButFailed is a subset of searched.
    internal static void AssertUnloadableAttribution(string text)
    {
        var token = text.IndexOf(SelfCheckTokens.Unloadable, StringComparison.Ordinal);
        Assert.True(token >= 0, "the present-but-unloadable token is missing");
        Assert.True(text.LastIndexOf(Searched[1], StringComparison.Ordinal) > token,
            "the path that failed to load must be named after the 'exists but could not be loaded' token");
        foreach (var other in new[] { Searched[0], Searched[2] })
        {
            Assert.True(text.LastIndexOf(other, StringComparison.Ordinal) < token,
                $"{other} was merely searched and must not be attributed as unloadable");
        }
    }
}

internal static class SelfCheckProbes
{
    internal static NativeProbe Failing(IReadOnlyList<string> searched, IReadOnlyList<string> existedButFailed) =>
        (out IntPtr handle, out string? winningPath,
         out IReadOnlyList<string> reportedSearched, out IReadOnlyList<string> reportedFailed) =>
        {
            handle = IntPtr.Zero;
            winningPath = null;
            reportedSearched = searched;
            reportedFailed = existedButFailed;
            return false;
        };

    internal static NativeProbe Loading(IntPtr handle, string winningPath,
        IReadOnlyList<string> searched, IReadOnlyList<string> existedButFailed) =>
        (out IntPtr reportedHandle, out string? reportedWinningPath,
         out IReadOnlyList<string> reportedSearched, out IReadOnlyList<string> reportedFailed) =>
        {
            reportedHandle = handle;
            reportedWinningPath = winningPath;
            reportedSearched = searched;
            reportedFailed = existedButFailed;
            return true;
        };

    internal static NativeProbe Throwing(Exception fault) =>
        (out IntPtr handle, out string? winningPath,
         out IReadOnlyList<string> searched, out IReadOnlyList<string> existedButFailed) => throw fault;
}

internal sealed class RecordingExports(params string[] present)
{
    readonly HashSet<string> _present = new(present, StringComparer.Ordinal);

    internal List<string> Queried { get; } = [];

    internal bool Has(IntPtr handle, string name)
    {
        Queried.Add(name);
        return _present.Contains(name);
    }
}

internal sealed class ThrowingExports(Exception fault)
{
    internal bool Has(IntPtr handle, string name) => throw fault;
}

internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    internal List<(LogLevel Level, string Message)> Entries { get; } = [];

    internal IEnumerable<string> Messages => Entries.Select(entry => entry.Message);

    public ILogger CreateLogger(string categoryName) => new Recording(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    sealed class Recording(RecordingLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => owner.Entries.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class ThrowingLoggerFactory(bool throwOnCreate) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) =>
        throwOnCreate
            ? throw new InvalidOperationException("logger factory is broken")
            : new ThrowingLogger();

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("logger is broken");
    }
}

internal sealed class ThrowingTextWriter : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public override void Write(char value) => throw new IOException("sink is broken");

    public override void Write(string? value) => throw new IOException("sink is broken");

    public override void WriteLine(string? value) => throw new IOException("sink is broken");
}

internal sealed class ThrowingServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => throw new InvalidOperationException("provider is broken");
}

internal sealed class FactoryServiceProvider(ILoggerFactory factory) : IServiceProvider
{
    public object? GetService(Type serviceType) =>
        serviceType == typeof(ILoggerFactory) ? factory : null;
}

internal static class StagedNative
{
    internal static string GatePath => Path.Combine(AppContext.BaseDirectory, "runtimes",
        RuntimeInformation.RuntimeIdentifier, "native", RgbVerifyNative.NativeFileName());

    internal static string RgbLibPath => Path.Combine(AppContext.BaseDirectory, "runtimes",
        RuntimeInformation.RuntimeIdentifier, "native",
        OperatingSystem.IsWindows() ? "rgblibcffi.dll"
            : OperatingSystem.IsMacOS() ? "librgblibcffi.dylib"
            : "librgblibcffi.so");

    // A precondition, enforced in the test body: unstaged, the assertions these tests make pass
    // vacuously, which is the silent-green failure the CI staging step exists to prevent.
    internal static void Require()
        => Assert.True(File.Exists(GatePath), $"unverified: gate native not staged at {GatePath}");

    internal static void RequireRgbLib()
        => Assert.True(File.Exists(RgbLibPath), $"unverified: rgblibcffi not staged at {RgbLibPath}");
}
