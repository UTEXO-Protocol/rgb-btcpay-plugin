using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class BoundedInvoiceWorkQueueTests
{
    [Fact]
    public void Flood_NeverExceedsCapacity_AndRequestsDurableRecovery()
    {
        var queue = new BoundedInvoiceWorkQueue(capacity: 2);

        Assert.True(queue.TryEnqueue("first"));
        Assert.True(queue.TryEnqueue("second"));
        Assert.False(queue.TryEnqueue("overflow"));

        Assert.Equal(2, queue.Count);
        Assert.True(queue.HasRecoveryPending);
        Assert.Equal(new[] { "first", "second" }, Drain(queue));
    }

    [Fact]
    public void FailedRecovery_DoesNotLoseSignal()
    {
        var queue = OverflowedQueue();
        var claim = Assert.IsType<long>(queue.TryClaimRecovery());

        queue.CompleteRecovery(claim, succeeded: false);

        Assert.True(queue.HasRecoveryPending);
        Assert.NotNull(queue.TryClaimRecovery());
    }

    [Fact]
    public void OverflowDuringRecovery_RemainsPendingAfterOlderClaimSucceeds()
    {
        var queue = OverflowedQueue();
        var firstClaim = Assert.IsType<long>(queue.TryClaimRecovery());

        Assert.False(queue.TryEnqueue("new-overflow"));
        queue.CompleteRecovery(firstClaim, succeeded: true);

        Assert.True(queue.HasRecoveryPending);
        var secondClaim = Assert.IsType<long>(queue.TryClaimRecovery());
        Assert.True(secondClaim > firstClaim);
    }

    [Fact]
    public void SuccessfulRecovery_ClearsOnlyClaimedGeneration()
    {
        var queue = OverflowedQueue();
        var claim = Assert.IsType<long>(queue.TryClaimRecovery());

        queue.CompleteRecovery(claim, succeeded: true);

        Assert.False(queue.HasRecoveryPending);
        Assert.Null(queue.TryClaimRecovery());
    }

    [Fact]
    public void ExplicitRecoveryRequest_DoesNotConsumeQueueCapacity()
    {
        var queue = new BoundedInvoiceWorkQueue(capacity: 1);

        queue.RequestRecovery();

        Assert.True(queue.HasRecoveryPending);
        Assert.Equal(0, queue.Count);
        Assert.True(queue.TryEnqueue("invoice"));
    }

    [Fact]
    public void DequeueFreesCapacityWithoutBlockingWriter()
    {
        var queue = OverflowedQueue();
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal("first", first);

        Assert.True(queue.TryEnqueue("replacement"));
        Assert.Equal(2, queue.Count);
    }

    static BoundedInvoiceWorkQueue OverflowedQueue()
    {
        var queue = new BoundedInvoiceWorkQueue(capacity: 2);
        Assert.True(queue.TryEnqueue("first"));
        Assert.True(queue.TryEnqueue("second"));
        Assert.False(queue.TryEnqueue("overflow"));
        return queue;
    }

    static string[] Drain(BoundedInvoiceWorkQueue queue)
    {
        var result = new List<string>();
        while (queue.TryDequeue(out var id)) result.Add(id);
        return result.ToArray();
    }
}
