using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class ConfirmedBtcInputSelectionTests
{
    const float FeeRate = 2.0f;

    static ConfirmedBtcInputSelection.Candidate Utxo(long sats, int vout) =>
        new(new Outpoint(new string((char)('a' + vout), 64), vout), sats);

    static List<ConfirmedBtcInputSelection.Candidate> LargestFirst(params long[] sats) =>
        sats.Select((s, i) => Utxo(s, i)).OrderByDescending(c => c.BtcAmount).ToList();

    static Func<Outpoint, CancellationToken, Task<bool?>> Says(
        params bool?[] answersInOrder)
    {
        var next = 0;
        return (_, _) => Task.FromResult(answersInOrder[next++]);
    }

    [Fact]
    public async Task TheLargestCandidateBeingUnconfirmedDoesNotStopSmallerConfirmedOnesBeingUsed()
    {
        var walk = await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            LargestFirst(100_000, 60_000, 50_000), 55_000, FeeRate,
            Says(false, true), CancellationToken.None);

        Assert.Single(walk.Confirmed);
        Assert.Equal(60_000, walk.Confirmed[0].BtcAmount);
        Assert.Equal(100_000, walk.UnconfirmedSatsSkipped);
    }

    [Fact]
    public async Task AnUnconfirmedCandidateIsNeverPlacedAmongTheConfirmedOnes()
    {
        var walk = await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            LargestFirst(100_000), 10_000, FeeRate, Says(false), CancellationToken.None);

        Assert.Empty(walk.Confirmed);
        Assert.Equal(100_000, walk.UnconfirmedSatsSkipped);
    }

    [Fact]
    public async Task AnOutpointTheIndexerDoesNotListIsExcludedWithoutBeingCalledPending()
    {
        var walk = await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            LargestFirst(100_000), 10_000, FeeRate, Says((bool?)null), CancellationToken.None);

        Assert.Empty(walk.Confirmed);
        Assert.Equal(0, walk.UnconfirmedSatsSkipped);
    }

    [Fact]
    public async Task AnIndexerFailurePropagatesRatherThanLookingLikeMissingFunds()
    {
        var boom = new InvalidOperationException("indexer unreachable");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
                LargestFirst(100_000), 10_000, FeeRate,
                (_, _) => Task.FromException<bool?>(boom), CancellationToken.None));

        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task TheWalkStopsAskingOnceTheConfirmedTotalCoversAmountAndFee()
    {
        var asked = 0;

        var walk = await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            LargestFirst(100_000, 90_000, 80_000), 50_000, FeeRate,
            (_, _) => { asked++; return Task.FromResult<bool?>(true); }, CancellationToken.None);

        Assert.Equal(1, asked);
        Assert.Equal(1, walk.Examined);
        Assert.Single(walk.Confirmed);
    }

    [Fact]
    public async Task ASendAllRequestExaminesEveryCandidate()
    {
        var asked = 0;

        await ConfirmedBtcInputSelection.WalkConfirmedCandidatesAsync(
            LargestFirst(40_000, 20_000), 60_000, FeeRate,
            (_, _) => { asked++; return Task.FromResult<bool?>(true); }, CancellationToken.None);

        Assert.Equal(2, asked);
    }

    [Fact]
    public void ConfirmationOf_ReturnsNullWhenTheOutpointIsAbsentFromItsScriptsRows()
    {
        var target = new Outpoint(new string('a', 64), 0);
        var rows = new List<UnspentWithConfirmation>
        {
            new(new Outpoint(new string('b', 64), 0), true)
        };

        Assert.Null(ConfirmedBtcInputSelection.ConfirmationOf(target, rows));
    }

    [Fact]
    public void ConfirmationOf_DoesNotAnswerForADifferentOutputOfTheSameTransaction()
    {
        var target = new Outpoint(new string('a', 64), 1);
        var rows = new List<UnspentWithConfirmation>
        {
            new(new Outpoint(new string('a', 64), 0), true)
        };

        Assert.True(ConfirmedBtcInputSelection.ConfirmationOf(target, rows) is null,
            "Vout 1 is absent from its script's unspent rows, so it is spent or otherwise gone. "
            + "Matching on txid alone would hand back vout 0's confirmation state and let a "
            + "vanished output be selected as spendable.");
    }

    [Fact]
    public void CoversAmountAndFee_BudgetsForAChangeOutput()
    {
        var oneOutputFee = RGBWalletService.EstimateTaprootFee(1, 1, FeeRate);

        Assert.False(
            ConfirmedBtcInputSelection.CoversAmountAndFee(50_000 + oneOutputFee, 1, 50_000, FeeRate),
            "A total that covers only a one-output fee must not satisfy the stop predicate: the "
            + "transaction this path builds has a change output too, and stopping early there "
            + "would hand ChooseOrRefuse a set it then refuses as insufficient.");

        Assert.True(
            ConfirmedBtcInputSelection.CoversAmountAndFee(
                50_000 + RGBWalletService.EstimateTaprootFee(1, 2, FeeRate), 1, 50_000, FeeRate));
    }

    [Fact]
    public void RefusesWhenNoCandidateIsConfirmed_AndSaysSoRatherThanBlamingRgbAllocations()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.ChooseOrRefuse([], 10_000, FeeRate, 250_000));

        Assert.Contains("None of the Bitcoin outputs", ex.Message);
        Assert.Contains(250_000.ToString("N0"), ex.Message);
        Assert.DoesNotContain("RGB allocations", ex.Message);
    }

    [Fact]
    public void RefusalWithNothingPending_DoesNotClaimAPendingAmount()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.ChooseOrRefuse([], 10_000, FeeRate, 0));

        Assert.Contains("None of the Bitcoin outputs", ex.Message);
        Assert.DoesNotContain("becomes spendable", ex.Message);
        Assert.DoesNotContain("sits in outputs", ex.Message);
    }

    [Fact]
    public void SelectsLargestFirstAndStopsAsSoonAsAmountAndFeeAreCovered()
    {
        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(
            LargestFirst(100_000, 90_000, 80_000), 50_000, FeeRate, 0);

        Assert.Single(choice.Inputs);
        Assert.Equal(100_000, choice.TotalInput);
        Assert.Equal(50_000, choice.AmountSats);
    }

    [Fact]
    public void AccumulatesFurtherInputsWhenOneCannotCoverAmountAndFee()
    {
        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(
            LargestFirst(30_000, 30_000, 30_000), 55_000, FeeRate, 0);

        Assert.Equal(2, choice.Inputs.Count);
        Assert.Equal(60_000, choice.TotalInput);
    }

    [Fact]
    public void SendAll_DeductsTheFeeFromTheAmountRatherThanRefusing()
    {
        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(
            LargestFirst(40_000, 20_000), 60_000, FeeRate, 0);

        Assert.Equal(2, choice.Inputs.Count);
        Assert.Equal(60_000, choice.TotalInput);
        Assert.True(choice.AmountSats < 60_000,
            "Send-all must deduct the fee from the amount; leaving it equal to the input total "
            + "would build a transaction with a zero or negative fee.");
        Assert.False(choice.HasChange);
        Assert.Equal(60_000 - choice.AmountSats, choice.Fee);
    }

    [Fact]
    public void SendAll_RefusesWhenTheAmountAfterFeeWouldBeDust()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.ChooseOrRefuse(LargestFirst(600), 600, FeeRate, 0));

        Assert.Contains("546", ex.Message);
    }

    [Fact]
    public void RefusesWhenConfirmedFundsCannotCoverAmountPlusFee_AndSuggestsAnAmountThatIsPayable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.ChooseOrRefuse(LargestFirst(20_000), 19_990, FeeRate, 700_000));

        Assert.Contains("confirmed", ex.Message);
        Assert.Contains(700_000.ToString("N0"), ex.Message);

        var suggested = 20_000 - RGBWalletService.EstimateTaprootFee(1, 1, FeeRate);
        Assert.Contains($"Try {suggested:N0} sats or less", ex.Message);

        var retry = ConfirmedBtcInputSelection.ChooseOrRefuse(
            LargestFirst(20_000), suggested, FeeRate, 700_000);
        Assert.True(retry.AmountSats == suggested,
            "The amount a refusal tells the operator to try must actually go through. Advertising a "
            + "figure the very next attempt would refuse again is a false statement to the operator.");
    }

    [Fact]
    public void RefusalSuggestsNoAmountWhenNoAmountIsPayable()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConfirmedBtcInputSelection.ChooseOrRefuse(LargestFirst(700), 100_000, FeeRate, 0));

        Assert.DoesNotContain("Try ", ex.Message);
        Assert.DoesNotContain("-", ex.Message);
        Assert.DoesNotContain("A further", ex.Message);
    }

    [Fact]
    public void ChangeBelowDustIsGivenToTheFeeRatherThanCreatingADustOutput()
    {
        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(LargestFirst(50_000), 49_600, FeeRate, 0);

        Assert.False(choice.HasChange);
        Assert.Equal(0, choice.Change);
        Assert.Equal(50_000 - choice.AmountSats, choice.Fee);
    }

    [Fact]
    public void ChangeAtOrAboveDustIsKeptAsAnOutput()
    {
        var choice = ConfirmedBtcInputSelection.ChooseOrRefuse(LargestFirst(200_000), 50_000, FeeRate, 0);

        Assert.True(choice.HasChange);
        Assert.True(choice.Change >= 546);
        Assert.Equal(200_000, choice.AmountSats + choice.Fee + choice.Change);
    }
}
