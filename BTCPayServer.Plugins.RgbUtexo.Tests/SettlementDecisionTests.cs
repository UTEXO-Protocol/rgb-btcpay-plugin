using BTCPayServer.Plugins.RgbUtexo.Services;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class SettlementDecisionTests
{
    [Fact]
    public void Status3_AmountMeetsRequired_RecordSettled()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 100, 100);
        Assert.Equal(SettlementDecision.RecordSettled, result);
    }

    [Fact]
    public void Status3_AmountExceedsRequired_RecordSettled()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 200, 100);
        Assert.Equal(SettlementDecision.RecordSettled, result);
    }

    [Fact]
    public void Status3_AmountBelowRequired_RecordUnderpaid()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 1, 100);
        Assert.Equal(SettlementDecision.RecordUnderpaid, result);
    }

    [Fact]
    public void Status3_AmountZero_RejectZeroAmount()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 0, 100);
        Assert.Equal(SettlementDecision.RejectZeroAmount, result);
    }

    [Fact]
    public void Status3_AmountNegative_RejectZeroAmount()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, -5, 100);
        Assert.Equal(SettlementDecision.RejectZeroAmount, result);
    }

    [Fact]
    public void Status3_WildcardInvoice_AnyPositiveAmount_RecordSettled()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 1, null);
        Assert.Equal(SettlementDecision.RecordSettled, result);
    }

    [Fact]
    public void Status3_WildcardInvoice_ZeroAmount_RejectZeroAmount()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 0, null);
        Assert.Equal(SettlementDecision.RejectZeroAmount, result);
    }

    [Fact]
    public void Status1_PositiveAmount_DoesNotTreatWaitingCounterpartyAsPayment()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(1, 50, 100);
        Assert.Equal(SettlementDecision.TransitionWaitingNoPayment, result);
    }

    [Fact]
    public void Status2_PositiveAmount_TransitionWaiting()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(2, 50, 100);
        Assert.Equal(SettlementDecision.TransitionWaiting, result);
    }

    [Fact]
    public void Status1_ZeroAmount_TransitionWaitingNoPayment()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(1, 0, 100);
        Assert.Equal(SettlementDecision.TransitionWaitingNoPayment, result);
    }

    [Fact]
    public void Status3_LongMaxValue_RecordSettled()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, long.MaxValue, 100);
        Assert.Equal(SettlementDecision.RecordSettled, result);
    }

    [Fact]
    public void Status3_LongMinValue_RejectZeroAmount()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, long.MinValue, 100);
        Assert.Equal(SettlementDecision.RejectZeroAmount, result);
    }

    [Fact]
    public void Status3_InvoiceAmountZero_AnyPositiveSettles()
    {
        var result = RGBInvoiceListener.EvaluateTransfer(3, 1, 0);
        Assert.Equal(SettlementDecision.RecordSettled, result);
    }

    [Fact]
    public void MultiTransfer_CumulativeSum_StaysUnderpaidThenSettles()
    {
        long invoiceAmount = 100;

        var poll1Sum = new long[] { 40 }.Sum();
        Assert.Equal(SettlementDecision.RecordUnderpaid, RGBInvoiceListener.EvaluateTransfer(3, poll1Sum, invoiceAmount));

        var poll2Sum = new long[] { 40, 30 }.Sum();
        Assert.Equal(SettlementDecision.RecordUnderpaid, RGBInvoiceListener.EvaluateTransfer(3, poll2Sum, invoiceAmount));

        var poll2Again = new long[] { 40, 30 }.Sum();
        Assert.Equal(SettlementDecision.RecordUnderpaid, RGBInvoiceListener.EvaluateTransfer(3, poll2Again, invoiceAmount));

        var poll3Sum = new long[] { 40, 30, 30 }.Sum();
        Assert.Equal(SettlementDecision.RecordSettled, RGBInvoiceListener.EvaluateTransfer(3, poll3Sum, invoiceAmount));
    }

    [Fact]
    public void MultiTransfer_RepeatedPoll_SameTransfers_NoDoubleCounting()
    {
        long invoiceAmount = 100;

        var transferAmounts = new long[] { 40, 30 };
        var sum = transferAmounts.Sum();

        for (int poll = 0; poll < 5; poll++)
        {
            Assert.Equal(SettlementDecision.RecordUnderpaid,
                RGBInvoiceListener.EvaluateTransfer(3, sum, invoiceAmount));
        }
    }

    [Fact]
    public void MultiTransfer_ExactAmount_Settles()
    {
        long invoiceAmount = 100;
        var sum = new long[] { 33, 33, 34 }.Sum();
        Assert.Equal(SettlementDecision.RecordSettled, RGBInvoiceListener.EvaluateTransfer(3, sum, invoiceAmount));
    }
}
