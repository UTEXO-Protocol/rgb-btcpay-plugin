namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbWalletConstructionException : InvalidOperationException
{
    public RgbWalletConstructionException(string message) : base(message) { }
}
