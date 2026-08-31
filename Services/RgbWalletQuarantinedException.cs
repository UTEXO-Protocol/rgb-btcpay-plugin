namespace BTCPayServer.Plugins.RgbUtexo.Services;

// Derives from InvalidOperationException so existing handlers and message rendering behave unchanged, while
// RGBInvoiceListener can discriminate it from a genuine replenishment failure: a quarantine typically clears
// on the next listener refresh, so stamping the cooldown would turn a seconds-long condition into a
// thirty-minute doubling backoff.
public class RgbWalletQuarantinedException : InvalidOperationException
{
    public RgbWalletQuarantinedException(string message) : base(message) { }
    public RgbWalletQuarantinedException(string message, Exception inner) : base(message, inner) { }
}

// Thrown only after NativeSendProcessRunner has returned a result whose ChildReaped flag is true.
// Recovery may therefore inspect and safely fail an authoritative Initiated row without racing a helper.
internal sealed class NativeSendReapedFailureException : InvalidOperationException
{
    internal NativeSendReapedFailureException(string message) : base(message) { }
}

// Deliberately NOT a NativeSendReapedFailureException: that type means "the helper definitely did not
// do the work", and a result the parent could not read in full carries no such claim. Recovery must
// treat it as indeterminate, so it must not be caught by the reaped-failure handler.
internal sealed class NativeSendOutputTruncatedException : InvalidOperationException
{
    internal NativeSendOutputTruncatedException(string operation, int outputCapChars)
        : base($"RGB {operation} produced more than the {outputCapChars}-character result cap, "
            + "so its result could not be read in full and must not be treated as a value") { }
}
