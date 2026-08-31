using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RgbNativeConsoleErrorCollection
{
    public const string Name = "RgbNativeSelfCheck.ConsoleError";
}

/// <summary>
/// Every case that leaves the convenience overloads' sink at its default and therefore writes to the
/// process-global Console.Error. They are serialized: measured, under xunit's default per-collection
/// parallelism two such cases race and fail both ways — including an "emits nothing" assertion
/// passing vacuously because another test had already redirected the stream. The rule is the
/// invariant, not this list: any later case that omits the sink argument belongs here.
/// </summary>
[Collection(RgbNativeConsoleErrorCollection.Name)]
public class RgbNativeSelfCheckDefaultSinkTests
{
    // Substituting TextWriter.Null passes every other test while production
    // VerifyOrLog(ctx.BootstrapServices) silently loses the writer half — the only surviving sink
    // when the host hands back a discarding logger.
    [Fact]
    public void ConvenienceOverload_DefaultsTheSinkToConsoleError()
    {
        var reported = SelfCheckCases.Build(SelfCheckState.Absent);

        var captured = CaptureConsoleError(() =>
            Assert.False(RgbNativeSelfCheck.VerifyOrLog(null, reported.Probe, reported.HasExport)));

        SelfCheckCases.AssertReported(captured, SelfCheckState.Absent, reported);
    }

    // The 4-arg overload receives an already-resolved factory, so it cannot cover a resolution
    // failure at all. A throwing provider must not escape plugin startup.
    [Fact]
    public void ConvenienceOverload_ThrowingServiceProvider_ReturnsFalseWithoutPropagating()
    {
        var reported = SelfCheckCases.Build(SelfCheckState.Absent);

        var captured = CaptureConsoleError(() =>
            Assert.False(RgbNativeSelfCheck.VerifyOrLog(new ThrowingServiceProvider(),
                reported.Probe, reported.HasExport)));

        SelfCheckCases.AssertReported(captured, SelfCheckState.Absent, reported);
    }

    // Nothing else pins that the convenience overloads are bound to the real helpers: every other
    // test injects both, and the call-site clause only parses source. With the native staged, a
    // default probe or default export check rewired to a constant false turns this red.
    [Fact]
    public void DefaultBindings_OnAHealthyHost_ReportNothing()
    {
        StagedNative.Require();

        var captured = CaptureConsoleError(() =>
            Assert.True(RgbNativeSelfCheck.VerifyOrLog(null)));

        Assert.Empty(captured);
    }

    // The only case that catches a default hasExport of constant true: a real handle that genuinely
    // lacks the four gate exports. The healthy-host case above is green either way.
    [Fact]
    public void DefaultExportCheck_AgainstALibraryWithoutTheGateExports_ReportsWrongVersion()
    {
        StagedNative.RequireRgbLib();

        Assert.True(NativeLibrary.TryLoad(StagedNative.RgbLibPath, out var foreignHandle));

        var captured = CaptureConsoleError(() =>
            Assert.False(RgbNativeSelfCheck.VerifyOrLog(null,
                SelfCheckProbes.Loading(foreignHandle, StagedNative.RgbLibPath,
                    new[] { StagedNative.RgbLibPath }, Array.Empty<string>()))));

        SelfCheckTokens.AssertEveryStateClauses(captured);
        SelfCheckTokens.AssertStateTokens(captured, SelfCheckState.WrongVersion);
        Assert.Contains(SelfCheckTokens.RequiredExports[0], captured, StringComparison.Ordinal);
        Assert.Contains(StagedNative.RgbLibPath, captured, StringComparison.Ordinal);
    }

    // Replacing the factory resolution with null keeps signatures, call sites and return values
    // intact and leaves every other test green, while deleting the ILogger half of "emit to both,
    // always" in production — the audit clause's primary sink. Both overloads resolve it
    // independently, and Verify is what a later phase ships as the hard-fail entry point.
    [Fact]
    public void ConvenienceOverloads_ResolveTheLoggerFactoryFromTheProvider()
    {
        var logOnlyFactory = new RecordingLoggerFactory();
        var logOnly = SelfCheckCases.Build(SelfCheckState.Absent);

        CaptureConsoleError(() =>
            Assert.False(RgbNativeSelfCheck.VerifyOrLog(new FactoryServiceProvider(logOnlyFactory),
                logOnly.Probe, logOnly.HasExport)));

        SelfCheckCases.AssertReported(Assert.Single(logOnlyFactory.Entries).Message,
            SelfCheckState.Absent, logOnly);

        var hardFailFactory = new RecordingLoggerFactory();
        var hardFail = SelfCheckCases.Build(SelfCheckState.Absent);

        CaptureConsoleError(() =>
            Assert.Throws<RgbNativeUnavailableException>(() =>
                RgbNativeSelfCheck.Verify(new FactoryServiceProvider(hardFailFactory),
                    hardFail.Probe, hardFail.HasExport)));

        SelfCheckCases.AssertReported(Assert.Single(hardFailFactory.Entries).Message,
            SelfCheckState.Absent, hardFail);
    }

    static string CaptureConsoleError(Action act)
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            act();
        }
        finally
        {
            Console.SetError(original);
        }
        return captured.ToString();
    }
}
