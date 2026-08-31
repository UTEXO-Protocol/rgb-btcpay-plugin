namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

public static class RgbRestoreValidationGate
{
    public const string ConcurrentParentSideValidationRefusalMessage =
        "Another wallet restore is already being checked. Try again once it completes.";

    static readonly SemaphoreSlim ProcessWideParentSideValidationGate = new(1, 1);

    public static Task RunOneAtATimeOrRefuseAsync(Func<Task> parentSideValidation) =>
        RunOneAtATimeOrRefuseAsync(ProcessWideParentSideValidationGate, parentSideValidation);

    internal static async Task RunOneAtATimeOrRefuseAsync(SemaphoreSlim gate, Func<Task> parentSideValidation)
    {
        if (!await gate.WaitAsync(TimeSpan.Zero))
            throw new InvalidOperationException(ConcurrentParentSideValidationRefusalMessage);
        try
        {
            await parentSideValidation();
        }
        finally
        {
            gate.Release();
        }
    }
}
