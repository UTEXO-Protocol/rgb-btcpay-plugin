using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbUnsignedAssetSupplyTests
{
    const string PoisonAssetId = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";
    const string HonestAssetId = "rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-TMsGMSA";

    const string UnsignedMaximumAsDecimal = "18446744073709551615";
    const string OneAboveSignedMaximumAsDecimal = "9223372036854775808";

    static string ListAssetsJson(string assetId, string issuedSupply, string settled, string future, string spendable)
        => $$"""
            {"nia":[{"asset_id":"{{assetId}}","ticker":"PSN","name":"poison supply",
              "details":null,"precision":0,"issued_supply":{{issuedSupply}},
              "timestamp":1,"added_at":1,
              "balance":{"settled":{{settled}},"future":{{future}},"spendable":{{spendable}}},
              "media":null}]}
            """;

    [Fact]
    public void IssuedSupplyAtTheUnsignedMaximum_IsParsedExactlyRatherThanRefusingTheWholeList()
    {
        var assets = RgbLibService.InterpretListAssets(
            ListAssetsJson(PoisonAssetId, UnsignedMaximumAsDecimal, "1", "1", "1"));

        var asset = Assert.Single(assets);
        Assert.Equal(UnsignedMaximumAsDecimal, asset.IssuedSupply.ToString());
    }

    [Fact]
    public void BalancesOneAboveTheSignedMaximum_AreParsedExactlyRatherThanRefusingTheWholeList()
    {
        var assets = RgbLibService.InterpretListAssets(ListAssetsJson(
            PoisonAssetId, UnsignedMaximumAsDecimal,
            OneAboveSignedMaximumAsDecimal, OneAboveSignedMaximumAsDecimal, OneAboveSignedMaximumAsDecimal));

        var asset = Assert.Single(assets);
        Assert.Equal(OneAboveSignedMaximumAsDecimal, asset.Balance.ToString());
        Assert.Equal(OneAboveSignedMaximumAsDecimal, asset.FutureBalance.ToString());
        Assert.Equal(OneAboveSignedMaximumAsDecimal, asset.SpendableBalance.ToString());
    }

    [Fact]
    public void OneCounterpartyIssuedOversizedContract_DoesNotHideTheMerchantsOwnAssetFromTheSendPath()
    {
        var listAssetsJson = $$"""
            {"nia":[
              {"asset_id":"{{PoisonAssetId}}","ticker":"PSN","name":"poison supply","details":null,
               "precision":0,"issued_supply":{{UnsignedMaximumAsDecimal}},"timestamp":1,"added_at":1,
               "balance":{"settled":1,"future":1,"spendable":1},"media":null},
              {"asset_id":"{{HonestAssetId}}","ticker":"USDT","name":"Tether","details":null,
               "precision":0,"issued_supply":1000000,"timestamp":1,"added_at":1,
               "balance":{"settled":10000,"future":10000,"spendable":10000},"media":null}]}
            """;

        var assets = RgbLibService.InterpretListAssets(listAssetsJson);

        Assert.Equal(2, assets.Count);
        var invoice = new RgbInvoiceData
        {
            RecipientId = "utxob:abc123",
            AssetId = HonestAssetId,
            Amount = 100,
            ExpirationTimestamp = 0,
            TransportEndpoints = ["rpc://proxy.example.com:3000/json-rpc"]
        };

        var (resolvedAssetId, asset) = RGBWalletService.ValidateSendAssetRequest(
            invoice, HonestAssetId, 100, assets);

        Assert.Equal(HonestAssetId, resolvedAssetId);
        Assert.Equal("10000", asset.SpendableBalance.ToString());
    }

    [Fact]
    public void SpendableBalanceAboveTheSignedMaximum_StillAuthorisesAnOrdinarySizedSend()
    {
        var assets = RgbLibService.InterpretListAssets(ListAssetsJson(
            PoisonAssetId, UnsignedMaximumAsDecimal,
            UnsignedMaximumAsDecimal, UnsignedMaximumAsDecimal, UnsignedMaximumAsDecimal));

        var invoice = new RgbInvoiceData
        {
            RecipientId = "utxob:abc123",
            AssetId = PoisonAssetId,
            Amount = 7,
            ExpirationTimestamp = 0,
            TransportEndpoints = ["rpc://proxy.example.com:3000/json-rpc"]
        };

        var (_, asset) = RGBWalletService.ValidateSendAssetRequest(invoice, PoisonAssetId, 7, assets);

        Assert.Equal(UnsignedMaximumAsDecimal, asset.SpendableBalance.ToString());
    }

    [Fact]
    public void NegativeSendAmount_IsRefusedRatherThanReinterpretedAsAnEnormousUnsignedAmount()
    {
        var assets = RgbLibService.InterpretListAssets(
            ListAssetsJson(HonestAssetId, "1000000", "10000", "10000", "10000"));

        var invoice = new RgbInvoiceData
        {
            RecipientId = "utxob:abc123",
            AssetId = HonestAssetId,
            Amount = -5,
            ExpirationTimestamp = 0,
            TransportEndpoints = ["rpc://proxy.example.com:3000/json-rpc"]
        };

        var fault = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(invoice, HonestAssetId, -5, assets));
        Assert.Contains("Insufficient", fault.Message);

        var spendableLeavingNoHeadroomAboveTheReinterpretedNegative = RgbLibService.InterpretListAssets(
            ListAssetsJson(HonestAssetId, "1000000",
                UnsignedMaximumAsDecimal, UnsignedMaximumAsDecimal, UnsignedMaximumAsDecimal));
        var invoiceForMinusOne = new RgbInvoiceData
        {
            RecipientId = "utxob:abc123",
            AssetId = HonestAssetId,
            Amount = -1,
            ExpirationTimestamp = 0,
            TransportEndpoints = ["rpc://proxy.example.com:3000/json-rpc"]
        };

        var minusOneReinterpretsToExactlyTheUnsignedMaximum = Assert.Throws<InvalidOperationException>(
            () => RGBWalletService.ValidateSendAssetRequest(
                invoiceForMinusOne, HonestAssetId, -1, spendableLeavingNoHeadroomAboveTheReinterpretedNegative));
        Assert.Contains("Insufficient", minusOneReinterpretsToExactlyTheUnsignedMaximum.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PendingColumnsOnAnOversizedAsset_NeverUnderflowIntoAnEnormousPendingAmount()
    {
        var settledAboveFuture = RgbLibService.InterpretListAssets(ListAssetsJson(
            PoisonAssetId, UnsignedMaximumAsDecimal,
            UnsignedMaximumAsDecimal, OneAboveSignedMaximumAsDecimal, "0"))[0].ToViewModel();

        var futureAboveSettled = RgbLibService.InterpretListAssets(ListAssetsJson(
            PoisonAssetId, UnsignedMaximumAsDecimal,
            OneAboveSignedMaximumAsDecimal, UnsignedMaximumAsDecimal, "0"))[0].ToViewModel();

        Assert.Equal("9223372036854775807", settledAboveFuture.PendingOutgoing.ToString());
        Assert.Equal("0", settledAboveFuture.PendingIncoming.ToString());
        Assert.Equal("0", futureAboveSettled.PendingOutgoing.ToString());
        Assert.Equal("9223372036854775807", futureAboveSettled.PendingIncoming.ToString());
    }

    [Fact]
    public void PersistedIssuedSupply_RoundTripsThroughTheBigintColumnWithoutLosingABit()
    {
        var factory = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=model_only;Username=none;Password=none"
        }));
        using var ctx = factory.CreateContext();

        var supply = ctx.Model.FindEntityType(typeof(RGBAsset))!.FindProperty(nameof(RGBAsset.IssuedSupply))!;
        var converter = supply.GetValueConverter();

        Assert.True(converter != null,
            "without a value converter EF would have to narrow the u64 issued supply into the bigint "
            + "column itself, and every supply above long.MaxValue would be truncated or rejected on save");

        foreach (var probe in new[] { "0", "1000000", "9223372036854775807", OneAboveSignedMaximumAsDecimal, UnsignedMaximumAsDecimal })
        {
            var original = ulong.Parse(probe);
            var stored = converter!.ConvertToProvider(original);
            var restored = converter.ConvertFromProvider(stored);
            Assert.Equal(probe, restored!.ToString());
        }
    }

    [Fact]
    public void EntityAndDeserialisedSupplyShareTheSameUnsignedWidth_SoNoAssignmentBetweenThemCanNarrow()
    {
        Assert.Equal(typeof(ulong), typeof(RgbAsset).GetProperty(nameof(RgbAsset.IssuedSupply))!.PropertyType);
        Assert.Equal(typeof(ulong), typeof(RgbAsset).GetProperty(nameof(RgbAsset.Balance))!.PropertyType);
        Assert.Equal(typeof(ulong), typeof(RgbAsset).GetProperty(nameof(RgbAsset.FutureBalance))!.PropertyType);
        Assert.Equal(typeof(ulong), typeof(RgbAsset).GetProperty(nameof(RgbAsset.SpendableBalance))!.PropertyType);
        Assert.Equal(typeof(ulong), typeof(RGBAsset).GetProperty(nameof(RGBAsset.IssuedSupply))!.PropertyType);
    }
}
