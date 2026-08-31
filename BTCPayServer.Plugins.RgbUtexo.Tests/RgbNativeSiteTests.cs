using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeSiteTests
{
    static RgbLibService.NativeCallResult Ok(string p) => new(p, null);
    static RgbLibService.NativeCallResult Err(string e) => new(null, e);

    [Fact] // G1-T8
    public void CreateUtxosBegin_SwallowsAlreadyAvailable_AndThrowsOtherwise()
    {
        Assert.Equal("", RgbLibService.InterpretCreateUtxosBegin(Err("Error: AlreadyAvailable")));
        Assert.Equal("", RgbLibService.InterpretCreateUtxosBegin(Err("alreadyavailable")));
        Assert.Equal("psbt", RgbLibService.InterpretCreateUtxosBegin(Ok("psbt")));
        Assert.Throws<RgbLibException>(() => RgbLibService.InterpretCreateUtxosBegin(Err("InsufficientFunds")));
    }

    [Fact] // G1-T9 — was: return [] on failure, which read as "no transactions"
    public void ListBtcTransactions_ThrowsOnFailure_InsteadOfReturningEmpty()
    {
        Assert.Throws<RgbLibException>(() => RgbLibService.InterpretListBtcTransactions(Err("boom")));
        Assert.Empty(RgbLibService.InterpretListBtcTransactions(Ok("[]")));
    }

    [Fact] // P-C8 — was: return [] on failure, which read as "this wallet holds no UTXOs"
    public void ListUnspents_ThrowsOnFailure_InsteadOfReportingAnEmptyPool()
    {
        var failed = Assert.Throws<RgbLibException>(() => RgbLibService.InterpretListUnspents(Err("boom")));
        Assert.Contains("boom", failed.Message);

        var uninterpretable = Assert.Throws<RgbLibException>(() => RgbLibService.InterpretListUnspents(default));
        Assert.Contains("list_unspents failed", uninterpretable.Message);

        // The discriminating observation: only a genuine Ok yields the empty pool that drove the sweep to
        // read zero colorable UTXOs, compute maximal demand, and sign a creation because of an error.
        Assert.Empty(RgbLibService.InterpretListUnspents(Ok("[]")));
        Assert.Single(RgbLibService.InterpretListUnspents(
            Ok("""[{"utxo":{"outpoint":{"txid":"t","vout":0},"btc_amount":1000,"colorable":true},"rgb_allocations":[]}]""")));
    }

    [Fact]
    public void ListUnspents_ReadsAllocationAmountsFromTheAssignmentKeyRgbLibActuallySends()
    {
        var unspents = RgbLibService.InterpretListUnspents(Ok("""
            [{"utxo":{"outpoint":{"txid":"t","vout":0},"btc_amount":1000,"colorable":true},
              "rgb_allocations":[
                {"asset_id":"a","assignment":{"Fungible":18446744073709551615},"settled":true},
                {"asset_id":"b","assignment":{"InflationRight":77},"settled":false},
                {"asset_id":"c","assignment":"NonFungible","settled":true},
                {"asset_id":"d","assignment":"Any","settled":false}]}]
            """));

        var allocations = Assert.Single(unspents).RgbAllocations;
        Assert.Equal(ulong.MaxValue, allocations[0].Amount);
        Assert.Equal(0UL, allocations[1].Amount);
        Assert.Equal(0UL, allocations[2].Amount);
        Assert.Equal(0UL, allocations[3].Amount);
    }

    [Fact]
    public void ListUnspents_TreatsAMissingOrUnreadableAssignmentAsZeroRatherThanThrowing()
    {
        var unspents = RgbLibService.InterpretListUnspents(Ok("""
            [{"utxo":{"outpoint":{"txid":"t","vout":0},"btc_amount":1000,"colorable":true},
              "rgb_allocations":[
                {"asset_id":"a","settled":true},
                {"asset_id":"b","assignment":{"Fungible":-1},"settled":true},
                {"asset_id":"c","assignment":{"Fungible":"5"},"settled":true},
                {"asset_id":"d","assignment":123,"settled":true}]}]
            """));

        var allocations = Assert.Single(unspents).RgbAllocations;
        Assert.All(allocations, a => Assert.Equal(0UL, a.Amount));
    }

    [Fact] // G1-T14
    public void Require_ReturnsPayloadOrThrowsWithTheCallName()
    {
        Assert.Equal("x", RgbLibService.Require(Ok("x"), "refresh"));

        var ex = Assert.Throws<RgbLibException>(() => RgbLibService.Require(Err("detail"), "refresh"));
        Assert.Contains("detail", ex.Message);

        var fallback = Assert.Throws<RgbLibException>(() => RgbLibService.Require(default, "refresh"));
        Assert.Contains("refresh failed", fallback.Message);
    }
}
