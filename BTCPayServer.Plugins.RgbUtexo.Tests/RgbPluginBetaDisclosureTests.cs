namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbPluginBetaDisclosureTests
{
    [Fact]
    public void Description_DisclosesBetaStatusToTheOperator()
    {
        var description = new RGBPlugin().Description;

        Assert.False(string.IsNullOrWhiteSpace(description),
            "BaseBTCPayServerPlugin.Description reads AssemblyDescriptionAttribute; an empty value "
            + "means the csproj <Description> failed to reach the built assembly");
        Assert.Contains("beta", description, StringComparison.OrdinalIgnoreCase);
    }
}
