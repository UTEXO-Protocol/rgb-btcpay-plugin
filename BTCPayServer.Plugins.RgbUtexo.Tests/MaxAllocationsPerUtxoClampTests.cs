using BTCPayServer.Plugins.RgbUtexo.Services;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class MaxAllocationsPerUtxoClampTests
{
    [Theory]
    [InlineData(1000000, 50)]
    [InlineData(51, 50)]
    [InlineData(50, 50)]
    [InlineData(25, 25)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    public void PositiveValues_AreClampedToUpperBound(int requested, int expected) =>
        Assert.Equal(expected, RGBWalletService.ResolveAllocationsPerUtxo(requested));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveValues_FallBackToDefault(int requested) =>
        Assert.Equal(10, RGBWalletService.ResolveAllocationsPerUtxo(requested));

    [Fact]
    public void Null_FallsBackToDefault() =>
        Assert.Equal(10, RGBWalletService.ResolveAllocationsPerUtxo(null));

    [Fact]
    public void ExtremeNonNull_NeverExceedsLimit() =>
        Assert.Equal(50, RGBWalletService.ResolveAllocationsPerUtxo(int.MaxValue));
}
