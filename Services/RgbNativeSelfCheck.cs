using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal delegate bool NativeProbe(out IntPtr handle,
                                   out string? winningPath,
                                   out IReadOnlyList<string> searched,
                                   out IReadOnlyList<string> existedButFailed);

internal sealed class RgbNativeUnavailableException : Exception
{
    internal RgbNativeUnavailableException(string message) : base(message) { }

    internal RgbNativeUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Startup self-check for the pre-sign gate's native trust core. Without it a missing or wrong
/// library is discovered one rejected send at a time; with it the operator is told once, loudly, at
/// startup — which of the four failure states occurred, where the library was looked for, and where
/// to report it.
/// </summary>
internal static class RgbNativeSelfCheck
{
    const string IssuesUrl = "https://github.com/UTEXO-Protocol/rgb-btcpay-plugin/issues";

    // Logs to both sinks and then throws for explicit callers that need a fatal check. Plugin startup
    // deliberately uses VerifyOrLog below so recovery features remain available while sends fail closed.
    internal static void Verify(ILoggerFactory? factory, TextWriter writer,
                                NativeProbe probe, Func<IntPtr, string, bool> hasExport)
    {
        var (message, fault) = Diagnose(probe, hasExport);
        if (message == null) return;

        // Reports before it throws in every failure state, state 5 included, so explicit fatal callers
        // receive our actionable diagnostic rather than only whatever their host logs for the exception.
        Report(factory, writer, message);

        throw fault == null
            ? new RgbNativeUnavailableException(message)
            : new RgbNativeUnavailableException(message, fault);
    }

    internal static void Verify(IServiceProvider? sp, NativeProbe? probe = null,
                                Func<IntPtr, string, bool>? hasExport = null, TextWriter? sink = null)
    {
        TextWriter writer = TextWriter.Null;
        try { writer = sink ?? Console.Error; } catch { }
        ILoggerFactory? factory = null;
        try { factory = sp?.GetService<ILoggerFactory>(); } catch { }
        Verify(factory, writer, probe ?? DefaultProbe, hasExport ?? DefaultHasExport);
    }

    // Catches every exception, reports to both sinks, returns false — the log-only entry point. A
    // typed-only catch would let an unexpected probe fault escape plugin startup and trigger
    // BTCPay's disable-and-restart cascade on every install in the fleet.
    internal static bool VerifyOrLog(ILoggerFactory? factory, TextWriter writer,
                                     NativeProbe probe, Func<IntPtr, string, bool> hasExport)
    {
        try
        {
            var (message, _) = Diagnose(probe, hasExport);
            if (message == null) return true;

            Report(factory, writer, message);
            return false;
        }
        catch
        {
            return false;
        }
    }

    internal static bool VerifyOrLog(IServiceProvider? sp, NativeProbe? probe = null,
                                     Func<IntPtr, string, bool>? hasExport = null, TextWriter? sink = null)
    {
        // Separate guards. Sharing one try lets a throwing provider abort before the sink is
        // assigned, so the diagnostic lands in TextWriter.Null — emitted nowhere at all.
        TextWriter writer = TextWriter.Null;
        try { writer = sink ?? Console.Error; } catch { }
        ILoggerFactory? factory = null;
        try { factory = sp?.GetService<ILoggerFactory>(); } catch { }
        return VerifyOrLog(factory, writer, probe ?? DefaultProbe, hasExport ?? DefaultHasExport);
    }

    // Declared as methods rather than static readonly fields: a field's initializer runs on first
    // touch of the class, before the method body and outside the guards above.
    internal static bool DefaultProbe(out IntPtr h, out string? winningPath,
                                      out IReadOnlyList<string> searched,
                                      out IReadOnlyList<string> existedButFailed)
        => RgbVerifyNative.TryLoadFromCandidates(
               RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly),
               out h, out winningPath, out searched, out existedButFailed);

    internal static bool DefaultHasExport(IntPtr h, string name)
        => NativeLibrary.TryGetExport(h, name, out _);

    internal static void RequireAvailable()
        => Verify(null, TextWriter.Null, DefaultProbe, DefaultHasExport);

    // The probe resolves the handle and requires the exports, but never calls one: every export
    // returns CResultString by value and the binding dereferences and frees that pointer, which
    // against an ABI-mismatched image can abort the process during plugin load.
    // Both sinks, always, each guarded on its own. Duplicated output in normal operation is a cheap
    // price for an audit-mandated error that cannot vanish into a NullLogger — which BTCPay does
    // hand out on some paths — or into a broken stderr. One shared try would satisfy "never throws"
    // while emitting nothing at all.
    static void Report(ILoggerFactory? factory, TextWriter writer, string message)
    {
        ILogger? logger = null;
        try { logger = factory?.CreateLogger(typeof(RgbNativeSelfCheck).FullName!); } catch { }

        // The message goes in as the single template argument so it survives into the rendered text
        // rather than living only in the structured state.
        try { logger?.LogError("{RgbNativeSelfCheck}", message); } catch { }

        try { writer.WriteLine(message); } catch { }
    }

