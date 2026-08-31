using System.Threading.Channels;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

/// <summary>
/// A non-blocking, process-local hint queue. Overflow never blocks the event callback and never
/// pretends the hint was durable: it advances a generation which the listener clears only after a
/// successful database-backed wallet sweep. A newer overflow cannot be cleared by an older sweep.
/// </summary>
public sealed class BoundedInvoiceWorkQueue
{
    readonly Channel<string> _channel;
    long _overflowGeneration;
    long _recoveredGeneration;
    int _count;

    public BoundedInvoiceWorkQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }
    public int Count => Volatile.Read(ref _count);
    public bool HasRecoveryPending =>
        Volatile.Read(ref _overflowGeneration) != Volatile.Read(ref _recoveredGeneration);

    public bool TryEnqueue(string invoiceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        if (_channel.Writer.TryWrite(invoiceId))
        {
            Interlocked.Increment(ref _count);
            return true;
        }

        Interlocked.Increment(ref _overflowGeneration);
        return false;
    }

    public bool TryWrite(string invoiceId) => TryEnqueue(invoiceId);

    public void RequestRecovery() => Interlocked.Increment(ref _overflowGeneration);

    public bool TryDequeue(out string invoiceId)
    {
        if (_channel.Reader.TryRead(out invoiceId!))
        {
            Interlocked.Decrement(ref _count);
            return true;
        }

        invoiceId = "";
        return false;
    }

    public long? TryClaimRecovery()
    {
        var overflow = Volatile.Read(ref _overflowGeneration);
        return overflow != Volatile.Read(ref _recoveredGeneration) ? overflow : null;
    }

    public void CompleteRecovery(long generation, bool succeeded)
    {
        if (!succeeded) return;

        Volatile.Write(ref _recoveredGeneration, generation);
    }
}
