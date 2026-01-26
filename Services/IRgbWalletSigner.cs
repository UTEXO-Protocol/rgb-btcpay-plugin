using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public interface IRgbWalletSigner : IDisposable
{
    Task<string> SignPsbtAsync(string psbt, Network network, CancellationToken cancellationToken = default);
    string MasterFingerprint { get; }
    string XpubVanilla { get; }
    string XpubColored { get; }
    bool IsDisposed { get; }
}

public interface IRgbWalletSignerProvider
{
    Task<bool> CanHandleAsync(string walletId, CancellationToken cancellationToken = default);
    Task<IRgbWalletSigner?> GetSignerAsync(string walletId, CancellationToken cancellationToken = default);
}
