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
    private readonly TimeSpan _disposeTimeout;
    private readonly ILogger? _log;

    public string WalletId { get; }
    public bool IsDisposed => _isDisposed;
    public bool NativeWalletFreed => _nativeWalletFreed;
    public DateTime LastAccess { get; private set; }

    public RgbLibWalletHandle(RgbLibWallet wallet, string walletId, ILogger? log = null)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        WalletId = walletId;
        _disposeTimeout = TimeSpan.FromSeconds(30);
        _log = log;
        LastAccess = DateTime.UtcNow;
    }

    internal RgbLibWalletHandle(string walletId, TimeSpan disposeTimeout)
    {
        WalletId = walletId;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        bool acquired = _semaphore.Wait(_disposeTimeout);
        if (acquired)
        {
            try
            {
                _isDisposed = true;
                DisposeNativeWallet();
                _nativeWalletFreed = true;
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
}

public class RgbLibException : Exception
{
    public RgbLibException(string message) : base(message) { }
    public RgbLibException(string message, Exception inner) : base(message, inner) { }
}