    static (string? Message, Exception? Fault) Diagnose(NativeProbe probe, Func<IntPtr, string, bool> hasExport)
    {
        try
        {
            // The export check is never reached on a failed load: TryGetExport on a zero handle
            // throws, which would report every absent native as a self-check fault instead.
            if (!probe(out var handle, out var winningPath, out var searched, out var existedButFailed))
            {
                return (existedButFailed.Count == 0
                    ? AbsentMessage(searched)
                    : UnloadableMessage(searched, existedButFailed), null);
            }

            // existedButFailed is informational, never a discriminator: an unloadable first
            // candidate followed by a loadable second is a healthy install.
            foreach (var name in RequiredExports())
            {
                if (!hasExport(handle, name))
                    return (WrongVersionMessage(searched, winningPath, name), null);
            }

            return (null, null);
        }
        catch (Exception fault)
        {
            return (SelfCheckFailedMessage(fault), fault);
        }
    }

    static string[] RequiredExports() =>
    [
        "rgbverify_decode_invoice",
        "rgbverify_validate",
        "rgbverify_commitment_check",
        "rgbverify_validate_v2",
        "rgbverify_string_free"
    ];

    static string AbsentMessage(IReadOnlyList<string> searched) =>
        LoadFailedHeader()
        + PlatformDetail()
        + SearchedDetail(searched)
        + "No candidate path existed, so the library is absent from this build. That is a known packaging"
        + " defect in the plugin distribution, not a problem with how your server is set up."
        + Environment.NewLine
        + Remediation();

    static string UnloadableMessage(IReadOnlyList<string> searched, IReadOnlyList<string> existedButFailed) =>
        LoadFailedHeader()
        + PlatformDetail()
        + SearchedDetail(searched)
        + "At least one candidate file exists but could not be loaded:" + Environment.NewLine
        + Paths(existedButFailed)
        + "That points at an architecture mismatch, a corrupt file, or incompatible system libraries"
        + " — for example a glibc floor newer than this host." + Environment.NewLine
        + Remediation();

    static string WrongVersionMessage(IReadOnlyList<string> searched, string? winningPath, string missingSymbol) =>
        Header("The RGB pre-sign verification library loaded but is the wrong version.")
        + PlatformDetail()
        + SearchedDetail(searched)
        + $"Loaded library: {winningPath}" + Environment.NewLine
        + $"The expected symbol {missingSymbol} is missing from it, which is an ABI/version mismatch"
        + " between the plugin and the native library." + Environment.NewLine
        + Remediation();

    static string SelfCheckFailedMessage(Exception fault) =>
        Header("The RGB pre-sign verification self-check failed.")
        + PlatformDetail()
        + $"The check itself raised {fault.GetType().FullName}: {fault.Message}" + Environment.NewLine
        + Remediation();

    static string LoadFailedHeader() =>
        Header("The RGB pre-sign verification library could not be loaded.");

    // The consequence comes first and in plain words: an operator who reads nothing else must still
    // learn that sends stop and that receiving keeps working.
    static string Header(string opening) =>
        opening + Environment.NewLine
        + "All RGB asset sends will be rejected until this is fixed. "
        + "Receiving RGB assets and the rest of the plugin are unaffected." + Environment.NewLine;

    static string PlatformDetail() =>
        $"Runtime identifier: {RuntimeInformation.RuntimeIdentifier}" + Environment.NewLine
        + $"Expected file name: {RgbVerifyNative.NativeFileName()}" + Environment.NewLine;

    static string SearchedDetail(IReadOnlyList<string> searched) =>
        "Paths searched:" + Environment.NewLine + Paths(searched);

    static string Paths(IReadOnlyList<string> paths) =>
        string.Concat(paths.Select(path => $"  {path}{Environment.NewLine}"));

    // No claim about which platforms are supported: delivery is whatever the build staged, and a
    // wrong "your platform is not covered" line would send an operator down the wrong path. The RID
    // and the searched paths are what they need. Developer remediation comes last and names only
    // things that exist today.
    static string Remediation() =>
        $"Please report this at {IssuesUrl} and quote this message." + Environment.NewLine
        + "Developers: native/rgb-verify/build-native.sh builds and stages the library for the host"
        + " runtime identifier." + Environment.NewLine;
}
