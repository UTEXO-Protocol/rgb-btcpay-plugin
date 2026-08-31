using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSelfCheckTests
{
    public static TheoryData<SelfCheckState, bool> ReportedStates => new()
    {
        { SelfCheckState.Absent, false },
        { SelfCheckState.PresentButUnloadable, false },
        { SelfCheckState.WrongVersion, false },
    };

    public static TheoryData<SelfCheckState, bool> EveryFailureState => new()
    {
        { SelfCheckState.Absent, false },
        { SelfCheckState.PresentButUnloadable, false },
        { SelfCheckState.WrongVersion, false },
        { SelfCheckState.SelfCheckFailed, false },
        { SelfCheckState.SelfCheckFailed, true },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfCheck_LoadsAndResolvesAllFiveExports_DoesNotThrow(bool withEarlierLoadFailure)
    {
        // State 4: an earlier candidate that existed but would not load is informational only. An
        // operator on a working install must not be shown unloadable-native text.
        var existedButFailed = withEarlierLoadFailure
            ? new[] { SelfCheckCases.Searched[0] }
            : Array.Empty<string>();

        var exports = new RecordingExports(SelfCheckTokens.RequiredExports);
        var logger = new RecordingLoggerFactory();
        using var writer = new StringWriter();

        RgbNativeSelfCheck.Verify(logger, writer,
            SelfCheckProbes.Loading((IntPtr)7, SelfCheckCases.LoadedElsewhere, SelfCheckCases.Searched, existedButFailed),
            exports.Has);

        AssertQueriedEveryExport(exports);
        Assert.Empty(writer.ToString());
        Assert.Empty(logger.Entries);

        var logOnlyExports = new RecordingExports(SelfCheckTokens.RequiredExports);
        var logOnlyLogger = new RecordingLoggerFactory();
        using var logOnlyWriter = new StringWriter();

        var healthy = RgbNativeSelfCheck.VerifyOrLog(logOnlyLogger, logOnlyWriter,
            SelfCheckProbes.Loading((IntPtr)7, SelfCheckCases.LoadedElsewhere, SelfCheckCases.Searched, existedButFailed),
            logOnlyExports.Has);

        Assert.True(healthy);
        AssertQueriedEveryExport(logOnlyExports);
        Assert.Empty(logOnlyWriter.ToString());
        Assert.Empty(logOnlyLogger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfCheck_ProbeReturnsFalse_ThrowsWithActionableMessage(bool presentButUnloadable)
    {
        var state = presentButUnloadable ? SelfCheckState.PresentButUnloadable : SelfCheckState.Absent;
        var reported = SelfCheckCases.Build(state);
        using var writer = new StringWriter();

        var thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(null, writer, reported.Probe, reported.HasExport));

        // A load failure must never reach the export check: TryGetExport on a zero handle throws,
        // which would turn states 1-2 into state 5 in production while every fake stays green.
        Assert.Empty(reported.Exports!.Queried);

        SelfCheckCases.AssertReported(thrown.Message, state, reported);

        if (presentButUnloadable)
            SelfCheckCases.AssertUnloadableAttribution(thrown.Message);
    }

    [Theory]
    [InlineData("rgbverify_decode_invoice", false)]
    [InlineData("rgbverify_validate", false)]
    [InlineData("rgbverify_commitment_check", false)]
    [InlineData("rgbverify_validate_v2", false)]
    [InlineData("rgbverify_string_free", false)]
    [InlineData("rgbverify_string_free", true)]
    public void SelfCheck_MissingExport_ThrowsNamingTheSymbol(string missing, bool withEarlierLoadFailure)
    {
        // Testing existedButFailed before the exports emits the unloadable message for an
        // ABI-drifted native — the misdiagnosis the third branch exists to prevent.
        var existedButFailed = withEarlierLoadFailure
            ? new[] { SelfCheckCases.Searched[0] }
            : Array.Empty<string>();

        var exports = new RecordingExports(
            SelfCheckTokens.RequiredExports.Where(name => name != missing).ToArray());
        using var writer = new StringWriter();

        var thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(null, writer,
                SelfCheckProbes.Loading((IntPtr)7, SelfCheckCases.LoadedElsewhere, SelfCheckCases.Searched, existedButFailed),
                exports.Has));

        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.WrongVersion,
            [missing, SelfCheckCases.LoadedElsewhere]);
        Assert.DoesNotContain(SelfCheckCases.LoadedElsewhere, SelfCheckCases.Searched);
    }

    [Theory]
    [MemberData(nameof(ReportedStates))]
    public void Verify_FailingProbe_LogsToBothSinksThenThrows(SelfCheckState state, bool exportCheckThrows)
    {
        var reported = SelfCheckCases.Build(state, exportCheckThrows);
        var logger = new RecordingLoggerFactory();
        using var writer = new StringWriter();

        var thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(logger, writer, reported.Probe, reported.HasExport));

        SelfCheckCases.AssertReported(thrown.Message, state, reported);

        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        SelfCheckCases.AssertReported(logged.Message, state, reported);
        SelfCheckCases.AssertReported(writer.ToString(), state, reported);
    }

    // The hard-fail entry point must surface its own actionable exception whichever reporting
    // dependency fails, and must still deliver the message to the sink that is healthy. An
    // unguarded inline report surfaces IOException or InvalidOperationException instead and loses
    // the diagnostic at the exact moment a later phase auto-disables the plugin.
    [Fact]
    public void Verify_FaultingReportingDependency_StillThrowsTheActionableException()
    {
        using var brokenCreateLogger = new StringWriter();
        var viaBrokenFactory = SelfCheckCases.Build(SelfCheckState.Absent);
        var thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(new ThrowingLoggerFactory(throwOnCreate: true), brokenCreateLogger,
                viaBrokenFactory.Probe, viaBrokenFactory.HasExport));
        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.Absent, viaBrokenFactory);
        SelfCheckCases.AssertReported(brokenCreateLogger.ToString(), SelfCheckState.Absent, viaBrokenFactory);

        using var brokenLog = new StringWriter();
        var viaBrokenLog = SelfCheckCases.Build(SelfCheckState.Absent);
        thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(new ThrowingLoggerFactory(throwOnCreate: false), brokenLog,
                viaBrokenLog.Probe, viaBrokenLog.HasExport));
        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.Absent, viaBrokenLog);
        SelfCheckCases.AssertReported(brokenLog.ToString(), SelfCheckState.Absent, viaBrokenLog);

        var loggerForBrokenSink = new RecordingLoggerFactory();
        var viaBrokenSink = SelfCheckCases.Build(SelfCheckState.Absent);
        thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(loggerForBrokenSink, new ThrowingTextWriter(),
                viaBrokenSink.Probe, viaBrokenSink.HasExport));
        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.Absent, viaBrokenSink);
        SelfCheckCases.AssertReported(Assert.Single(loggerForBrokenSink.Entries).Message,
            SelfCheckState.Absent, viaBrokenSink);

        // A throwing provider leaves no factory, so only the sink can carry the message — but the
        // thrown type must still be the actionable one, never the provider's exception.
        using var brokenProviderSink = new StringWriter();
        var viaBrokenProvider = SelfCheckCases.Build(SelfCheckState.Absent);
        thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(new ThrowingServiceProvider(), viaBrokenProvider.Probe,
                viaBrokenProvider.HasExport, brokenProviderSink));
        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.Absent, viaBrokenProvider);
        SelfCheckCases.AssertReported(brokenProviderSink.ToString(), SelfCheckState.Absent, viaBrokenProvider);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbeThrew_BothEntryPointsReportStateFive(bool exportCheckThrows)
    {
        var logOnly = SelfCheckCases.Build(SelfCheckState.SelfCheckFailed, exportCheckThrows);
        var logOnlyLogger = new RecordingLoggerFactory();
        using var logOnlyWriter = new StringWriter();

        Assert.False(RgbNativeSelfCheck.VerifyOrLog(logOnlyLogger, logOnlyWriter,
            logOnly.Probe, logOnly.HasExport));
        SelfCheckCases.AssertReported(Assert.Single(logOnlyLogger.Entries).Message,
            SelfCheckState.SelfCheckFailed, logOnly);
        SelfCheckCases.AssertReported(logOnlyWriter.ToString(), SelfCheckState.SelfCheckFailed, logOnly);

        var hardFail = SelfCheckCases.Build(SelfCheckState.SelfCheckFailed, exportCheckThrows);
        var hardFailLogger = new RecordingLoggerFactory();
        using var hardFailWriter = new StringWriter();

        var thrown = Assert.Throws<RgbNativeUnavailableException>(() =>
            RgbNativeSelfCheck.Verify(hardFailLogger, hardFailWriter, hardFail.Probe, hardFail.HasExport));

        // Wrapping preserves the fault while guaranteeing the diagnostic the audit clause demands in
        // every failure state — a state-5 throw that logged nothing would not satisfy it.
        Assert.Same(SelfCheckCases.Fault, thrown.InnerException);
        SelfCheckCases.AssertReported(thrown.Message, SelfCheckState.SelfCheckFailed, hardFail);
        SelfCheckCases.AssertReported(Assert.Single(hardFailLogger.Entries).Message,
            SelfCheckState.SelfCheckFailed, hardFail);
        SelfCheckCases.AssertReported(hardFailWriter.ToString(), SelfCheckState.SelfCheckFailed, hardFail);
    }

    [Theory]
    [MemberData(nameof(EveryFailureState))]
    public void VerifyOrLog_FailingProbe_ReportsToBothSinksAndReturnsFalse(SelfCheckState state, bool exportCheckThrows)
    {
        var reported = SelfCheckCases.Build(state, exportCheckThrows);
        var logger = new RecordingLoggerFactory();
        using var writer = new StringWriter();

        Assert.False(RgbNativeSelfCheck.VerifyOrLog(logger, writer, reported.Probe, reported.HasExport));

        var logged = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        SelfCheckCases.AssertReported(logged.Message, state, reported);

        // Written to the sink even though a non-null logger was supplied: writing only when the
        // logger is null passes a conditional test while the message vanishes into a NullLogger.
        SelfCheckCases.AssertReported(writer.ToString(), state, reported);

        if (state is SelfCheckState.Absent or SelfCheckState.PresentButUnloadable)
            Assert.Empty(reported.Exports!.Queried);

        if (state == SelfCheckState.PresentButUnloadable)
        {
            SelfCheckCases.AssertUnloadableAttribution(logged.Message);
            SelfCheckCases.AssertUnloadableAttribution(writer.ToString());
        }
    }

    // BTCPay does register a real factory on the plugin-load path, so GetService returning null is
    // essentially unreachable; the case that actually swallows the message is a non-null factory
    // handing back NullLogger.Instance.
    [Theory]
    [MemberData(nameof(EveryFailureState))]
    public void VerifyOrLog_DiscardingLogger_StillLeavesTheMessageInTheSink(SelfCheckState state, bool exportCheckThrows)
    {
        var reported = SelfCheckCases.Build(state, exportCheckThrows);
        using var writer = new StringWriter();

        Assert.False(RgbNativeSelfCheck.VerifyOrLog(NullLoggerFactory.Instance, writer,
            reported.Probe, reported.HasExport));

        SelfCheckCases.AssertReported(writer.ToString(), state, reported);
    }

    // Each of the three places reporting can throw — acquiring the logger, logging, and writing —
    // is separately guarded. One shared try satisfies "never throws" while emitting nothing.
    [Fact]
    public void VerifyOrLog_FaultingSinks_FailIndependently()
    {
        using var brokenCreateLogger = new StringWriter();
        var viaBrokenFactory = SelfCheckCases.Build(SelfCheckState.Absent);
        Assert.False(RgbNativeSelfCheck.VerifyOrLog(new ThrowingLoggerFactory(throwOnCreate: true),
            brokenCreateLogger, viaBrokenFactory.Probe, viaBrokenFactory.HasExport));
        SelfCheckCases.AssertReported(brokenCreateLogger.ToString(), SelfCheckState.Absent, viaBrokenFactory);

        using var brokenLog = new StringWriter();
        var viaBrokenLog = SelfCheckCases.Build(SelfCheckState.Absent);
        Assert.False(RgbNativeSelfCheck.VerifyOrLog(new ThrowingLoggerFactory(throwOnCreate: false),
            brokenLog, viaBrokenLog.Probe, viaBrokenLog.HasExport));
        SelfCheckCases.AssertReported(brokenLog.ToString(), SelfCheckState.Absent, viaBrokenLog);

        var logger = new RecordingLoggerFactory();
        var viaBrokenSink = SelfCheckCases.Build(SelfCheckState.Absent);
        Assert.False(RgbNativeSelfCheck.VerifyOrLog(logger, new ThrowingTextWriter(),
            viaBrokenSink.Probe, viaBrokenSink.HasExport));
        SelfCheckCases.AssertReported(Assert.Single(logger.Entries).Message,
            SelfCheckState.Absent, viaBrokenSink);
    }

    // The default helpers are exercised as themselves rather than through injected fakes: an
    // always-true export check passes every other test and state 3 never fires in production.
    [Fact]
    public void DefaultHasExport_AnswersFromTheRealLibrary()
    {
        StagedNative.Require();

        Assert.True(RgbVerifyNative.TryLoadFromCandidates(
            RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly),
            out var handle, out _, out _, out _));

        Assert.True(RgbNativeSelfCheck.DefaultHasExport(handle, "rgbverify_decode_invoice"));
        Assert.False(RgbNativeSelfCheck.DefaultHasExport(handle, "rgbverify_not_a_real_symbol"));
    }

    // Agreement with the helper it forwards to also catches a swap of the searched/existedButFailed
    // out-arguments, which would make production report "exists but could not be loaded" for a
    // genuinely absent native — the exact misdiagnosis the branch exists to prevent.
    [Fact]
    public void DefaultProbe_AgreesWithTheSharedCandidateLoop()
    {
        StagedNative.Require();

        var probed = RgbNativeSelfCheck.DefaultProbe(out var probedHandle, out var probedWinningPath,
            out var probedSearched, out var probedFailed);

        var direct = RgbVerifyNative.TryLoadFromCandidates(
            RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly),
            out var directHandle, out var directWinningPath, out var directSearched, out var directFailed);

        Assert.Equal(direct, probed);
        Assert.Equal(directHandle, probedHandle);
        Assert.Equal(directWinningPath, probedWinningPath);
        Assert.Equal(directSearched, probedSearched);
        Assert.Equal(directFailed, probedFailed);
    }

    // Returning AppContext.BaseDirectory and ignoring the argument survives the call-site clause;
    // under the plugin host that is BTCPay's directory rather than the plugin's, which would send
    // the probe looking somewhere the real DllImport never searches.
    [Fact]
    public void ResolveBaseDir_HonoursTheAssemblyItIsGiven()
    {
        var plugin = RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly);
        var sharedFramework = RgbVerifyNative.ResolveBaseDir(typeof(object).Assembly);

        Assert.Equal(Path.GetDirectoryName(typeof(RgbVerifyNative).Assembly.Location), plugin);
        Assert.NotEqual(plugin, sharedFramework);
    }

    // T18 always injects a fake loader and DefaultProbe compares two callers that both take the
    // default, so a default that fabricates a handle or opens the wrong file would "agree" with
    // itself. Asserting through the returned handle is what rejects it.
    [Fact]
    public void TryLoadFromCandidates_DefaultLoader_ReturnsAUsableHandle()
    {
        StagedNative.Require();

        var loaded = RgbVerifyNative.TryLoadFromCandidates(
            RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly),
            out var handle, out var winningPath, out _, out _);

        Assert.True(loaded);
        Assert.NotNull(winningPath);
        Assert.True(File.Exists(winningPath));
        Assert.True(NativeLibrary.TryGetExport(handle, "rgbverify_decode_invoice", out _));
    }

    static void AssertQueriedEveryExport(RecordingExports exports) =>
        Assert.Equal(
            SelfCheckTokens.RequiredExports.OrderBy(name => name, StringComparer.Ordinal),
            exports.Queried.Distinct().OrderBy(name => name, StringComparer.Ordinal));
}
