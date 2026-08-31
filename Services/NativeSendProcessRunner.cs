using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed record NativeSendLimits(
    TimeSpan Timeout,
    long RamCapBytes,
    TimeSpan CpuLimit,
    TimeSpan Poll,
    TimeSpan ReapGrace,
    int OutputCapChars = 1_048_576);

public enum NativeSendOutcome { Exited, TimedOut, KilledRam }

public sealed record NativeSendRunResult(
    NativeSendOutcome Outcome,
    int? ExitCode,
    string StdOut,
    string StdErr,
    bool ChildReaped,
    TimeSpan Elapsed,
    string? HelperDllHandedToTheDotnetHost = null);

public interface INativeSendProcessRunner
{
    Task<NativeSendRunResult> RunAsync(
        string operation, string requestJson, string leaseWalletDir, Func<bool> quiesceParent,
        NativeSendLimits limits, CancellationToken ct);
}

public sealed class NativeSendProcessRunner : INativeSendProcessRunner
{
    internal const int MinOutputCapChars = 1_024;
    internal const int MaxOutputCapChars = 8 * 1_048_576;

    readonly ILogger<NativeSendProcessRunner> _log;
    readonly Func<ProcessStartInfo, int, IChildHandle> _handleFactory;
    readonly Func<string> _resolveHelperDll;
    readonly Func<string> _resolveDotnetHost;

    public NativeSendProcessRunner(ILogger<NativeSendProcessRunner> log,
        Func<ProcessStartInfo, int, IChildHandle>? handleFactory = null,
        Func<string>? resolveHelperDll = null,
        Func<string>? resolveDotnetHost = null)
    {
        _log = log;
        _handleFactory = handleFactory ?? CreateRealChild;
        _resolveHelperDll = resolveHelperDll ?? (() => Path.Combine(
            Path.GetDirectoryName(typeof(NativeSendProcessRunner).Assembly.Location)!,
            "RgbRestoreHelper.dll"));
        _resolveDotnetHost = resolveDotnetHost ?? (() => RestoreProcessRunner.ResolveDotnetHost(
            Environment.ProcessPath,
            RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            File.Exists,
            OperatingSystem.IsWindows()));
    }

