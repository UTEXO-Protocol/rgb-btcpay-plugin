using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbVerifyNative
{
    const string Library = "rgbverifycffi";

    static RgbVerifyNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(RgbVerifyNative).Assembly, ResolveNative);
    }

    // The resolver is registered for the whole plugin assembly, which also declares six
    // [DllImport("rgblibcffi")] entries: without this guard first, rgb-lib's P/Invokes fall through
    // to the loop below and are handed an rgbverifycffi handle — every rgb-lib entry point
    // disappears and the whole wallet path breaks.
    internal static IntPtr ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library) return IntPtr.Zero;

        return TryLoadFromCandidates(ResolveBaseDir(assembly), out var handle, out _, out _, out _)
            ? handle
            : IntPtr.Zero;
    }

    // Takes the assembly the runtime hands the resolver rather than reading AppContext.BaseDirectory:
    // under BTCPay's plugin host those differ, and the latter is BTCPay's directory, not the plugin's.
    internal static string ResolveBaseDir(Assembly assembly)
    {
        var baseDir = Path.GetDirectoryName(assembly.Location);
        return string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : baseDir;
    }

    internal static string NativeFileName()
        => OperatingSystem.IsWindows() ? "rgbverifycffi.dll"
            : OperatingSystem.IsMacOS() ? "librgbverifycffi.dylib"
            : "librgbverifycffi.so";

    // Deduped: on .NET 8+ RuntimeInformation.RuntimeIdentifier already equals <os>-<arch> for every
    // RID we ship, so the two sources would otherwise emit the same candidate twice.
    internal static IEnumerable<string> CandidatePaths(string baseDir)
    {
        var fileName = NativeFileName();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rid in RuntimeIdentifiers())
        {
            var candidate = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
            if (seen.Add(candidate)) yield return candidate;
        }

        var flat = Path.Combine(baseDir, fileName);
        if (seen.Add(flat)) yield return flat;
    }

    // The startup self-check and the live DllImport resolution path share this loop, so the probe
    // searches exactly where the real P/Invoke will: parity is structural rather than an assumption
    // about runtime API semantics. It stops at the first success — loading every present candidate
    // would dlopen images nothing needs and widen the initializer-abort radius.
    //
    // existedButFailed is what lets the diagnostic tell a missing file (a packaging defect) apart
    // from a present but unloadable one (architecture mismatch, corruption, or a glibc floor newer
    // than the host) — a different problem with a different fix.
    //
    // load exists so the state and ordering tests are deterministic and cross-platform; production
    // never passes it. A lambda is not a legal parameter default, hence the null coalesce inside.
    internal static bool TryLoadFromCandidates(string baseDir, out IntPtr handle, out string? winningPath,
        out IReadOnlyList<string> searched, out IReadOnlyList<string> existedButFailed,
        Func<string, IntPtr>? load = null)
    {
        var loader = load ?? (path => NativeLibrary.TryLoad(path, out var loaded) ? loaded : IntPtr.Zero);

        var tried = new List<string>();
        var failed = new List<string>();
        searched = tried;
        existedButFailed = failed;
        handle = IntPtr.Zero;
        winningPath = null;

        foreach (var candidate in CandidatePaths(baseDir))
        {
            tried.Add(candidate);
            if (!File.Exists(candidate)) continue;

            var loaded = loader(candidate);
            if (loaded != IntPtr.Zero)
            {
                handle = loaded;
                winningPath = candidate;
                return true;
            }

            failed.Add(candidate);
        }

        return false;
    }

    internal static IEnumerable<string> RuntimeIdentifiers()
    {
        yield return RuntimeInformation.RuntimeIdentifier;
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };
        yield return $"{os}-{arch}";
    }

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_decode_invoice(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string invoice);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_validate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string consignmentPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string unsignedTxid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string indexerUrl,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string network,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string stockDir);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_commitment_check(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fasciaPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string unsignedTxid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string opretCommitmentBytes,
        ulong entropy);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern CResultString rgbverify_validate_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string requestJson);

    [DllImport("rgbverifycffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgbverify_string_free(IntPtr ptr);

    public static RgbDecodeInvoiceResult DecodeInvoice(string invoice)
        => Deserialize<RgbDecodeInvoiceResult>(Read(rgbverify_decode_invoice(invoice)), "decode_invoice");

    public static RgbValidateResult Validate(string consignmentPath, string unsignedTxid, string indexerUrl, string network, string stockDir)
        => Deserialize<RgbValidateResult>(Read(rgbverify_validate(consignmentPath, unsignedTxid, indexerUrl, network, stockDir)), "validate");

    public static RgbCommitmentCheckResult CommitmentCheck(string fasciaPath, string unsignedTxid, string opretCommitmentBytes, ulong entropy)
        => Deserialize<RgbCommitmentCheckResult>(Read(rgbverify_commitment_check(fasciaPath, unsignedTxid, opretCommitmentBytes, entropy)), "commitment_check");

    public static RgbValidateV2Result ValidateV2(RgbValidateV2Request request)
    {
        RgbNativeSelfCheck.RequireAvailable();
        var requestJson = JsonSerializer.Serialize(request);
        return Deserialize<RgbValidateV2Result>(Read(rgbverify_validate_v2(requestJson)), "validate_v2");
    }

    static string Read(CResultString result)
    {
        try
        {
            var payload = result.inner != IntPtr.Zero ? Marshal.PtrToStringUTF8(result.inner) : null;
            if (result.result != CResultValue.Ok)
                throw new RgbIntentVerificationException($"rgb-verify native call failed: {payload ?? "no detail"}");
            if (payload == null)
                throw new RgbIntentVerificationException("rgb-verify returned a null payload");
            return payload;
        }
        finally
        {
            if (result.inner != IntPtr.Zero)
                rgbverify_string_free(result.inner);
        }
    }

    static T Deserialize<T>(string json, string call)
        => JsonSerializer.Deserialize<T>(json)
           ?? throw new RgbIntentVerificationException($"rgb-verify {call} returned unparseable JSON");
}
