using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;
using RgbLib;
using RgbRestoreHelper;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbRestoreHelperTests
{
    const long RestoreMemoryLimitBytes = 536_870_912;
    const int RestoreCpuLimitSeconds = 3_600;
    const string WatchdogTooLongToFireInsideAUnitTest = "3600000";

    static string[] RestoreArgs(
        string timeoutMs = WatchdogTooLongToFireInsideAUnitTest,
        string memoryLimitBytes = "536870912",
        string cpuLimitSeconds = "3600")
        => ["bk", "dir", timeoutMs, memoryLimitBytes, cpuLimitSeconds];

    sealed class LimiterStub : IDisposable
    {
        public readonly List<(long Memory, int Cpu)> Calls = new();
        public Action<long, int>? Behaviour;

        readonly Action<long, int> _restoreOnDispose;

        public LimiterStub()
        {
            _restoreOnDispose = Program.ApplyResourceLimits;
            Program.ApplyResourceLimits = (memory, cpu) =>
            {
                Calls.Add((memory, cpu));
                Behaviour?.Invoke(memory, cpu);
            };
        }

        public void Dispose() => Program.ApplyResourceLimits = _restoreOnDispose;
    }

    sealed class NativeStub : IDisposable
    {
        public int Invocations;

        public NativeStub(bool ok = true, string error = "")
        {
            RgbRestoreNative.NativeInvoke = (_, _, _) =>
            {
                Invocations++;
                return (ok, error);
            };
        }

        public void Dispose() => RgbRestoreNative.ResetNativeInvoke();
    }

    sealed class RecordingReader(string content) : TextReader
    {
        readonly StringReader _inner = new(content);
        public int Reads;

        public override string? ReadLine()
        {
            Reads++;
            return _inner.ReadLine();
        }

        public override string ReadToEnd()
        {
            Reads++;
            return _inner.ReadToEnd();
        }
    }

    [Fact]
    public void NativeSendAddressSpaceLimitAddsOnlyTheBoundedBudgetAndHonorsExistingHardLimit()
    {
        var type = typeof(Program).Assembly.GetType(
            "RgbRestoreHelper.NativeSendResourceLimiter", throwOnError: true)!;
        var compute = type.GetMethod("ComputeUnixAddressSpaceLimit",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        ulong Invoke(ulong current, ulong budget, ulong soft, ulong hard) =>
            (ulong)compute.Invoke(null, [current, budget, soft, hard])!;

        Assert.Equal<ulong>(600, Invoke(100, 500, ulong.MaxValue, ulong.MaxValue));
        Assert.Equal<ulong>(550, Invoke(100, 500, ulong.MaxValue, 550));
        Assert.Equal<ulong>(400, Invoke(100, 500, 400, ulong.MaxValue));
        Assert.Equal(ulong.MaxValue,
            Invoke(ulong.MaxValue - 10, 100, ulong.MaxValue, ulong.MaxValue));
    }

    [Fact]
    public void MissingArgs_ReturnsNonZero()
    {
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(new[] { "only-one-arg" }, stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void ClosedStdin_ReturnsNonZero_DoesNotHang()
    {
        using var limiter = new LimiterStub();
        using var stdin = new StringReader("");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(), stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void EmptyPasswordLine_ReturnsNonZero()
    {
        using var limiter = new LimiterStub();
        using var stdin = new StringReader("\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(), stdin, stderr);
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public void NativeSuccess_ReturnsZero()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(), stdin, stderr);
        Assert.Equal(0, rc);
    }

    [Fact]
    public void NativeFailure_ReturnsNonZero_WritesStderr()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub(ok: false, error: "boom");
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(), stdin, stderr);
        Assert.NotEqual(0, rc);
        Assert.Contains("boom", stderr.ToString());
    }

    [Fact]
    public void NativeFailure_DoesNotEchoPassword()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub(ok: false, error: "boom");
        using var stdin = new StringReader("SECRET-PW\n");
        using var stderr = new StringWriter();

        Program.Run(RestoreArgs(), stdin, stderr);
        Assert.DoesNotContain("SECRET-PW", stderr.ToString());
    }

    [Fact]
    public void RestoreAppliesTheRestoreBudgetsBeforeThePasswordIsRead()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new RecordingReader("pw\n");
        using var stderr = new StringWriter();
        var readsWhenLimitsApplied = -1;
        limiter.Behaviour = (_, _) => readsWhenLimitsApplied = stdin.Reads;

        var rc = Program.Run(RestoreArgs(), stdin, stderr);

        Assert.Equal(0, rc);
        Assert.True(limiter.Calls.Count == 1,
            $"the restore child must apply exactly one hard resource limit, applied {limiter.Calls.Count}: "
            + "without it an orphaned restore has no RAM bound and no CPU bound on any platform");
        Assert.Equal((RestoreMemoryLimitBytes, RestoreCpuLimitSeconds), limiter.Calls[0]);
        Assert.True(readsWhenLimitsApplied == 0,
            "the resource limits must be applied BEFORE the password is read, so that no "
            + "attacker-supplied archive is ever touched by an unbounded process; reads already seen "
            + $"when the limits were applied: {readsWhenLimitsApplied}");
    }

    [Theory]
    [InlineData("99")]
    [InlineData("3600001")]
    [InlineData("nonsense")]
    public void RestoreWithATimeoutOutsideTheAcceptedRangeIsRefusedBeforeAnyNativeWork(string timeoutMs)
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(timeoutMs: timeoutMs), stdin, stderr);

        Assert.NotEqual(0, rc);
        Assert.Equal(0, native.Invocations);
        Assert.Empty(limiter.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("nonsense")]
    public void RestoreWithAnUnusableMemoryBudgetIsRefusedBeforeAnyNativeWork(string memoryLimitBytes)
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(memoryLimitBytes: memoryLimitBytes), stdin, stderr);

        Assert.NotEqual(0, rc);
        Assert.Equal(0, native.Invocations);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3601")]
    [InlineData("nonsense")]
    public void RestoreWithAnUnusableCpuBudgetIsRefusedBeforeAnyNativeWork(string cpuLimitSeconds)
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(RestoreArgs(cpuLimitSeconds: cpuLimitSeconds), stdin, stderr);

        Assert.NotEqual(0, rc);
        Assert.Equal(0, native.Invocations);
    }

    [Fact]
    public void RestoreWithNoLimitArgumentsAtAllIsRefusedBeforeAnyNativeWork()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();

        var rc = Program.Run(new[] { "bk", "dir" }, stdin, stderr);

        Assert.NotEqual(0, rc);
        Assert.True(native.Invocations == 0,
            "a restore invoked without its containment arguments must be refused, never run unbounded");
        Assert.Empty(limiter.Calls);
    }

    [Fact]
    public void RestoreIsRefusedWhenThePlatformRejectsTheBudget()
    {
        using var limiter = new LimiterStub();
        using var native = new NativeStub();
        using var stdin = new StringReader("pw\n");
        using var stderr = new StringWriter();
        limiter.Behaviour = (_, _) => throw new InvalidOperationException("no address-space budget");

        var rc = Program.Run(RestoreArgs(), stdin, stderr);

        Assert.NotEqual(0, rc);
        Assert.True(native.Invocations == 0,
            "a platform that refuses the restore budget must stop the restore, not run it unbounded");
        Assert.Contains("no address-space budget", stderr.ToString());
    }

    [Fact]
    public void TheResourceLimiterSeamDefaultsToTheRealPlatformLimiter()
    {
        var expected = typeof(Program).Assembly
            .GetType("RgbRestoreHelper.NativeSendResourceLimiter", throwOnError: true)!
            .GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.True(expected == Program.ApplyResourceLimits.Method,
            "with no test stub installed, the seam must still be the real platform limiter: every "
            + "other observation in this class would stay green while both the send child and the "
            + "restore child ran with no address-space and no CPU rlimit at all. Found "
            + $"{Program.ApplyResourceLimits.Method}");
    }

    [Fact]
    public void TheRealRestoreChildSelfTerminatesWhenItsParentNeverDeliversAPassword()
    {
        var helperDll = Path.Combine(
            Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "RgbRestoreHelper.dll");
        Assert.True(File.Exists(helperDll), $"restore helper not laid down beside the tests: {helperDll}");

        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(helperDll);
        psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"rgb-absent-backup-{Guid.NewGuid():N}"));
        psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), $"rgb-absent-staging-{Guid.NewGuid():N}"));
        psi.ArgumentList.Add("1500");
        psi.ArgumentList.Add(RestoreMemoryLimitBytes.ToString());
        psi.ArgumentList.Add(RestoreCpuLimitSeconds.ToString());

        var child = Process.Start(psi)!;
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            var exited = child.WaitForExit(30_000);
            var diagnostics = exited ? $"exit={child.ExitCode} stderr={stderr.Result} stdout={stdout.Result}" : "still running";
            Assert.True(exited,
                "the restore child must bound its own wall clock: with stdin held open forever it still "
                + "has to self-terminate, or a parent that dies mid-restore leaves an unbounded orphan "
                + "decrypting and inflating an attacker-supplied archive");
            Assert.True(child.ExitCode != 0, $"a self-terminated restore must not report success; {diagnostics}");
            Assert.Contains("restore helper self-timeout", stderr.Result);
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch { }
            child.Dispose();
        }
    }

    static string DotnetHost()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? RestoreProcessRunner.ResolveDotnetHost(
                Environment.ProcessPath,
                RuntimeEnvironment.GetRuntimeDirectory(),
                Environment.GetEnvironmentVariable("DOTNET_ROOT"),
                File.Exists,
                OperatingSystem.IsWindows());

    [Fact]
    public void TheRestoreResultTypeStillCarriesItsErrorBehindAnOpaqueInnerPointer()
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var native = assembly.GetType("RgbLib.NativeMethods", throwOnError: true)!;
        var cresult = native.GetMethod("rgblib_restore_backup")!.ReturnType;

        Assert.Equal("RgbLib.CResult", cresult.FullName);
        Assert.True(cresult.GetMethod("GetError") == null,
            "CResult has grown a GetError of its own — the reflective inner.ptr decode can be replaced "
            + "by it, but until then a GetError lookup on CResult is unconditionally null");
        var inner = cresult.GetField("inner")!;
        Assert.Equal(typeof(IntPtr), inner.FieldType.GetField("ptr")!.FieldType);
        Assert.Equal(typeof(ulong), inner.FieldType.GetField("ty")!.FieldType);
        Assert.NotNull(assembly.GetType("RgbLib.CResultString")!.GetMethod("GetError"));
    }

    struct FakeOpaqueStruct
    {
        public IntPtr ptr;
        public ulong ty;
    }

    struct FakeCResult
    {
        public string result;
        public FakeOpaqueStruct inner;
    }

    [Fact]
    public void TheRestoreErrorDetailIsDecodedFromTheOpaqueInnerPointerAndReleased()
    {
        var pointer = Marshal.StringToCoTaskMemUTF8("scrypt parameters rejected");
        try
        {
            var freed = new List<IntPtr>();
            var message = RgbRestoreNative.ReadCResultErrorStringFromOpaqueInnerPointer(
                new FakeCResult { result = "Err", inner = new FakeOpaqueStruct { ptr = pointer, ty = 0 } },
                freed.Add);

            Assert.Equal("scrypt parameters rejected", message);
            Assert.Equal(new[] { pointer }, freed);
        }
        finally { Marshal.ZeroFreeCoTaskMemUTF8(pointer); }
    }

    [Theory]
    [InlineData("Ok", 0UL, false)]
    [InlineData("Err", 7UL, false)]
    [InlineData("Err", 0UL, true)]
    public void TheRestoreErrorDetailIsOnlyDecodedWhenInnerReallyHoldsARawString(
        string discriminant, ulong opaqueType, bool decoded)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8("real detail");
        try
        {
            var message = RgbRestoreNative.ReadCResultErrorStringFromOpaqueInnerPointer(
                new FakeCResult
                {
                    result = discriminant,
                    inner = new FakeOpaqueStruct { ptr = pointer, ty = opaqueType }
                },
                _ => { });

            Assert.Equal(decoded ? "real detail" : RgbRestoreNative.UndiagnosedRestoreFailure, message);
        }
        finally { Marshal.ZeroFreeCoTaskMemUTF8(pointer); }
    }

    [Fact]
    public void ANullErrorPointerFallsBackToTheGenericRestoreFailure()
    {
        var message = RgbRestoreNative.ReadCResultErrorStringFromOpaqueInnerPointer(
            new FakeCResult { result = "Err", inner = new FakeOpaqueStruct { ptr = IntPtr.Zero, ty = 0 } },
            _ => throw new InvalidOperationException("nothing to free"));

        Assert.Equal(RgbRestoreNative.UndiagnosedRestoreFailure, message);
    }
}
