using System.Globalization;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Services.Rates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbCurrencyDataProvider : CurrencyDataProvider
{
    static readonly Lazy<HashSet<string>> ReservedCurrencyCodes = new(() =>
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTC", "SATS", "RGB" };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try { codes.Add(new RegionInfo(culture.LCID).ISOCurrencySymbol); }
            catch { }
        }
        return codes;
    });

    readonly RGBPluginDbContextFactory _dbFactory;
    readonly ILogger<RgbCurrencyDataProvider> _log;

    public RgbCurrencyDataProvider(RGBPluginDbContextFactory dbFactory, ILogger<RgbCurrencyDataProvider> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    internal static CurrencyData[] BuildCurrencies(
        IReadOnlyList<RGBAsset> assets,
        Func<string, string> pricingCode,
        Action<string, string, string>? onCollision = null,
        Action<string, string>? onUnparseableAssetId = null)
    {
        var currencies = new List<CurrencyData>
        {
            new() { Code = "RGB", Name = "RGB Token", Divisibility = 0, Crypto = true }
        };

        var assetsByCanonicalId = new Dictionary<string, RGBAsset>(StringComparer.Ordinal);
        var seenTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            // IsNullOrWhiteSpace, not IsNullOrEmpty: RgbPricingCode.For throws on whitespace. Any
            // other undecodable id is caught per row below, for the same reason: not instance-wide.
            if (string.IsNullOrWhiteSpace(asset.AssetId)) continue;

            // RGB_Assets is keyed (WalletId, AssetId): one contract in two wallets is one asset.
            // Prefix and separator variants are the same ContractId according to RGB Core.
            try
            {
                assetsByCanonicalId.TryAdd(RgbPricingCode.CanonicalizeAssetId(asset.AssetId), asset);
            }
            catch (ArgumentException)
            {
                onUnparseableAssetId?.Invoke(asset.WalletId, asset.AssetId);
                continue;
            }

            if (string.IsNullOrEmpty(asset.Ticker)) continue;
            var ticker = asset.Ticker.ToUpperInvariant();
            // A ticker shaped like a pricing code could shadow another contract's entry.
            if (RgbPricingCode.IsPricingCode(ticker)) continue;
            if (ReservedCurrencyCodes.Value.Contains(ticker)) continue;
            if (!seenTickers.Add(ticker)) continue;

            currencies.Add(new CurrencyData
            {
                Code = ticker, Name = asset.Name, Divisibility = asset.Precision, Crypto = true
            });
        }

        foreach (var codeGroup in assetsByCanonicalId.Values
                     .Select(asset => (Asset: asset, Code: pricingCode(asset.AssetId)))
                     .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            var owners = codeGroup.ToList();
            if (owners.Count > 1)
            {
                var first = owners[0];
                foreach (var other in owners.Skip(1))
                    onCollision?.Invoke(first.Code, first.Asset.AssetId, other.Asset.AssetId);
                continue;
            }

            var owner = owners[0];
            currencies.Add(new CurrencyData
            {
                Code = owner.Code,
                Name = DescribeAsset(owner.Asset, owner.Code),
                Divisibility = owner.Asset.Precision,
                Crypto = true
            });
        }

        return currencies.ToArray();
    }

    static string DescribeAsset(RGBAsset asset, string code) =>
        (asset.Ticker, asset.Name) switch
        {
            ("", "") => code,
            (var t, "") => t,
            ("", var n) => n,
            var (t, n) => $"{t} — {n}"
        };

    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        try
        {
            await using var ctx = _dbFactory.CreateContext();
            var assets = await ctx.RGBAssets.ToListAsync(cancellationToken);
            return BuildCurrencies(assets, RgbPricingCode.For,
                (code, owner, other) => _log.LogCritical(
                    "RGB pricing code {Code} collides between assets {Owner} and {Other}; neither contract will be priced",
                    code, owner, other),
                (walletId, assetId) => _log.LogWarning(
                    "RGB asset {AssetId} in wallet {WalletId} is not a decodable RGB contract id; only that asset loses its pricing code, every other contract is still priced",
                    assetId, walletId));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load RGB asset currencies from DB");
            return [new CurrencyData { Code = "RGB", Name = "RGB Token", Divisibility = 0, Crypto = true }];
        }
    }
}
