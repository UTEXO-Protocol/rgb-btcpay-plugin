using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The fund-loss half of finding F1. rgb.json replaces the whole configuration object, so a file that
// omits rgb_base_dir falls back to the literal "/data" — and GetWalletDataDir CREATES the path it
// returns while migrating only the doubled-network layout BENEATH ONE BASE, never a change of base.
// An RGB stock opened from the wrong parent does not resync from the chain, so both directions matter:
// a deployment already reading "/data" must never be moved off it, and a deployment that has never had
// "/data" must not be moved onto it merely because it wrote a partial config file.
public class RgbBaseDirResolutionTests
{
    const string WalletId = "11111111-2222-3333-4444-555555555555";

    static string TempDataDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "rgb-basedir-" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(root, "Main");
        Directory.CreateDirectory(dataDir);
        return dataDir;
    }

    static RGBConfiguration FromJson(string json) =>
        JsonSerializer.Deserialize<RGBConfiguration>(json)!;

    [Fact]
    public void ConfigFileWithoutBaseDir_LeavesTheFlagClear()
    {
        var cfg = FromJson("""{ "native_send_timeout_seconds": 120 }""");

        Assert.False(cfg.RgbBaseDirExplicitlySet);
        Assert.Equal(RGBConfiguration.DefaultRgbBaseDir, cfg.RgbBaseDir);
    }

    [Fact]
    public void ConfigFileWithExplicitBaseDir_IsHonoured()
    {
        var cfg = FromJson("""{ "rgb_base_dir": "/somewhere/else" }""");

        Assert.True(cfg.RgbBaseDirExplicitlySet);
        Assert.Equal("/somewhere/else", cfg.RgbBaseDir);
    }

    // The case that separates "absent" from "explicitly set to the default value". System.Text.Json
    // invokes the setter only when the property is present, so an operator who deliberately writes the
    // default keeps it and is never second-guessed.
    [Fact]
    public void ConfigFileWithExplicitDefaultBaseDir_IsStillHonoured()
    {
        var cfg = FromJson($"{{ \"rgb_base_dir\": \"{RGBConfiguration.DefaultRgbBaseDir}\" }}");

        Assert.True(cfg.RgbBaseDirExplicitlySet);
        Assert.Equal(RGBConfiguration.DefaultRgbBaseDir, cfg.RgbBaseDir);
    }

    [Theory]
    [InlineData("""{ "rgb_base_dir": null }""")]
    [InlineData("""{ "rgb_base_dir": "" }""")]
    [InlineData("""{ "rgb_base_dir": "   " }""")]
    public void NullOrWhitespaceBaseDir_ReadsAsAbsent(string json)
    {
        var cfg = FromJson(json);

        Assert.False(cfg.RgbBaseDirExplicitlySet);
        Assert.Equal(RGBConfiguration.DefaultRgbBaseDir, cfg.RgbBaseDir);
        Assert.False(string.IsNullOrWhiteSpace(cfg.RgbBaseDir));
    }

    [Fact]
    public void RgbBaseDirExplicitlySet_IsNotPartOfTheWireShape()
    {
        var json = JsonSerializer.Serialize(new RGBConfiguration());

        Assert.DoesNotContain("ExplicitlySet", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rgb_base_dir", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialConfigFile_MovesTheWalletDataDirWhenTheDefaultBaseDirIsAbsent()
    {
        var dataDir = TempDataDir();
        var cfg = FromJson("""{ "native_send_timeout_seconds": 120 }""");

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir, _ => false);

        var walletDir = cfg.GetWalletDataDir(WalletId, "regtest");
        Assert.StartsWith(Directory.GetParent(dataDir)!.FullName, walletDir, StringComparison.Ordinal);
        Assert.False(
            walletDir.StartsWith(RGBConfiguration.DefaultRgbBaseDir + Path.DirectorySeparatorChar,
                StringComparison.Ordinal),
            $"the wallet directory resolved to {walletDir}, still under the built-in default. With no "
            + "wallet data at the default and a config file that never named a base, the plugin must "
            + "use the directory it would have chosen without the file — otherwise writing rgb.json to "
            + "raise a timeout silently relocates every wallet's RGB stock.");
    }

    // The regression test for the blocker two independent reviewers found: substituting the resolved
    // base UNCONDITIONALLY orphans every install whose rgb.json has always omitted the key, because
    // that install has been reading "/data" all along. Named after the test the earlier finding-C gate
    // used for the same property.
    [Fact]
    public void PartialConfigFile_KeepsTheLegacyDefaultWhenThatDirectoryExists()
    {
        var dataDir = TempDataDir();
        var cfg = FromJson("""{ "native_send_timeout_seconds": 120 }""");

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir,
            path => path == RGBConfiguration.DefaultRgbBaseDir);

        Assert.Equal(RGBConfiguration.DefaultRgbBaseDir, cfg.RgbBaseDir);
        Assert.StartsWith(RGBConfiguration.DefaultRgbBaseDir,
            cfg.GetWalletDataDir(WalletId, "regtest"), StringComparison.Ordinal);
    }

    // Directory.Exists returns FALSE rather than throwing for a path it cannot traverse and for a
    // volume that is not yet mounted. A probe scoped to a subtree under the default therefore reads
    // "empty" for a container whose /data is mounted but unreadable, and relocates. This case passes
    // only if the probe asks about the default directory itself.
    [Fact]
    public void DefaultBaseDirPresentButEmpty_StillKeepsTheDefault()
    {
        var dataDir = TempDataDir();
        var cfg = FromJson("""{ "native_send_timeout_seconds": 120 }""");

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir,
            path => path == RGBConfiguration.DefaultRgbBaseDir);

        Assert.True(cfg.RgbBaseDir == RGBConfiguration.DefaultRgbBaseDir,
            $"the base moved to {cfg.RgbBaseDir} even though the built-in default exists on this host. "
            + "An existing but empty-looking default may be a mounted volume this process cannot read, "
            + "and relocating off it opens a fresh RGB stock that no chain rescan can rebuild.");
    }

    [Fact]
    public void TheProbeAsksOnlyAboutTheDefaultBaseDir()
    {
        var dataDir = TempDataDir();
        var cfg = FromJson("""{ "native_send_timeout_seconds": 120 }""");
        var asked = new List<string>();

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir, path =>
        {
            asked.Add(path);
            return false;
        });

        Assert.Equal(new[] { RGBConfiguration.DefaultRgbBaseDir }, asked);
    }

    [Fact]
    public void ExplicitBaseDir_IsNeverOverriddenEvenWhenTheDefaultIsAbsent()
    {
        var dataDir = TempDataDir();
        var cfg = FromJson("""{ "rgb_base_dir": "/operator/choice" }""");
        var asked = 0;

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir, _ => { asked++; return false; });

        Assert.Equal("/operator/choice", cfg.RgbBaseDir);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void AConfigurationBuiltInCode_CountsAsExplicit()
    {
        var dataDir = TempDataDir();
        var cfg = new RGBConfiguration("/from/code");

        RGBPlugin.ApplyResolvedRgbBaseDir(cfg, dataDir, _ => false);

        Assert.True(cfg.RgbBaseDirExplicitlySet);
        Assert.Equal("/from/code", cfg.RgbBaseDir);
    }
}

