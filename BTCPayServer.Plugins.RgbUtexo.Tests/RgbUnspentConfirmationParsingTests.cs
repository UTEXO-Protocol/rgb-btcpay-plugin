using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbUnspentConfirmationParsingTests
{
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string Txid(char c) => new(c, 64);

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(870000, true)]
    public void Electrum_TreatsOnlyAPositiveHeightAsConfirmed(int height, bool expected)
    {
        var rows = ElectrumClient.ReadUnspentRows(Parse(
            $$"""[{"tx_hash":"{{Txid('a')}}","tx_pos":3,"height":{{height}},"value":1000}]"""));

        Assert.Single(rows);
        Assert.Equal(Txid('a'), rows[0].Outpoint.Txid);
        Assert.Equal(3, rows[0].Outpoint.Vout);
        Assert.True(expected == rows[0].ConfirmedInABlock,
            $"Electrum height {height} must read as ConfirmedInABlock={expected}; a mempool entry "
            + "reports height 0 and an unconfirmed-parent entry may report a negative height, and "
            + "spending either can produce a transaction that never confirms.");
    }

    [Fact]
    public void Electrum_TreatsAMissingHeightAsUnconfirmed()
    {
        var rows = ElectrumClient.ReadUnspentRows(Parse(
            $$"""[{"tx_hash":"{{Txid('a')}}","tx_pos":0,"value":1000}]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock,
            "A row with no height at all must fail closed as unconfirmed. Treating an absent field "
            + "as confirmed would let the send path spend an output it never established the depth of.");
    }

    [Fact]
    public void Electrum_TreatsANonNumericHeightAsUnconfirmed()
    {
        var rows = ElectrumClient.ReadUnspentRows(Parse(
            $$"""[{"tx_hash":"{{Txid('a')}}","tx_pos":0,"height":null}]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock);
    }

    [Fact]
    public void Electrum_ReadsEveryRowRatherThanOnlyTheFirst()
    {
        var rows = ElectrumClient.ReadUnspentRows(Parse(
            $$"""
            [{"tx_hash":"{{Txid('a')}}","tx_pos":0,"height":0},
             {"tx_hash":"{{Txid('b')}}","tx_pos":1,"height":5}]
            """));

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].ConfirmedInABlock);
        Assert.True(rows[1].ConfirmedInABlock);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Esplora_TakesConfirmationFromStatusConfirmed(string confirmed, bool expected)
    {
        var rows = EsploraHttpClient.ReadUnspentRows(Parse(
            $$"""[{"txid":"{{Txid('c')}}","vout":2,"status":{"confirmed": {{confirmed}} } } ]"""));

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Outpoint.Vout);
        Assert.True(expected == rows[0].ConfirmedInABlock,
            $"Esplora status.confirmed={confirmed} must read as ConfirmedInABlock={expected}.");
    }

    [Fact]
    public void Esplora_TreatsAMissingStatusAsUnconfirmedRatherThanThrowing()
    {
        var rows = EsploraHttpClient.ReadUnspentRows(Parse(
            $$"""[{"txid":"{{Txid('d')}}","vout":0}]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock);
    }

    [Fact]
    public void Esplora_TreatsAStatusWithoutAConfirmedFlagAsUnconfirmed()
    {
        var rows = EsploraHttpClient.ReadUnspentRows(Parse(
            $$"""[{"txid":"{{Txid('d')}}","vout":0,"status":{"block_height":5} } ]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock,
            "A status object that carries no confirmed flag must fail closed. Reading 'not false' "
            + "as confirmed would treat an unrecognised reply shape as spendable.");
    }

    [Fact]
    public void Esplora_TreatsANonBooleanConfirmedAsUnconfirmed()
    {
        var rows = EsploraHttpClient.ReadUnspentRows(Parse(
            $$"""[{"txid":"{{Txid('d')}}","vout":0,"status":{"confirmed":null} } ]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock);
    }

    [Fact]
    public void Esplora_TreatsANonObjectStatusAsUnconfirmedRatherThanThrowing()
    {
        var rows = EsploraHttpClient.ReadUnspentRows(Parse(
            $$"""[{"txid":"{{Txid('d')}}","vout":0,"status":"confirmed"}]"""));

        Assert.Single(rows);
        Assert.False(rows[0].ConfirmedInABlock,
            "TryGetProperty throws on a non-object element, so the parser must check ValueKind "
            + "first; an indexer that returns an unexpected shape must produce a refusal, not an "
            + "unactionable InvalidOperationException in the operator's browser.");
    }
}
