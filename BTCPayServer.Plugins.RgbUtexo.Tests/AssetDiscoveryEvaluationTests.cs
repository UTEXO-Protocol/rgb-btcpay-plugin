using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class AssetDiscoveryEvaluationTests
{
    const string CandidateAsset = "rgb:asset-1";
    const string Recipient = "utxob:abc";

    static RGBInvoice BlindInvoice(string recipientId = Recipient) =>
        new() { Id = "inv-blind", WalletId = "w1", AssetId = null, BtcPayInvoiceId = null, Amount = null, RecipientId = recipientId, Status = RGBInvoiceStatus.Pending };

    static RgbTransfer T(int idx, int status, long amount, int kind = 1, string recipientId = Recipient, string txid = "tx") =>
        new() { Idx = idx, Status = status, Amount = amount, Kind = kind, RecipientId = recipientId, Txid = txid };

    [Fact]
    public void Returns_null_when_invoice_has_asset_id()
    {
        var inv = BlindInvoice();
        inv.AssetId = "rgb:other";
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 100)]);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_null_when_invoice_has_BtcPayInvoiceId()
    {
        // Locks the discriminator invariant against audit C3 regression.
        var inv = BlindInvoice();
        inv.BtcPayInvoiceId = "btcpay-invoice-123";
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 100)]);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_null_when_no_transfer_matches_recipient_id()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 100, recipientId: "utxob:other")]);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_WaitingConfirmations_for_status_2_transfer()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 2, 50)]);
        Assert.NotNull(result);
        Assert.Equal(RGBInvoiceStatus.WaitingConfirmations, result!.NewStatus);
        Assert.Equal(50, result.ReceivedAmount);
        Assert.False(result.IsZeroAmount);
    }

    [Fact]
    public void Returns_Settled_for_status_3_positive_amount()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 1000)]);
        Assert.NotNull(result);
        Assert.Equal(RGBInvoiceStatus.Settled, result!.NewStatus);
        Assert.Equal(1000, result.ReceivedAmount);
        Assert.Equal(CandidateAsset, result.AssetId);
    }

    [Fact]
    public void Flags_IsZeroAmount_for_status_3_zero_amount()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 0)]);
        Assert.NotNull(result);
        Assert.True(result!.IsZeroAmount);
        Assert.Equal(RGBInvoiceStatus.Pending, result.NewStatus);
        Assert.Equal(0, result.ReceivedAmount);
    }

    [Fact]
    public void Status1WaitingCounterpartyDoesNotMatchTheInvoiceItCreated()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 1, 0)]);
        Assert.Null(result);
    }

    [Fact]
    public void Picks_lowest_Idx_when_multiple_matches()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(2, 3, 50), T(1, 3, 100)]);
        Assert.NotNull(result);
        Assert.Equal(1, result!.Transfer.Idx);
        Assert.Equal(100, result.ReceivedAmount);
    }

    [Fact]
    public void Ignores_outgoing_kind_3_transfers()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 3, 100, kind: 3)]);
        Assert.Null(result);
    }

    [Fact]
    public void Returns_Failed_for_status_4_transfer()
    {
        var inv = BlindInvoice();
        var result = RGBInvoiceListener.EvaluateAssetDiscoveryMatch(inv, CandidateAsset, [T(1, 4, 0)]);
        Assert.NotNull(result);
        Assert.Equal(RGBInvoiceStatus.Failed, result!.NewStatus);
        Assert.Equal(0, result.ReceivedAmount);
        Assert.False(result.IsZeroAmount);
    }
}
