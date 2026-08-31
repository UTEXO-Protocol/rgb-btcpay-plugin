using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class TransportEndpointValidator
{
    static readonly string[] AllowedSchemes = ["rpc", "rpcs"];

    // 8 is ~8x observed practice: a real RGB invoice carries one transport endpoint, the proxy.
    // A list of ten thousand is a bug on any network, so the cap ignores allowPrivateNetworks.
    public const int MaxTransportEndpoints = 8;
    public const int MaxTransportEndpointLength = 2_048;
    public const int MaxTransportEndpointsTotalLength = 8_192;
    public const int MaxRgbInvoiceLength = 16_384;
    internal const int MaxConcurrentResolvers = 16;

    // The count bounds how many endpoints are tried; only a clock bounds how long that takes,
    // because the platform resolver cannot be interrupted once getaddrinfo is running.
    public const int ValidationBudgetSeconds = 5;
    public const int PerEndpointTimeoutSeconds = 3;

    // The seam exists because no test can make the real resolver slow, on demand, deterministically —
    // and slow-but-successful resolution is the vector this class now bounds. internal, never public:
    // production must have no injection point here.
    static readonly Func<string, CancellationToken, Task<IPAddress[]>> RealResolver = Dns.GetHostAddressesAsync;
    static SemaphoreSlim _resolverConcurrency = new(MaxConcurrentResolvers, MaxConcurrentResolvers);

    internal static Func<string, CancellationToken, Task<IPAddress[]>> Resolver { get; set; } = RealResolver;

    internal static void ResetResolver()
    {
        Resolver = RealResolver;
        _resolverConcurrency = new SemaphoreSlim(MaxConcurrentResolvers, MaxConcurrentResolvers);
    }

    public static async Task<List<string>> ValidateAsync(
        List<string> endpoints, bool allowPrivateNetworks = false,
        CancellationToken ct = default)
    {
        if (endpoints == null || endpoints.Count == 0)
            throw new InvalidOperationException("No transport endpoints provided");

        if (endpoints.Count > MaxTransportEndpoints)
            throw new InvalidOperationException(
                $"Too many transport endpoints: {endpoints.Count} (maximum {MaxTransportEndpoints})");

        var totalLength = 0;
        foreach (var endpoint in endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint)
                || endpoint.Length > MaxTransportEndpointLength)
                throw new InvalidOperationException(
                    $"Transport endpoint exceeds the {MaxTransportEndpointLength}-character limit");
            totalLength = checked(totalLength + endpoint.Length);
            if (totalLength > MaxTransportEndpointsTotalLength)
                throw new InvalidOperationException(
                    $"Transport endpoints exceed the {MaxTransportEndpointsTotalLength}-character aggregate limit");
        }

        var budget = TimeSpan.FromSeconds(ValidationBudgetSeconds);
        var sw = Stopwatch.StartNew();

        var validated = new List<string>();
        foreach (var endpoint in endpoints)
        {
            ThrowIfCancelledOrOverBudget(ct, sw, budget);
            var pinned = await ValidateAndPinEndpointAsync(endpoint, allowPrivateNetworks, ct, sw, budget);
            validated.Add(pinned);
        }

        // The literal-IP and allowPrivateNetworks paths consult no clock, so without this the call
        // can return success with the budget already violated — a false-ACCEPT.
        ThrowIfCancelledOrOverBudget(ct, sw, budget);
        return validated;
    }

    static void ThrowIfCancelledOrOverBudget(CancellationToken ct, Stopwatch sw, TimeSpan budget)
    {
        ct.ThrowIfCancellationRequested();
        if (sw.Elapsed >= budget)
            throw BudgetExceeded();
    }

    static InvalidOperationException BudgetExceeded() =>
        new($"Transport endpoint validation exceeded its {ValidationBudgetSeconds}s time budget");

    static async Task<string> ValidateAndPinEndpointAsync(
        string endpoint, bool allowPrivateNetworks, CancellationToken ct,
        Stopwatch sw, TimeSpan budget)
    {
        Uri uri;
        try { uri = new Uri(endpoint); }
        catch (UriFormatException)
        { throw new InvalidOperationException($"Malformed transport endpoint: {endpoint}"); }

        if (!AllowedSchemes.Any(s =>
            uri.Scheme.Equals(s, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Transport endpoint scheme '{uri.Scheme}' not allowed. Use rpc:// or rpcs://");

        if (allowPrivateNetworks) return endpoint;

        var host = uri.Host.Trim('[', ']');

        if (IPAddress.TryParse(host, out var directIp))
        {
            ValidateIpAddress(directIp, endpoint);
            return endpoint;
        }

        var capSpan = TimeSpan.FromSeconds(PerEndpointTimeoutSeconds);
        var remaining = budget - sw.Elapsed;
        // URI parsing and the scheme checks above can carry us past the deadline, and WaitAsync
        // rejects a negative timeout with ArgumentOutOfRangeException — outside the catch filter
        // below, so it would escape as an untyped exception instead of a rejection. Exactly -1ms
        // would be worse still: that value means "wait forever".
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var wait = remaining < capSpan ? remaining : capSpan;
        // Recorded before the await, never re-derived from the clock afterwards: WaitAsync fires off
        // the millisecond-truncated timer queue while Stopwatch is a different clock, and 2% of
        // budget-bound waits end with sw.Elapsed still under budget.
        var wasBudgetBound = remaining < capSpan;

        IPAddress[] addresses;
        try
        {
            var resolverConcurrency = _resolverConcurrency;
            if (!await resolverConcurrency.WaitAsync(wait, ct))
                throw new TimeoutException("transport resolver concurrency limit remained saturated");

            Task<IPAddress[]> resolveTask;
            try { resolveTask = Resolver(host, ct); }
            catch
            {
                resolverConcurrency.Release();
                throw;
            }

            // getaddrinfo may ignore cancellation. Keep its global permit until the actual source
            // task completes, even when this request's deadline wins, so abandoned resolver work is
            // bounded instead of accumulating behind repeated hostile invoices.
            _ = resolveTask.ContinueWith(static (t, state) =>
                {
                    _ = t.Exception;
                    ((SemaphoreSlim)state!).Release();
                },
                resolverConcurrency,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var resolverRemaining = budget - sw.Elapsed;
            if (resolverRemaining < TimeSpan.Zero) resolverRemaining = TimeSpan.Zero;
            var resolverWait = resolverRemaining < capSpan ? resolverRemaining : capSpan;
            addresses = await resolveTask.WaitAsync(resolverWait, ct);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            // The flag says WHICH timeout would fire; it says nothing about a non-timeout failure,
            // so a fast SocketException late in the budget must not be reported as exhaustion.
            if (ex is TimeoutException && wasBudgetBound)
                throw BudgetExceeded();
            throw new InvalidOperationException(
                $"DNS resolution failed for transport endpoint host '{host}'", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException(
                $"DNS resolution returned no addresses for '{host}'");

        foreach (var ip in addresses)
            ValidateIpAddress(ip, endpoint);

        // For TLS endpoints (rpcs://), preserve the hostname so the TLS client can
        // perform hostname/SAN verification against the server certificate. IP pinning
        // is only applied to plaintext rpc:// endpoints where DNS rebinding is the
        // primary concern and TLS validation isn't in play.
        if (uri.Scheme.Equals("rpcs", StringComparison.OrdinalIgnoreCase))
            return endpoint;

        var pinnedIp = addresses[0];
        var ipHost = pinnedIp.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{pinnedIp}]" : pinnedIp.ToString();
        var pinned = $"{uri.Scheme}://{ipHost}:{uri.Port}{uri.PathAndQuery}";
        return pinned;
    }

    static void ValidateIpAddress(IPAddress ip, string endpoint)
    {
        var checkIp = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

        if (IPAddress.IsLoopback(checkIp))
            throw new InvalidOperationException(
                $"Transport endpoint resolves to loopback address: {endpoint}");

        if (checkIp.Equals(IPAddress.Any) || checkIp.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException(
                $"Transport endpoint resolves to unspecified address: {endpoint}");

        var bytes = checkIp.GetAddressBytes();
        if (checkIp.AddressFamily == AddressFamily.InterNetwork)
        {
            var isPrivate = bytes switch
            {
                [0, ..] => true,
                [10, ..] => true,
                [100, >= 64 and <= 127, ..] => true,
                [172, >= 16 and <= 31, ..] => true,
                [192, 168, ..] => true,
                [169, 254, ..] => true,
                [192, 0, 2, ..] => true,
                [198, 18 or 19, ..] => true,
                [198, 51, 100, ..] => true,
                [203, 0, 113, ..] => true,
                [>= 224, ..] => true,
                _ => false
            };
            if (isPrivate)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to private/reserved address: {endpoint}");
        }
        else if (checkIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (checkIp.IsIPv6LinkLocal)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to link-local IPv6: {endpoint}");
            if (IsIPv6SiteLocalPrefix(bytes))
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to site-local IPv6: {endpoint}");
            if (bytes.Length >= 1 && (bytes[0] & 0xFE) == 0xFC)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to unique-local IPv6: {endpoint}");
            if (bytes.Length >= 1 && bytes[0] == 0xFF)
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to multicast IPv6: {endpoint}");
            if (IsIPv4TranslationOrTunnelPrefix(bytes))
                throw new InvalidOperationException(
                    $"Transport endpoint resolves to an IPv6 address that embeds or translates to an "
                    + $"IPv4 destination, which this validator cannot check: {endpoint}");
        }
    }

    internal static bool IsIPv6SiteLocalPrefix(byte[] bytes) =>
        bytes.Length == 16 && bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0xC0;

    internal static bool IsIPv4TranslationOrTunnelPrefix(byte[] bytes)
    {
        if (bytes.Length != 16) return false;

        var wellKnownNat64 = bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B;
        if (wellKnownNat64) return true;

        var sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        if (sixToFour) return true;

        var teredo = bytes[0] == 0x20 && bytes[1] == 0x01
                     && bytes[2] == 0x00 && bytes[3] == 0x00;
        if (teredo) return true;

        var ipv4Compatible = bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0
                             && bytes.AsSpan(12, 4).IndexOfAnyExcept((byte)0) >= 0;
        return ipv4Compatible;
    }
}
