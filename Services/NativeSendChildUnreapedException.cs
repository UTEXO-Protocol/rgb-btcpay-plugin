namespace BTCPayServer.Plugins.RgbUtexo.Services;

public sealed class NativeSendChildUnreapedException : Exception
{
    public NativeSendChildUnreapedException()
        : base("Native RGB wallet handle or send worker could not be confirmed terminated; wallet remains locked until restart")
    {
    }
}
