using RgbLib;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbLibWalletHandle : IDisposable
{
    private RgbLibWallet? _wallet;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    
    public string WalletId { get; }
    public bool IsDisposed { get; private set; }
    public DateTime LastAccess { get; private set; }

    public RgbLibWalletHandle(RgbLibWallet wallet, string walletId)
    {
        _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        WalletId = walletId;
        LastAccess = DateTime.UtcNow;
    }

    public RgbLibWallet GetWallet()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        LastAccess = DateTime.UtcNow;
        return _wallet!;
    }

    public async Task<T> ExecuteAsync<T>(Func<RgbLibWallet, T> operation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        
        await _semaphore.WaitAsync(ct);
        try
        {
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
            LastAccess = DateTime.UtcNow;
            operation(_wallet!);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (IsDisposed) return;

        bool acquired = _semaphore.Wait(TimeSpan.FromSeconds(5));
        try
        {
            if (IsDisposed) return;
            
            _wallet?.Dispose();
            _wallet = null;
            IsDisposed = true;
        }
        finally
        {
            if (acquired) _semaphore.Release();
            _semaphore.Dispose();
        }
        
        GC.SuppressFinalize(this);
    }
}

public class RgbLibException : Exception
{
    public RgbLibException(string message) : base(message) { }
    public RgbLibException(string message, Exception inner) : base(message, inner) { }
}
