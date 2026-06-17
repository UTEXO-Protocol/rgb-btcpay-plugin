using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class PaymentWalletStoreOwnershipTests
{
    [Fact]
    public void WalletOwnedByStore_IsAllowed()
    {
        Assert.True(RGBPaymentMethodHandler.WalletBelongsToStore("store-1", "store-1"));
    }

    [Fact]
    public void WalletOwnedByDifferentStore_IsRejected()
    {
        Assert.False(RGBPaymentMethodHandler.WalletBelongsToStore("store-2", "store-1"));
    }

    [Theory]
    [InlineData(null, "store-1")]
    [InlineData("", "store-1")]
    [InlineData("store-1", null)]
    [InlineData("store-1", "")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void MissingEitherStoreId_FailsClosed(string? walletStoreId, string? expectedStoreId)
    {
        Assert.False(RGBPaymentMethodHandler.WalletBelongsToStore(walletStoreId, expectedStoreId));
    }
}