    public async Task<NativeSendRunResult> RunAsync(
        string operation, string requestJson, string leaseWalletDir, Func<bool> quiesceParent,
        NativeSendLimits limits, CancellationToken ct)
    {
        if (operation is not ("send-begin" or "send-end"))
            throw new ArgumentOutOfRangeException(nameof(operation));
        var helperDll = _resolveHelperDll();
        if (!File.Exists(helperDll))
            throw new InvalidOperationException($"Native send helper not found at {helperDll}");
        if (!RgbNativeSendLease.Exists(leaseWalletDir)
            || !RgbNativeSendLease.IsOwnedByCurrentContext(leaseWalletDir))
            throw new InvalidOperationException("Native send requires an operation-wide wallet lease");

        var psi = BuildStartInfo(helperDll, operation, limits);
        var sw = Stopwatch.StartNew();
        // The caller publishes and retains the operation marker across send_begin, signing, and
        // send_end. Quiescing after that publication makes every other process refuse wallet access.
        if (!quiesceParent())
            throw new RgbWalletQuarantinedException(
                "cached native wallet could not be quiesced before helper launch");

        IChildHandle child;
        var outputCapChars = Math.Clamp(limits.OutputCapChars, MinOutputCapChars, MaxOutputCapChars);
        try { child = _handleFactory(psi, outputCapChars); }
        catch (NativeSendChildUnreapedException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("Failed to launch native send helper", ex); }

        using (child)
        {
            try
            {
                var outcome = NativeSendOutcome.TimedOut;
                var killed = false;
                var inputTask = child.WriteStdinLineAndCloseAsync(requestJson);
                try
                {
                    var remainingForInput = limits.Timeout - sw.Elapsed;
                    if (remainingForInput <= TimeSpan.Zero) killed = true;
                    else await inputTask.WaitAsync(remainingForInput, ct);
                }
                catch (OperationCanceledException) { killed = true; }
                catch (TimeoutException) { killed = true; }
                while (!child.HasExited)
                {
                    if (killed) break;
                    if (sw.Elapsed >= limits.Timeout) { killed = true; break; }
                    try
                    {
                        if (child.WorkingSet64 > limits.RamCapBytes)
                        {
                            outcome = NativeSendOutcome.KilledRam;
                            killed = true;
                            break;
                        }
                    }
                    catch { }

                    var remaining = limits.Timeout - sw.Elapsed;
                    if (remaining <= TimeSpan.Zero) { killed = true; break; }
                    try { await Task.Delay(remaining < limits.Poll ? remaining : limits.Poll, ct); }
                    catch (OperationCanceledException) { killed = true; break; }
                }

                if (!killed)
                {
                    var reaped = await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None);
                    var stdOut = await child.ReadStdOutAsync();
                    var stdErr = await child.ReadStdErrAsync();
                    // A truncated prefix of a PSBT or a txid is not a smaller value, it is a wrong
                    // one, and returning it would let a completed send_end read as a failed send.
                    if (child.StdOutTruncated)
                        throw new NativeSendOutputTruncatedException(operation, outputCapChars);
                    return new NativeSendRunResult(
                        NativeSendOutcome.Exited,
                        child.ExitCode,
                        stdOut,
                        stdErr,
                        reaped,
                        sw.Elapsed,
                        helperDll);
                }

                child.Kill(entireProcessTree: true);
                var childReaped = await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None);
                try { await inputTask.WaitAsync(limits.ReapGrace, CancellationToken.None); } catch { }
                if (!childReaped)
                    _log.LogCritical("Native RGB send child could not be confirmed reaped");
                return new NativeSendRunResult(outcome, null, "", "", childReaped, sw.Elapsed, helperDll);
            }
            catch (NativeSendChildUnreapedException) { throw; }
            catch
            {
                // Once Process.Start succeeds, every unexpected supervision failure must still prove
                // process exit. Dispose alone only performs a best-effort kill and cannot establish that.
                child.Kill(entireProcessTree: true);
                if (!await child.WaitForExitAsync(limits.ReapGrace, CancellationToken.None))
                    throw new NativeSendChildUnreapedException();
                throw;
            }
        }
    }

    // Named rather than inlined so the production default is reachable by a test. Every other
    // observation of the cap goes through an injected factory, so an edit that put a literal back
    // here would leave the whole suite green with the knob dead again.
    internal static IChildHandle CreateRealChild(ProcessStartInfo psi, int outputCapChars)
        => new RestoreProcessRunner.RealChildHandle(psi, outputCapChars);

    ProcessStartInfo BuildStartInfo(string helperDll, string operation, NativeSendLimits limits)
    {
        var dotnet = _resolveDotnetHost();
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var prlimit = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? RestoreProcessRunner.ResolvePrlimitPath()
            : null;
        if (prlimit != null)
        {
            psi.FileName = prlimit;
            psi.ArgumentList.Add($"--cpu={(int)limits.CpuLimit.TotalSeconds}");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(dotnet);
        }
        else
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _log.LogWarning("prlimit unavailable; native send CPU is bounded by the wall-clock kill");
            psi.FileName = dotnet;
        }
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(helperDll);
        psi.ArgumentList.Add(operation);
        psi.ArgumentList.Add(Math.Clamp((int)Math.Ceiling(limits.Timeout.TotalMilliseconds), 100, 600_000).ToString());
        psi.ArgumentList.Add(limits.RamCapBytes.ToString());
        psi.ArgumentList.Add(Math.Clamp((int)Math.Ceiling(limits.CpuLimit.TotalSeconds), 1, 600).ToString());
        return psi;
    }
}
