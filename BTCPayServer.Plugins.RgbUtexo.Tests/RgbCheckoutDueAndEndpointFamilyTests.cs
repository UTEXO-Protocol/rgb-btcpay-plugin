using System.Net;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCheckoutDueAndEndpointFamilyTests
{
    [Theory]
    [InlineData(1230000000000000000L, 18, "1.230000000000000000")]
    [InlineData(999999999999999999L, 18, "0.999999999999999999")]
    [InlineData(4000L, 2, "40.00")]
    [InlineData(100L, 0, "100")]
    public void AmountDueIsFormattedExactly(long units, int precision, string expected)
    {
        Assert.Equal(expected, RGBCheckoutModelExtension.FormatAssetUnitsExactly(units, precision));
    }

    [Fact]
    public void AmountDueNeverUnderstatesWhatTheInvoiceRequires()
    {
        const long units = 1230000000000000000L;
        var viaDouble = (units / Math.Pow(10, 18)).ToString(
            "F18", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal("1.229999999999999982", viaDouble);
        Assert.NotEqual(viaDouble, RGBCheckoutModelExtension.FormatAssetUnitsExactly(units, 18));
    }

    [Theory]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b:1::c0a8:1")]
    [InlineData("2002:c0a8:0101::1")]
    [InlineData("2001:0:1234::1")]
    [InlineData("::192.168.1.1")]
    public void IPv6AddressesThatEmbedOrTranslateToIPv4_AreRefused(string address)
    {
        Assert.True(
            TransportEndpointValidator.IsIPv4TranslationOrTunnelPrefix(
                IPAddress.Parse(address).GetAddressBytes()),
            $"{address} reaches an IPv4 destination this validator cannot inspect, so the private-address "
            + "checks it performs on the IPv6 form say nothing about where the request actually lands");
    }

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:80f::200e")]
    public void OrdinaryGlobalIPv6Addresses_AreNotRefusedByTheTranslationCheck(string address)
    {
        Assert.False(
            TransportEndpointValidator.IsIPv4TranslationOrTunnelPrefix(
                IPAddress.Parse(address).GetAddressBytes()),
            $"{address} is a normal globally routable IPv6 address; refusing it would reject legitimate "
            + "RGB proxies");
    }
}
