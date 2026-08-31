using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel.DataAnnotations;
using BTCPayServer.Plugins.RgbUtexo.Models;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// DisableParallelization is load-bearing twice over. xunit 2.9.3 honours Fact.Timeout only when
// parallelization is disabled globally or for the containing collection, and the per-endpoint-cap
// case depends on that timeout to fail fast instead of hanging CI for the stub's full delay.
// It also serializes the two process-wide statics these tests manipulate: the resolver seam, and
// TaskScheduler.UnobservedTaskException.
[CollectionDefinition(TransportEndpointValidatorCollection.Name, DisableParallelization = true)]
public sealed class TransportEndpointValidatorCollection
{
    public const string Name = "TransportEndpointValidator";
}

[Collection(TransportEndpointValidatorCollection.Name)]
public class TransportEndpointValidatorBoundsTests
{
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(TransportEndpointValidator.ValidationBudgetSeconds);
    static readonly TimeSpan Cap = TimeSpan.FromSeconds(TransportEndpointValidator.PerEndpointTimeoutSeconds);

    sealed class ResolverSwap : IDisposable
    {
        readonly Func<string, CancellationToken, Task<IPAddress[]>> _installed;

        public ResolverSwap(Func<string, CancellationToken, Task<IPAddress[]>> stub)
        {
            _installed = stub;
            TransportEndpointValidator.Resolver = stub;
        }

        // Restore only if this swap's stub is still installed. A timed-out xunit test's body keeps
        // running — measured, its Dispose fired ~1.7s after the timeout was reported, concurrently
        // with the next test in the same non-parallel collection. An unconditional restore would
        // then yank the seam out from under whichever test is running by then, and the victim would
        // be an unrelated case hitting real DNS for a name that does not resolve.
        public void Dispose()
        {
            if (ReferenceEquals(TransportEndpointValidator.Resolver, _installed))
                TransportEndpointValidator.ResetResolver();
        }
    }

    [Fact]
    public void SendModelUsesTheSameRawInvoiceBoundAsTheService()
    {
        var attribute = typeof(RGBSendAssetViewModel).GetProperty(nameof(RGBSendAssetViewModel.RgbInvoice))!
            .GetCustomAttributes(typeof(StringLengthAttribute), inherit: true)
            .Cast<StringLengthAttribute>()
            .Single();
        Assert.Equal(TransportEndpointValidator.MaxRgbInvoiceLength, attribute.MaximumLength);
    }

    static readonly IPAddress[] OnePublicAddress = [IPAddress.Parse("8.8.8.8")];

    // Hostnames, not literal IPs: the literal path never reaches the resolver, so a
    // "resolver was not invoked" assertion made with literal IPs holds even when the cap
    // is in the wrong place.
    static List<string> Hostnames(int n) =>
        Enumerable.Range(1, n).Select(i => $"rpc://h{i}.example:3000/json-rpc").ToList();

    static readonly string[] PublicIpLiterals =
    [
        "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1",
        "9.9.9.9", "149.112.112.112", "208.67.222.222", "208.67.220.220"
    ];

    static List<string> PublicIps(int n) =>
        PublicIpLiterals.Take(n).Select(ip => $"rpc://{ip}:3000/json-rpc").ToList();

    sealed class CountingResolver
    {
        public readonly List<string> Seen = [];
        readonly Func<string, Task<IPAddress[]>> _inner;

        public CountingResolver(Func<string, Task<IPAddress[]>>? inner = null) =>
            _inner = inner ?? (_ => Task.FromResult(OnePublicAddress));

        // The token is deliberately ignored: real getaddrinfo runs to completion regardless of it
        // (measured), and a cooperative stub would let a token-only implementation pass the
        // per-endpoint-cap test.
        public Task<IPAddress[]> Resolve(string host, CancellationToken _)
        {
            lock (Seen) Seen.Add(host);
            return _inner(host);
        }

        public int Count { get { lock (Seen) return Seen.Count; } }
    }

