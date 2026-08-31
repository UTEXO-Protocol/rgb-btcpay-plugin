using BTCPayServer.Data;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RateHandlingTests
{
    // The five tests this file used to hold all round-tripped AllowOneToOneRateFallback, the opt-in
    // that returned rate 1 without inspecting the invoice currency (audit finding E). The flag is
    // gone with no replacement; what still needs proving is that a store which persisted it keeps
    // loading, and grants nothing by having done so.
    [Fact]
    public void LegacyConfigWithFallbackFlag_DeserializesAndGrantsNothing()
    {
        var json = JObject.Parse("""{"walletId":"w1","defaultAssetId":"a1","allowOneToOneRateFallback":true}""");
        var config = json.ToObject<RGBPaymentMethodConfig>(BlobSerializer.CreateSerializer().Serializer);

        Assert.NotNull(config);
        Assert.Equal("w1", config!.WalletId);
        Assert.Null(config.GetType().GetProperty("AllowOneToOneRateFallback"));
    }
}
