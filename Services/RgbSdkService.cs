using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RGB.Services;

public class RgbSdkService
{
    private readonly INodeJSService _node;
    private readonly ILogger<RgbSdkService> _log;
    private readonly RGBConfiguration _cfg;
    private readonly string _modulePath;
    
    private bool _init;
    private string? _fingerprint;
    static readonly SemaphoreSlim _lock = new(1, 1);

    public RgbSdkService(INodeJSService node, RGBConfiguration cfg, ILogger<RgbSdkService> log)
    {
        _node = node;
        _cfg = cfg;
        _log = log;
        _modulePath = FindWrapper();
    }
    
    string FindWrapper()
    {
        var dir = Path.GetDirectoryName(typeof(RgbSdkService).Assembly.Location) ?? ".";
        string[] paths = [
            Path.Combine(dir, "Scripts", "rgb-sdk-wrapper.js"),
            Path.Combine(dir, "..", "Scripts", "rgb-sdk-wrapper.js")
        ];
        
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                _log.LogDebug("SDK wrapper: {Path}", Path.GetFullPath(p));
                return Path.GetFullPath(p);
            }
        }
        
        return paths[0];
    }

    public async Task InitializeAsync(RGBWalletCredentials creds)
    {
        if (_init && _fingerprint == creds.MasterFingerprint) return;

        await _lock.WaitAsync();
        try
        {
            if (_init && _fingerprint == creds.MasterFingerprint) return;
            
            var config = new {
                XpubVanilla = creds.XpubVanilla,
                XpubColored = creds.XpubColored,
                MasterFingerprint = creds.MasterFingerprint,
                RgbNodeEndpoint = _cfg.RgbNodeUrl,
                Network = string.IsNullOrEmpty(creds.Network) ? _cfg.Network : creds.Network
            };
            
            var res = await InvokeAsync<SdkResult>("initWallet", config);
            if (!res.Success) throw new RgbSdkException($"Init failed: {res.Error}");

            _init = true;
            _fingerprint = creds.MasterFingerprint;
        }
        finally { _lock.Release(); }
    }

    public async Task<string> SignPsbtAsync(string psbt)
    {
        EnsureInit();
        var r = await InvokeAsync<SignPsbtResult>("signPsbt", psbt);
        return r.SignedPsbt ?? throw new RgbSdkException("got empty psbt back");
    }

    public async Task<int> CreateUtxosAsync(int count = 5, int sats = 10000)
    {
        EnsureInit();
        return (await InvokeAsync<CreateUtxosResult>("createUtxos", count, sats)).UtxosCreated;
    }

    public async Task<string?> IssueAssetNiaAsync(string ticker, string name, List<long> amounts, int precision = 0)
    {
        EnsureInit();
        var r = await InvokeAsync<IssueAssetResult>("issueAssetNia", ticker, name, amounts, precision);
        return r.AssetId;
    }

    public Task<string?> SendBtcAsync(string addr, long sats, int feeRate = 2)
    {
        EnsureInit();
        return InvokeAsync<SendResult>("sendBtc", addr, sats, feeRate).ContinueWith(t => t.Result.Txid);
    }

    public async Task<string?> SendRgbAsync(string invoice, long amt, string assetId)
    {
        EnsureInit();
        return (await InvokeAsync<SendResult>("sendRgb", invoice, amt, assetId)).Txid;
    }

    public async Task<SdkStatusResult?> GetStatusAsync()
    {
        try
        {
            return await _node.InvokeFromFileAsync<SdkStatusResult>(_modulePath, "getStatus");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetStatus failed");
            return null;
        }
    }

    public async Task DisposeAsync()
    {
        if (!_init) return;
        try
        {
            await _node.InvokeFromFileAsync<SdkResult>(_modulePath, "disposeWallet");
            _init = false;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "DisposeWallet failed");
        }
    }

    async Task<T> InvokeAsync<T>(string fn, params object[] args) where T : SdkResult, new()
    {
        try
        {
            var r = await _node.InvokeFromFileAsync<T>(_modulePath, fn, args: args);
            if (r is null) return new T { Success = false, Error = "null from sdk" };
            if (!r.Success) throw new RgbSdkException(r.Error ?? $"{fn} failed");
            return r;
        }
        catch (RgbSdkException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Fn}", fn);
            throw new RgbSdkException($"{fn}: {ex.Message}", ex);
        }
    }
    
    void EnsureInit()
    {
        if (!_init) throw new InvalidOperationException("wallet not initialized - call InitializeAsync");
    }
}

public class RgbSdkException(string msg, Exception? inner = null) : Exception(msg, inner);

public class SdkResult { public bool Success; public string? Error; }
public class SignPsbtResult : SdkResult { public string? SignedPsbt; }
public class CreateUtxosResult : SdkResult { public int UtxosCreated; }
public class IssueAssetResult : SdkResult { public string? AssetId; }
public class SendResult : SdkResult { public string? Txid; }
public class SdkStatusResult { public bool SdkLoaded, WalletInitialized, Connected; }
