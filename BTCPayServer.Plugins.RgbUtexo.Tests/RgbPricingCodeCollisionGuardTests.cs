using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingCodeCollisionGuardTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";

    [Fact]
    public void DistinctCodes_AreUnambiguous()
    {
        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, AssetB], RgbPricingCode.For));
    }

    [Fact]
    public void EquivalentTextualForms_AreOneContractNotACollision()
    {
        var compact = AssetA[4..].Replace("-", "");

        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [compact], RgbPricingCode.For));
    }

    [Fact]
    public void SimulatedCollision_IsAmbiguous()
    {
        Assert.False(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, AssetB], _ => "RGB2" + new string('A', 64)));
    }

    [Fact]
    public void AnUndecodableStoredRow_LeavesEveryOtherContractPriced()
    {
        // Before the per-row guard this threw ArgumentException out of ConfigurePrompt, and BTCPay
        // turned that into a failed payment prompt for EVERY store on the instance.
        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, "not-a-contract-id", AssetB], RgbPricingCode.For));
    }

    [Fact]
    public void AnUndecodableStoredRow_DoesNotMaskARealCollisionBehindIt()
    {
        // The malformed row must be skipped, not end the scan: AssetB still collides.
        Assert.False(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, ["not-a-contract-id", AssetB], _ => "RGB2" + new string('A', 64)));
    }

    [Fact]
    public void AWhitespaceOnlyStoredRow_IsStillSkipped()
    {
        Assert.True(RgbPricingCodeCollisionGuard.IsUnambiguous(
            AssetA, [AssetA, "   ", AssetB], RgbPricingCode.For));
    }
}
