using System.Text.RegularExpressions;
using BTCPayServer.Plugins.RgbUtexo.PaymentHandler;
using BTCPayServer.Rating;
using Xunit;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPricingCodeTests
{
    const string AssetA = "rgb:bGxsbGxs-bGxsbGx-sbGxsbG-xsbGxsb-GxsbGxs-bGxsbGw";
    const string AssetB = "rgb:ERERERER-ERERERE-RERERER-ERERERE-RERERER-ERERERE";
    const string AssetC = "rgb:IiIiIiIi-IiIiIiI-iIiIiIi-IiIiIiI-iIiIiIi-IiIiIiI";

    [Fact]
    public void For_IsDeterministic()
    {
        Assert.Equal(
            "RGB2793856B2399FB6EFC2FBC42A76A8C05825CAC8DA66855C0F368F5862EA0F3415",
            RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_MatchesShape()
    {
        Assert.Matches("^RGB2[0-9A-F]{64}$", RgbPricingCode.For(AssetA));
    }

    [Fact]
    public void For_CanonicalizesContractIdPresentationForms()
    {
        const string payload = "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw";
        const string withEmbeddedChecksum = "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw2dHQx";

        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For(payload));
        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For($"RGB:{payload[..5]}-{payload[5..]}"));
        Assert.Equal(RgbPricingCode.For(AssetA), RgbPricingCode.For(withEmbeddedChecksum));
    }

    [Fact]
    public void For_RejectsAnInvalidEmbeddedChecksum()
    {
        Assert.Throws<ArgumentException>(() => RgbPricingCode.For(
            "bGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGxsbGw2dHQy"));
    }

    // The 36-byte embedded-checksum form. RgbPricingCode.cs builds the expected checksum as
    // { d[0], d[1], d[1], d[2] } — d[1] twice, d[3] never — which reads exactly like a typo and has been
    // reported as one. It is NOT: it is a faithful port of upstream baid64 0.4.1's own check(), whose
    // last line is literally `[sha[0], sha[1], sha[1], sha[2]]` (src/lib.rs, and =0.4.1 is pinned via
    // rgb-consensus-0.11.1-rc.10 and resolved in native/rgb-verify/Cargo.lock). "Correcting" it to the
    // first four digest bytes would reject every genuine embedded-checksum presentation, so this test
    // computes BOTH forms and pins which one the production code must accept.
    [Fact]
    public void EmbeddedChecksum_MatchesUpstreamBaid64_NotTheFirstFourDigestBytes()
    {
        var payload = Enumerable.Repeat((byte)0x6C, 32).ToArray();
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Security.Cryptography.SHA256.HashData("rgb"u8).Concat(payload).ToArray());

        var upstream = Baid64(payload, [digest[0], digest[1], digest[1], digest[2]]);
        var firstFour = Baid64(payload, [digest[0], digest[1], digest[2], digest[3]]);

        Assert.Equal(Convert.ToHexString(payload), RgbPricingCode.CanonicalizeAssetId(upstream));
        Assert.NotEqual(upstream, firstFour);
        var rejected = Assert.Throws<ArgumentException>(
            () => RgbPricingCode.CanonicalizeAssetId(firstFour));
        Assert.Contains("checksum", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThirtySixByteContractId_WithWrongChecksum_IsRejected()
    {
        var payload = Enumerable.Repeat((byte)0x6C, 32).ToArray();
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Security.Cryptography.SHA256.HashData("rgb"u8).Concat(payload).ToArray());

        var corrupted = Baid64(payload,
            [digest[0], digest[1], digest[1], (byte)(digest[2] ^ 0x01)]);

        Assert.Throws<ArgumentException>(() => RgbPricingCode.CanonicalizeAssetId(corrupted));
    }

    // rgb-core never emits the 36-byte form for a ContractId: rgb-consensus-0.11.1-rc.10
    // src/operation/commit.rs declares `impl DisplayBaid64 for ContractId` with EMBED_CHECKSUM = false
    // over a [u8; 32] payload. Pinned so a dependency that starts emitting 36 bytes is visible here
    // rather than as a refused invoice.
    [Theory]
    [InlineData(AssetA)]
    [InlineData(AssetB)]
    [InlineData(AssetC)]
    public void ContractIdPresentation_DecodesToThirtyTwoBytes(string assetId)
    {
        Assert.Equal(64, RgbPricingCode.CanonicalizeAssetId(assetId).Length);
    }

    static string Baid64(byte[] payload, byte[] checksum) =>
        Convert.ToBase64String(payload.Concat(checksum).ToArray())
            .TrimEnd('=').Replace('+', '_').Replace('/', '~');

    [Fact]
    public void For_PreservesCaseSensitiveBaid64Payload()
    {
        Assert.NotEqual(RgbPricingCode.For(AssetA), RgbPricingCode.For(AssetB));
    }

    [Fact]
    public void For_DistinctAssetIds_YieldDistinctCodes()
    {
        var codes = new[] { RgbPricingCode.For(AssetA), RgbPricingCode.For(AssetB), RgbPricingCode.For(AssetC) };
        Assert.Equal(3, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void For_RejectsEmptyAssetId(string assetId)
    {
        Assert.Throws<ArgumentException>(() => RgbPricingCode.For(assetId));
    }

    [Fact]
    public void Code_ParsesAsCurrencyPairLeftSide()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(CurrencyPair.TryParse($"{code}_USD", out var pair));
        Assert.Equal(code, pair.Left);
        Assert.Equal("USD", pair.Right);
    }

    [Fact]
    public void Code_IsUsableInARateRule()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(RateRules.TryParse($"{code}_USD = 1.5;", out var rules));
        var rule = rules.GetRuleFor(new CurrencyPair(code, "USD"));
        Assert.True(rule.Reevaluate());
        Assert.Equal(1.5m, rule.BidAsk!.Bid);
    }

    [Fact]
    public void IsPricingCode_IsCaseInsensitive_AndRejectsNearMisses()
    {
        var code = RgbPricingCode.For(AssetA);
        Assert.True(RgbPricingCode.IsPricingCode(code));
        Assert.True(RgbPricingCode.IsCurrentPricingCode(code));
        Assert.True(RgbPricingCode.IsPricingCode(code.ToLowerInvariant()));
        Assert.True(RgbPricingCode.IsLegacyPricingCode("RGB0123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsCurrentPricingCode("RGB0123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsPricingCode("USDT"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB20123456789ABCDEF"));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('A', 63)));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('A', 65)));
        Assert.False(RgbPricingCode.IsPricingCode("RGB2" + new string('G', 64)));
        Assert.False(RgbPricingCode.IsPricingCode(null));
    }
}
