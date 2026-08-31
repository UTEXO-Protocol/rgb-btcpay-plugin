namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class ConfirmedBtcInputSelection
{
    public const long DustLimitSats = 546;

    public sealed record Candidate(Outpoint Outpoint, long BtcAmount);

    public sealed record WalkResult(
        IReadOnlyList<Candidate> Confirmed,
        long UnconfirmedSatsSkipped,
        int Examined);

    public sealed record Choice(
        IReadOnlyList<Candidate> Inputs,
        long TotalInput,
        long AmountSats,
        long Fee,
        long Change,
        bool HasChange);

    public static bool CoversAmountAndFee(long runningTotal, int inputCount, long amountSats, float feeRate)
        => runningTotal >= amountSats + RGBWalletService.EstimateTaprootFee(inputCount, 2, feeRate);

    public static bool? ConfirmationOf(Outpoint outpoint, IReadOnlyList<UnspentWithConfirmation> rowsForItsScript)
    {
        foreach (var row in rowsForItsScript)
            if (row.Outpoint.Txid == outpoint.Txid && row.Outpoint.Vout == outpoint.Vout)
                return row.ConfirmedInABlock;
        return null;
    }

    public static async Task<WalkResult> WalkConfirmedCandidatesAsync(
        IReadOnlyList<Candidate> largestFirst,
        long amountSats,
        float feeRate,
        Func<Outpoint, CancellationToken, Task<bool?>> confirmationOf,
        CancellationToken ct)
    {
        var confirmed = new List<Candidate>();
        long confirmedTotal = 0;
        long unconfirmedSatsSkipped = 0;
        var examined = 0;

        foreach (var candidate in largestFirst)
        {
            if (CoversAmountAndFee(confirmedTotal, confirmed.Count, amountSats, feeRate))
                break;

            examined++;
            var state = await confirmationOf(candidate.Outpoint, ct);
            if (state is null)
                continue;

            if (state.Value)
            {
                confirmed.Add(candidate);
                confirmedTotal += candidate.BtcAmount;
            }
            else
            {
                unconfirmedSatsSkipped += candidate.BtcAmount;
            }
        }

        return new WalkResult(confirmed, unconfirmedSatsSkipped, examined);
    }

    public static Choice ChooseOrRefuse(
        IReadOnlyList<Candidate> confirmedLargestFirst,
        long requestedAmountSats,
        float feeRate,
        long unconfirmedSatsSkipped)
    {
        if (confirmedLargestFirst.Count == 0)
            throw new InvalidOperationException(NothingIsConfirmedYet(unconfirmedSatsSkipped));

        var selected = new List<Candidate>();
        long totalInput = 0;
        foreach (var candidate in confirmedLargestFirst)
        {
            selected.Add(candidate);
            totalInput += candidate.BtcAmount;
            if (CoversAmountAndFee(totalInput, selected.Count, requestedAmountSats, feeRate))
                break;
        }

        var amountSats = requestedAmountSats;
        var minFee = RGBWalletService.EstimateTaprootFee(selected.Count, 1, feeRate);
        if (amountSats == totalInput)
        {
            amountSats = totalInput - minFee;
            if (amountSats < DustLimitSats)
                throw new InvalidOperationException(
                    $"Amount after fee would be below dust limit ({DustLimitSats} sats)");
        }
        else if (totalInput < amountSats + minFee)
        {
            throw new InvalidOperationException(
                ConfirmedFundsFallShort(totalInput, minFee, unconfirmedSatsSkipped));
        }

        var fee = RGBWalletService.EstimateTaprootFee(selected.Count, 2, feeRate);
        var change = totalInput - amountSats - fee;
        var hasChange = change >= DustLimitSats;
        if (!hasChange)
        {
            fee = totalInput - amountSats;
            change = 0;
        }

        return new Choice(selected, totalInput, amountSats, fee, change, hasChange);
    }

    internal static string StillWaitingToBeMined(long unconfirmedSatsSkipped) =>
        unconfirmedSatsSkipped <= 0
            ? ""
            : $" A further {unconfirmedSatsSkipped:N0} sats sits in outputs this send examined that "
              + "have not been mined into a block yet, and becomes spendable once they are.";

    internal static string NothingIsConfirmedYet(long unconfirmedSatsSkipped) =>
        "None of the Bitcoin outputs this send examined is confirmed, so there is nothing it can spend."
        + (unconfirmedSatsSkipped > 0
            ? $" {unconfirmedSatsSkipped:N0} sats of them have not been mined into a block yet, and "
              + "that amount becomes spendable once they are. This server will not spend an unmined "
              + "output, because the transaction that created it can still be replaced, which would "
              + "leave this payment unable to confirm."
            : "");

    internal static string ConfirmedFundsFallShort(
        long totalInput, long minFee, long unconfirmedSatsSkipped)
    {
        var maxSendable = totalInput - minFee;
        if (maxSendable < DustLimitSats)
            return "Insufficient confirmed funds. This send examined this wallet's largest Bitcoin "
                 + $"outputs and found {totalInput:N0} sats confirmed, and the network fee for "
                 + $"spending them is about {minFee:N0} sats, which leaves less than the "
                 + $"{DustLimitSats} sat minimum this server will send."
                 + StillWaitingToBeMined(unconfirmedSatsSkipped);

        return "Insufficient confirmed funds after fee. This send examined this wallet's largest "
             + $"Bitcoin outputs and found {totalInput:N0} sats confirmed, and the network fee for "
             + $"spending them is about {minFee:N0} sats. Try {maxSendable:N0} sats or less."
             + StillWaitingToBeMined(unconfirmedSatsSkipped);
    }
}
