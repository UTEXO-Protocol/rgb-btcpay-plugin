using BTCPayServer.Plugins.RgbUtexo.Models;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbSendAssetContractIdentityTests
{
    const string ValuableContractId = "rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-TMsGMSA";
    const string LookAlikeContractIdSharingAHeadWithTheValuableOne =
        "rgb:2dkSTbr-jFhznbPmo-ZCL6bx2Kn-MhR2GZsUjh-YjYkHM4gH-ZZZZZZZ";
    const string ShortContractId = "rgb:AAAA-BBBB-CCCC";

    static RGBAssetViewModel Asset(string assetId) => new() { AssetId = assetId };

    [Fact]
    public void AbbreviatedContractId_DiffersBetweenLookAlikesThatShareAHead_BecauseAHeadOnlyElisionIsGrindable()
    {
        var valuable = Asset(ValuableContractId).AssetIdAbbreviatedKeepingHeadAndTail;
        var lookAlike = Asset(LookAlikeContractIdSharingAHeadWithTheValuableOne)
            .AssetIdAbbreviatedKeepingHeadAndTail;

        Assert.NotEqual(valuable, lookAlike);
        Assert.EndsWith(ValuableContractId[^RGBAssetViewModel.ContractIdTailCharsShown..], valuable);
        Assert.EndsWith(
            LookAlikeContractIdSharingAHeadWithTheValuableOne[^RGBAssetViewModel.ContractIdTailCharsShown..],
            lookAlike);
    }

    [Fact]
    public void AbbreviatedContractId_KeepsTheHeadOfTheContractId_SoTheDropdownRowIsRecognisable()
    {
        var abbreviated = Asset(ValuableContractId).AssetIdAbbreviatedKeepingHeadAndTail;

        Assert.StartsWith(ValuableContractId[..RGBAssetViewModel.ContractIdHeadCharsShown], abbreviated);
        Assert.Contains(RGBAssetViewModel.ContractIdElidedMiddleMarker, abbreviated);
        Assert.True(abbreviated.Length < ValuableContractId.Length,
            "an abbreviation that is not shorter than the contract id would widen the select for no gain");
    }

    [Theory]
    [InlineData(ShortContractId)]
    [InlineData("")]
    [InlineData("rgb:")]
    public void AbbreviatedContractId_ReturnsAShortIdUnchanged_SoTheSendPageCannotThrowAndStrandAFundedWallet(
        string assetId)
    {
        Assert.Equal(assetId, Asset(assetId).AssetIdAbbreviatedKeepingHeadAndTail);
    }

    [Fact]
    public void SendAssetOption_IdentifiesTheContract_NotOnlyTheIssuerChosenTicker()
    {
        var option = Between(ViewSource(), "<option value=\"@asset.AssetId\"", "</option>");

        Assert.Contains("@asset.AssetIdAbbreviatedKeepingHeadAndTail", option);
        Assert.Contains("@asset.Name", option);
        Assert.Contains("title=\"@asset.AssetId\"", option);
        Assert.Contains("data-name=\"@asset.Name\"", option);
    }

    [Fact]
    public void SendAssetConfirmDialog_QuotesTheContractIdThatTheFormWillSubmit()
    {
        var confirmArgument = Between(ViewSource(), "if(!confirm(", ")) return false;");

        Assert.Contains("contractId", confirmArgument);
        Assert.Contains("Contract id that will be signed", confirmArgument);
        Assert.Contains("var contractId=sel.value", ViewSource());
    }

    [Fact]
    public void SendAssetView_RendersTheWholeContractIdBesideTheSelect_BeforeTheOperatorSubmits()
    {
        var content = ViewSource();

        Assert.Contains("id=\"selectedContractId\"", content);
        Assert.Contains("id=\"selectedContractRow\"", content);
        Assert.Contains("document.getElementById('selectedContractId').textContent = contractId;", content);
        Assert.Contains("renderSelectedContractId();", content);
    }

    [Fact]
    public void SendAssetView_ReachesIssuerControlledStringsThroughAttributesOnly_NeverThroughInterpolatedScript()
    {
        var content = ViewSource();
        var onclick = Between(content, "onclick=\"", "\">");

        Assert.Contains("getAttribute('data-name')", onclick);
        Assert.Contains("getAttribute('data-ticker')", onclick);
        Assert.DoesNotContain("@asset.", onclick);
        Assert.DoesNotContain("@Model.", onclick);
        Assert.DoesNotContain("Html.Raw", content);
    }

    static string ViewSource()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Views", "RGB", "SendAsset.cshtml"));
        Assert.True(File.Exists(path), $"Could not locate SendAsset.cshtml at {path}");
        return File.ReadAllText(path);
    }

    static string Between(string content, string from, string to)
    {
        var start = content.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' not found in SendAsset.cshtml");
        var end = content.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"'{to}' not found after '{from}' in SendAsset.cshtml");
        return content[start..end];
    }
}
