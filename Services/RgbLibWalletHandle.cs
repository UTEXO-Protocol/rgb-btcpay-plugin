using Microsoft.Extensions.Logging;
using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbLibWalletHandle : IDisposable
{
    private RgbLibWallet? _wallet;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile bool _isDisposed;
    private volatile bool _nativeWalletFreed;
    private int _disposeStarted;
    private int _deferredDisposeStarted;
    private readonly TimeSpan _disposeTimeout;
    private readonly ILogger? _log;
    private readonly string? _walletDir;

    public string WalletId { get; }
    public bool IsDisposed => _isDisposed;
    public bool NativeWalletFreed => _nativeWalletFreed;
    public DateTime LastAccess { get; private set; }

    public RgbLibWalletHandle(RgbLibWallet wallet, string walletId, string walletDir, ILogger? log = null)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        WalletId = walletId;
        _walletDir = walletDir;
        _disposeTimeout = TimeSpan.FromSeconds(30);
        _log = log;
        LastAccess = DateTime.UtcNow;
    }

    internal RgbLibWalletHandle(string walletId, TimeSpan disposeTimeout, string? walletDir = null)
    {
        WalletId = walletId;
        _walletDir = walletDir;
        _disposeTimeout = disposeTimeout;
        LastAccess = DateTime.UtcNow;
    }

    public async Task<T> ExecuteAsync<T>(Func<RgbLibWallet, T> operation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _semaphore.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            using var walletAccess = _walletDir == null
                ? null
                : RgbNativeSendLease.AcquireWalletAccess(_walletDir);
            LastAccess = DateTime.UtcNow;
            return operation(_wallet!);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ExecuteAsync(Action<RgbLibWallet> operation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        await _semaphore.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            using var walletAccess = _walletDir == null
                ? null
                : RgbNativeSendLease.AcquireWalletAccess(_walletDir);
            LastAccess = DateTime.UtcNow;
            operation(_wallet!);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    protected virtual void DisposeNativeWallet()
    {
        _wallet?.Dispose();
        _wallet = null;
    }

    internal void CompleteTimedOutDispose()
    {
        if (NativeWalletFreed) return;

        _semaphore.Wait();
        try
        {
            if (_nativeWalletFreed) return;

            using var walletAccess = _walletDir == null
                ? null
                : RgbNativeSendLease.AcquireWalletAccess(_walletDir, allowMarked: true);
            DisposeNativeWallet();
            _nativeWalletFreed = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        bool acquired = _semaphore.Wait(_disposeTimeout);
        if (acquired)
        {
            try
            {
                _isDisposed = true;
                try
                {
                    using var walletAccess = _walletDir == null
                        ? null
                        : RgbNativeSendLease.AcquireWalletAccess(_walletDir, allowMarked: true);
                    DisposeNativeWallet();
                    _nativeWalletFreed = true;
                }
                catch (Exception ex) when (ex is IOException or RgbWalletQuarantinedException)
                {
                    // A foreign staged operation owns the wallet. The cache keeps this disposed
                    // handle until deferred disposal can take the native-access mutex safely.
                    _log?.LogWarning(ex,
                        "RGB wallet handle {WalletId} native disposal deferred while the wallet is leased",
                        WalletId);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        else
        {
            _isDisposed = true;
            _log?.LogWarning(
                "RGB wallet handle {WalletId} disposed while an operation was still running after {Timeout}s; native wallet leaked to avoid use-after-free",
                WalletId, _disposeTimeout.TotalSeconds);
        }

        GC.SuppressFinalize(this);
    }

    internal bool TryStartDeferredDispose() =>
        Interlocked.Exchange(ref _deferredDisposeStarted, 1) == 0;
}

public class RgbLibException : Exception
{
    public RgbLibException(string message) : base(message) { }
    public RgbLibException(string message, Exception inner) : base(message, inner) { }
}
