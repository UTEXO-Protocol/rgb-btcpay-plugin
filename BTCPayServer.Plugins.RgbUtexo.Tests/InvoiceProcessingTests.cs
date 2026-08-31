using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class InvoiceProcessingTests
{
    static RGBInvoice MakeInvoice(long? amount = 100, string assetId = "USDT", RGBInvoiceStatus status = RGBInvoiceStatus.Pending, string recipientId = "utxob:abc") =>
        new() { Id = "inv-1", WalletId = "w1", AssetId = assetId, Amount = amount, RecipientId = recipientId, Status = status };

    static RgbTransfer T(int idx, int status, long amount, string recipientId = "utxob:abc", string txid = "tx") =>
        new() { Idx = idx, Status = status, Amount = amount, RecipientId = recipientId, Txid = txid };

    [Fact]
    public void NoMatchingTransfers_NoStateChange()
    {
        var result = RGBInvoiceListener.EvaluateInvoiceState(MakeInvoice(), Array.Empty<RgbTransfer>());
        Assert.Null(result.NewStatus);
        Assert.Empty(result.PaymentsToRecord);
    }

    [Fact]
    public void SingleWaitingConfirmationsTransfer_TransitionsToWaitingConfirmations()
    {
        var inv = MakeInvoice();
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 2, 50)]);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, result.NewStatus);
        Assert.Equal(50, result.ReceivedAmount);
        Assert.Single(result.PaymentsToRecord);
    }

    [Fact]
    public void SingleSettledFullPayment_Settles()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 100)]);
        Assert.Equal(RGBInvoiceStatus.Settled, result.NewStatus);
        Assert.Equal(100, result.ReceivedAmount);
        Assert.Single(result.PaymentsToRecord);
        Assert.Equal(BTCPayServer.Data.PaymentStatus.Settled, result.PaymentStatus);
    }

    [Fact]
    public void SingleSettledUnderpayment_StaysUnderpaid()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 60)]);
        Assert.Equal(RGBInvoiceStatus.Underpaid, result.NewStatus);
        Assert.Equal(60, result.ReceivedAmount);
        Assert.Single(result.PaymentsToRecord);
        Assert.Equal(BTCPayServer.Data.PaymentStatus.Processing, result.PaymentStatus);
    }

    [Fact]
    public void TwoSettledTransfers_CumulativeSettles_RecordsBoth()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 60), T(2, 3, 40)]);
        Assert.Equal(RGBInvoiceStatus.Settled, result.NewStatus);
        Assert.Equal(100, result.ReceivedAmount);
        Assert.Equal(2, result.PaymentsToRecord.Count);
        Assert.Equal(1, result.PaymentsToRecord[0].Idx);
        Assert.Equal(2, result.PaymentsToRecord[1].Idx);
    }

    [Fact]
    public void TwoSettledTransfers_StillUnderpaid_RecordsBothAsProcessing()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 30), T(2, 3, 40)]);
        Assert.Equal(RGBInvoiceStatus.Underpaid, result.NewStatus);
        Assert.Equal(70, result.ReceivedAmount);
        Assert.Equal(2, result.PaymentsToRecord.Count);
        Assert.Equal(BTCPayServer.Data.PaymentStatus.Processing, result.PaymentStatus);
    }

    [Fact]
    public void AlreadySettled_NoStateChange()
    {
        var inv = MakeInvoice(amount: 100, status: RGBInvoiceStatus.Settled);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 100)]);
        Assert.Null(result.NewStatus);
    }

    [Fact]
    public void ZeroSettledAmount_RejectsForManualReview()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, 0)]);
        Assert.Null(result.NewStatus);
        Assert.Equal(SettlementDecision.RejectZeroAmount, result.Decision);
    }

    [Fact]
    public void OverflowSum_SaturatesToMaxValueAndSettles()
    {
        var inv = MakeInvoice(amount: 100);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 3, long.MaxValue / 2 + 1), T(2, 3, long.MaxValue / 2 + 1)]);
        Assert.Equal(RGBInvoiceStatus.Settled, result.NewStatus);
        Assert.Equal(long.MaxValue, result.ReceivedAmount);
    }

    [Fact]
    public void WaitingTransfer_WithExistingSettledInvoice_NoChange()
    {
        var inv = MakeInvoice(amount: 100, status: RGBInvoiceStatus.WaitingConfirmations);
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 2, 50)]);
        Assert.Null(result.NewStatus);
    }

    [Fact]
    public void ZeroAmountWaitingTransfer_TransitionsButNoPayment()
    {
        var inv = MakeInvoice();
        var result = RGBInvoiceListener.EvaluateInvoiceState(inv, [T(1, 2, 0)]);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, result.NewStatus);
        Assert.Equal(0, result.ReceivedAmount);
        Assert.Empty(result.PaymentsToRecord);
    }

    [Fact]
    public void WaitingCounterpartyRowDoesNotAdvanceAnUnfundedInvoice()
    {
        var result = RGBInvoiceListener.EvaluateInvoiceState(MakeInvoice(), [T(1, 1, 0)]);

        Assert.Null(result.NewStatus);
        Assert.Empty(result.PaymentsToRecord);
    }

    [Fact]
    public void SaturatingSum_NoOverflow()
    {
        Assert.Equal(150, RGBInvoiceListener.SaturatingSum(new long[] { 50, 100 }));
    }

    [Fact]
    public void SaturatingSum_Overflow_ReturnsMaxValue()
    {
        Assert.Equal(long.MaxValue, RGBInvoiceListener.SaturatingSum(new long[] { long.MaxValue, 1 }));
    }

    [Fact]
    public void SaturatingSum_Empty_ReturnsZero()
    {
        Assert.Equal(0, RGBInvoiceListener.SaturatingSum(Array.Empty<long>()));
    }
}
