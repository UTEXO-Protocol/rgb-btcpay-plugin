namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal sealed class RgbColorableUtxoCeilingReachedException : InvalidOperationException
{
    public RgbColorableUtxoCeilingReachedException(string message) : base(message) { }
}