    [Fact]
    public async Task NineHostnameEndpoints_RejectedBeforeAnyResolution()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(
                Hostnames(TransportEndpointValidator.MaxTransportEndpoints + 1)));

        Assert.Equal(
            $"Too many transport endpoints: {TransportEndpointValidator.MaxTransportEndpoints + 1} " +
            $"(maximum {TransportEndpointValidator.MaxTransportEndpoints})",
            ex.Message);
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public async Task EightLiteralIpEndpoints_Accepted()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var result = await TransportEndpointValidator.ValidateAsync(
            PublicIps(TransportEndpointValidator.MaxTransportEndpoints));

        Assert.Equal(TransportEndpointValidator.MaxTransportEndpoints, result.Count);
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public async Task CountCap_AppliesWithAllowPrivateNetworks()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var oversized = Enumerable
            .Range(1, TransportEndpointValidator.MaxTransportEndpoints + 1)
            .Select(i => $"rpc://10.0.0.{i}:3000/json-rpc")
            .ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(oversized, allowPrivateNetworks: true));

        Assert.Contains("Too many transport endpoints", ex.Message);
        Assert.Equal(0, resolver.Count);
    }

    [Fact]
    public async Task EndpointLengthAndAggregateCapsRunBeforeResolution()
    {
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(resolver.Resolve);

        var oversized = "rpc://host.example/" + new string('x',
            TransportEndpointValidator.MaxTransportEndpointLength);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync([oversized]));

        var aggregate = Enumerable.Range(0, TransportEndpointValidator.MaxTransportEndpoints)
            .Select(i => $"rpc://h{i}.example/" + new string('x', 1_100))
            .ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(aggregate));

        Assert.Equal(0, resolver.Count);
    }

    [Fact(Timeout = 12000)]
    public async Task AbandonedDnsWorkNeverExceedsTheGlobalConcurrencyCap()
    {
        var resolver = new CountingResolver(_ => new TaskCompletionSource<IPAddress[]>().Task);
        using var swap = new ResolverSwap(resolver.Resolve);
        var attempts = Enumerable.Range(0, TransportEndpointValidator.MaxConcurrentResolvers + 1)
            .Select(i => TransportEndpointValidator.ValidateAsync(
                [$"rpc://limit-{i}.example:3000/json-rpc"]))
            .ToList();

        await Task.WhenAll(attempts.Select(async attempt =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => attempt)));

        Assert.Equal(TransportEndpointValidator.MaxConcurrentResolvers, resolver.Count);
    }

    // Must exceed the budget, since it is the only thing consuming it. The extra second covers
    // timer granularity on a loaded machine.
    static readonly TimeSpan OverBudgetBlock = Budget + TimeSpan.FromSeconds(1);

    // Two full resolutions must leave >= 1s of budget (so the third endpoint clears the loop-top
    // gate) AND leave the remainder >= 1s below the per-endpoint cap (so the third wait is visibly
    // truncated once Task 4 adds truncation). Both margins are 1s at the shipped constants. Earlier
    // revisions left one margin at ~195ms and flaked against the CORRECT implementation.
    static readonly TimeSpan StepDelay = (Budget - TimeSpan.FromSeconds(1)) / 2;

    static Func<string, CancellationToken, Task<IPAddress[]>> DelayingResolver(
        CountingResolver resolver, TimeSpan delay) =>
        async (host, ct) =>
        {
            var pending = resolver.Resolve(host, ct);
            // No token passed to Task.Delay, deliberately: real getaddrinfo does not honour one
            // mid-flight, and a cooperative stub would let a token-only implementation pass.
            await Task.Delay(delay);
            return await pending;
        };

    // Blocks SYNCHRONOUSLY, before returning its task. That is the only stub shape that burns
    // budget outside the WaitAsync race: an overrun that happens inside the returned task is cut
    // short by the race and throws from the catch, so control never returns to a loop top with the
    // budget already spent.
    static Func<string, CancellationToken, Task<IPAddress[]>> BlockingBefore(
        string blockingHost, TimeSpan block, CountingResolver resolver, Action? afterBlock = null)
    {
        return (host, ct) =>
        {
            var pending = resolver.Resolve(host, ct);
            if (host == blockingHost)
            {
                Thread.Sleep(block);
                afterBlock?.Invoke();
            }
            return pending;
        };
    }

    static string HostOf(string endpoint) => new Uri(endpoint).Host;

    [Fact]
    public async Task BudgetSpentByEarlyEndpoint_TailNotResolved()
    {
        var endpoints = Hostnames(4);
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(
            BlockingBefore(HostOf(endpoints[0]), OverBudgetBlock, resolver));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints));

        Assert.Equal(
            $"Transport endpoint validation exceeded its " +
            $"{TransportEndpointValidator.ValidationBudgetSeconds}s time budget",
            ex.Message);
        // The discriminating observable is work-not-done: the loop-top gate is a cost bound, and
        // without it the post-loop gate still produces the right message after walking the tail.
        Assert.Equal(new[] { HostOf(endpoints[0]) }, resolver.Seen);
    }

    [Fact]
    public async Task BudgetSpentByLastEndpoint_RejectedWithBudgetMessage()
    {
        var endpoints = Hostnames(2);
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(
            BlockingBefore(HostOf(endpoints[1]), OverBudgetBlock, resolver));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints));

        Assert.Contains("time budget", ex.Message);
        Assert.Equal(2, resolver.Count);
    }

    [Fact]
    public async Task BudgetAlreadySpent_HostnameEndpoint_ReportsBudgetNotDnsFailure()
    {
        var endpoints = Hostnames(2);
        var resolver = new CountingResolver(host =>
            host == HostOf(endpoints[1])
                ? Task.FromException<IPAddress[]>(new SocketException(11001))
                : Task.FromResult(OnePublicAddress));
        using var swap = new ResolverSwap(
            BlockingBefore(HostOf(endpoints[0]), OverBudgetBlock, resolver));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints));

        // Without the loop-top gate the second endpoint resolves and fails, and the operator is
        // told "DNS resolution failed" for what is actually budget exhaustion. The two have
        // different remedies.
        Assert.Contains("time budget", ex.Message);
        Assert.DoesNotContain("DNS resolution failed", ex.Message);
    }

    [Fact]
    public async Task SlowButSuccessfulResolutions_RejectedWithBudgetMessage()
    {
        Assert.True(StepDelay < Cap, "the step delay must stay under the per-endpoint cap");

        var endpoints = Hostnames(TransportEndpointValidator.MaxTransportEndpoints);
        var resolver = new CountingResolver();
        using var swap = new ResolverSwap(DelayingResolver(resolver, StepDelay));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints));

        Assert.Contains("time budget", ex.Message);
        Assert.True(resolver.Count < TransportEndpointValidator.MaxTransportEndpoints,
            $"expected fewer than {TransportEndpointValidator.MaxTransportEndpoints} resolutions, saw {resolver.Count}");
    }

    [Fact]
    public async Task CallerCancellation_AtLoopTop_OutranksAnAlreadySpentBudget()
    {
        var endpoints = Hostnames(2);
        var resolver = new CountingResolver();
        using var cts = new CancellationTokenSource();
        using var swap = new ResolverSwap(
            BlockingBefore(HostOf(endpoints[0]), OverBudgetBlock, resolver, cts.Cancel));

        // Both conditions hold at the second loop top: the budget is spent AND ct is cancelled.
        // ThrowsAnyAsync, not ThrowsAsync: the gate helper throws a plain OperationCanceledException
        // while WaitAsync throws the TaskCanceledException subclass, and the test's subject is the
        // token the exception carries, not which of the two it is.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints, ct: cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.Equal(new[] { HostOf(endpoints[0]) }, resolver.Seen);
    }

    // Never completes and never faults, so nothing here can raise UnobservedTaskException.
    static Task<IPAddress[]> Hang(CountingResolver resolver, string host, CancellationToken ct)
    {
        _ = resolver.Resolve(host, ct);
        return new TaskCompletionSource<IPAddress[]>().Task;
    }

    // Every case below whose stub never completes carries an explicit timeout. Without the race, an
    // await on such a stub does not fail — it HANGS, because the resolver ignores the token. The
    // timeout is what turns "no bound" into a red test instead of a stuck run, and it is what makes
    // the ablation sweep's race mutation runnable at all. It works because the collection disables
    // parallelization.
    [Fact(Timeout = 12000)]
    public async Task ResolutionExceedingPerEndpointCap_FailsWithinCap_AsDnsFailure()
    {
        var resolver = new CountingResolver();
        // Succeeds after the delay, on purpose: a stub that THREW would give a token-only
        // implementation the same DNS-failure message and pass vacuously. This is the only case
        // that discriminates race from token-only.
        using var swap = new ResolverSwap(DelayingResolver(resolver, Cap * 20));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(Hostnames(1)));
        sw.Stop();

        Assert.Contains("DNS resolution failed", ex.Message);
        Assert.DoesNotContain("time budget", ex.Message);
        Assert.True(sw.Elapsed < Cap + TimeSpan.FromSeconds(2),
            $"expected the per-endpoint cap to fire near {Cap}, took {sw.Elapsed}");
    }

    [Fact(Timeout = 15000)]
    public async Task CallerCancellation_InFlight_PropagatesAsCancellationNotDnsFailure()
    {
        var resolver = new CountingResolver(_ => new TaskCompletionSource<IPAddress[]>().Task);
        using var cts = new CancellationTokenSource();
        using var swap = new ResolverSwap(resolver.Resolve);

        var pending = TransportEndpointValidator.ValidateAsync(Hostnames(1), ct: cts.Token);
        await Task.Delay(500);
        await cts.CancelAsync();

        // Today this surfaces as InvalidOperationException("DNS resolution failed ..."): the catch
        // swallows OperationCanceledException. That masking is the behaviour this changes.
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact(Timeout = 30000)]
    public async Task LastWaitTruncatedToRemainingBudget()
    {
        Assert.True(StepDelay < Cap, "the step delay must stay under the per-endpoint cap");

        var endpoints = Hostnames(3);
        var resolver = new CountingResolver();
        var hanging = HostOf(endpoints[2]);
        var delaying = DelayingResolver(resolver, StepDelay);
        using var swap = new ResolverSwap((host, ct) =>
            host == hanging
                ? Hang(resolver, host, ct)
                : delaying(host, ct));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TransportEndpointValidator.ValidateAsync(endpoints));
        sw.Stop();

        Assert.Contains("time budget", ex.Message);
        // Correct: at ~4s elapsed the remainder is ~1s, the third wait truncates to it, and the
        // call returns at ~5s. With wait = cap it waits the full 3s and returns at ~7s.
        Assert.True(sw.Elapsed < Budget + TimeSpan.FromSeconds(1),
            $"expected a return near {Budget}, took {sw.Elapsed}");
    }

    sealed class AbandonedProbeException : Exception { }

    // Timeout because this case awaits an unbounded signal (`faulted.Task`) rather than a bounded
    // delay: any mutation that stretches the stub's fault would otherwise hang the sweep instead of
    // failing it. ~6x the correct-implementation runtime of ~5s.
    [Fact(Timeout = 30000)]
    public async Task AbandonedResolverTask_FaultIsObserved()
    {
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = 0;

        void Handler(object? _, UnobservedTaskExceptionEventArgs e)
        {
            // The event is process-global; an unfiltered handler attributes unrelated faults to
            // this test. [Collection] cannot isolate a process-global event, so filtering is the
            // only fix.
            if (e.Exception.InnerExceptions.Any(x => x is AbandonedProbeException))
                Interlocked.Increment(ref fired);
        }

        static async Task<IPAddress[]> FaultAfterAbandonment(TimeSpan delay)
        {
            await Task.Delay(delay);
            throw new AbandonedProbeException();
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            using (var swap = new ResolverSwap((_, _) =>
            {
                var t = FaultAfterAbandonment(Cap + TimeSpan.FromSeconds(2));
                // Signals without reading t.Exception, so this continuation does not itself observe
                // the fault.
                _ = t.ContinueWith(_ => faulted.TrySetResult(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return t;
            }))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => TransportEndpointValidator.ValidateAsync(Hostnames(1)));
            }

            // The task must be KNOWN to have faulted before the GC runs; collecting first makes the
            // ablation pass vacuously a quarter of the time.
            await faulted.Task;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Equal(0, Volatile.Read(ref fired));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }
}
