using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbVanillaReservationInspectorTests
{
    static RgbVanillaReservedOutpoint Reserved(string txid, int vout = 0) =>
        new(txid, vout, "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

    static Outpoint Unspent(string txid, int vout = 0) => new(txid, vout);

    [Fact]
    public void NoRows_IsClean()
    {
        var report = RgbVanillaReservationInspector.Classify([], [Unspent("aa")]);
        Assert.Equal(RgbVanillaReservationState.Clean, report.State);
        Assert.Empty(report.Reserved);
        Assert.Empty(report.StillUnspent);
    }

    [Fact]
    public void EveryReservedOutpointSpent_IsInertAlreadyRecovered()
    {
        var report = RgbVanillaReservationInspector.Classify(
            [Reserved("aa"), Reserved("bb")],
            [Unspent("cc"), Unspent("dd")]);

        Assert.True(report.State == RgbVanillaReservationState.InertAlreadyRecovered,
            "rows whose reserved outpoints are all spent constrain nothing: TxBuilder.unspendable() "
            + "filters BDK's CURRENT utxo set, so listing an outpoint no longer in that set excludes "
            + $"nothing. Reporting this as a fault sends the operator on repeated fee-paying self-sends "
            + $"chasing a warning that can never clear. Reported {report.State}.");
        Assert.Empty(report.StillUnspent);
    }

    [Fact]
    public void AtLeastOneReservedOutpointStillUnspent_IsLiveAndConstraining()
    {
        var report = RgbVanillaReservationInspector.Classify(
            [Reserved("aa"), Reserved("bb")],
            [Unspent("bb")]);

        Assert.True(report.State == RgbVanillaReservationState.LiveAndConstraining,
            "one still-unspent reserved outpoint is enough to exclude it from coin selection, so a "
            + $"partial spend is NOT recovery. Reported {report.State}.");
        Assert.Single(report.StillUnspent);
        Assert.Equal("bb", report.StillUnspent[0].Txid);
    }

    [Fact]
    public void SpentReservedOutpointWhoseSiblingOutputIsStillUnspent_IsInertAlreadyRecovered()
    {
        var report = RgbVanillaReservationInspector.Classify(
            [Reserved("aa", 1)],
            [Unspent("aa", 0)]);

        Assert.True(report.State == RgbVanillaReservationState.InertAlreadyRecovered,
            "a reserved outpoint is identified by txid AND vout. Vout 1 is spent while vout 0 of the same "
            + "transaction is not, so the reservation constrains nothing; matching on txid alone would "
            + $"report a permanent lockout that no self-send can ever clear. Reported {report.State}.");
        Assert.Empty(report.StillUnspent);
    }

    [Fact]
    public void SpentnessOracleUnavailable_IsUnknownAndFailsOpen()
    {
        var report = RgbVanillaReservationInspector.Classify([Reserved("aa")], null);

        Assert.True(report.State == RgbVanillaReservationState.Unknown,
            "with no unspent set there is no evidence either way, and this filter feeds human "
            + "diagnostics rather than a signing boundary, so it must fail OPEN. Reporting 'locked' here "
            + "would be the permanent false warning that provokes repeated fee-paying self-sends; "
            + $"reporting 'recovered' would hide a real lockout. Reported {report.State}.");
        Assert.Single(report.Reserved);
        Assert.Empty(report.StillUnspent);
    }

    [Fact]
    public async Task MissingDatabaseFile_ReadsNoRows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rgb-absent-{Guid.NewGuid():N}", "rgb_lib_db");
        Assert.Empty(await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(path));
    }
}
