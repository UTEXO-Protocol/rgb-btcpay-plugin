namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal sealed class RgbAutomaticReplenishmentNotAuthorizedException : InvalidOperationException
{
    internal RgbAutomaticReplenishmentNotAuthorizedException(string message) : base(message) { }
}