// ApplyResolvedRgbBaseDir is only useful if LoadConfiguration actually calls it on the rgb.json branch,
// and LoadConfiguration needs a PluginServiceCollection plus an IConfiguration, so no unit test can
// reach it. Without this pin, deleting the call leaves every test above green while restoring the
// original defect in full.
public class RgbBaseDirLoadConfigurationPinTests
{
    const string PluginType = "RGBPlugin";
    const string Load = "LoadConfiguration";
    const string Seam = "ApplyResolvedRgbBaseDir";

    [Fact]
    public void LoadConfiguration_ResolvesTheBaseDirBeforeReturningTheDeserializedFile()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree(RoslynPins.PluginFile);
        var method = RoslynPins.Method(tree, PluginType, Load);
        var body = RoslynPins.BodyOf(method);

        RoslynPins.AssertNoLocalShadow(method, Seam, "ApplyEnvironmentOverrides");

        var calls = body.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax
            {
                Identifier.ValueText: Seam
            })
            .ToList();

        Assert.True(calls.Count == 1,
            $"{Load} invokes {Seam} {calls.Count} time(s); exactly one is expected. Without it, an "
            + "rgb.json that omits rgb_base_dir keeps the literal default even where nothing has ever "
            + "been stored there, so writing that file to raise a timeout relocates every wallet's RGB "
            + "stock — and no unit test can see it, because LoadConfiguration needs a live "
            + "PluginServiceCollection.");

        RoslynPins.AssertBindsToMemberOf(plugin, tree, calls[0].Expression,
            Microsoft.CodeAnalysis.SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.RGBPlugin", Seam, Load);

        var returns = body.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax>()
            .Where(r => r.Expression?.ToString() == "fromFile")
            .ToList();
        Assert.True(returns.Count == 1, $"{Load} returns the deserialized file {returns.Count} time(s)");
        Assert.True(calls[0].SpanStart < returns[0].SpanStart,
            $"{Load} returns the deserialized configuration before resolving its base directory");

        var firstArgument = calls[0].ArgumentList.Arguments[0].Expression.ToString();
        Assert.True(firstArgument == "fromFile",
            $"{Load} passes '{firstArgument}' to {Seam}; it must be the object it is about to return, "
            + "or the resolution is applied to something the caller never sees.");
    }
}
