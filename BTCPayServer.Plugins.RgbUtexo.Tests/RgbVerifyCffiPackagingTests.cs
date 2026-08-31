using System.Diagnostics;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

/// <summary>
/// Pins the two build-file properties the native packaging project depends on. Both assert over
/// MSBuild's own <em>evaluated</em> item sets rather than over project text: a string match is
/// satisfied by an item inside a comment or one carrying Condition="false", and is false-rejected by
/// attribute reordering or reformatting.
/// </summary>
public class RgbVerifyCffiPackagingTests
{
    const string PackagingDirectory = "native/rgb-verify/packaging/";

    // Planted so the assertion cannot pass vacuously: the nested obj/ tree only exists once the
    // packaging project has been built, and that generated AssemblyInfo.cs is exactly what breaks
    // the plugin build with CS0579 when the glob removals are absent.
    static readonly string[] ProbeFiles =
    [
        PackagingDirectory + "GlobPinProbe.cs",
        PackagingDirectory + "obj/GlobPinProbe.g.cs",
    ];

    [Fact]
    public void PackagingProject_ExcludedFromPluginGlobs()
    {
        var repoRoot = PluginCompilation.RepoRootPath;
        var planted = ProbeFiles.Select(relative => Path.Combine(repoRoot, relative)).ToList();

        try
        {
            foreach (var path in planted)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "// planted by PackagingProject_ExcludedFromPluginGlobs\n");
            }

            var project = Path.Combine(repoRoot, "BTCPayServer.Plugins.RgbUtexo.csproj");

            foreach (var itemType in new[] { "Compile", "Content", "EmbeddedResource", "None" })
            {
                var leaked = EvaluateItems(project, itemType)
                    .Where(identity => identity.Replace('\\', '/').Contains(PackagingDirectory, StringComparison.Ordinal))
                    .ToList();

                Assert.True(leaked.Count == 0,
                    $"the plugin's {itemType} set still reaches the packaging project: {string.Join(", ", leaked)}");
            }
        }
        finally
        {
            foreach (var path in planted)
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public void PackagingProject_ExcludedFromBclMemoryInjection()
    {
        var project = Path.Combine(PluginCompilation.RepoRootPath, PackagingDirectory, "RgbVerifyCffi.csproj");

        var references = EvaluateItems(project, "PackageReference");

        // The trust core's nuspec must be dependency-free: anything inherited here would put another
        // package in the graph of the one component the pre-sign gate is meant to trust alone.
        Assert.True(references.Count == 0,
            $"RgbVerifyCffi inherits PackageReference(s): {string.Join(", ", references)}");
    }

    static List<string> EvaluateItems(string projectPath, string itemType)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

        var process = Process.Start(new ProcessStartInfo(dotnet)
        {
            ArgumentList =
            {
                "msbuild", projectPath,
                "-getItem:" + itemType,
                "-p:StaticWebAssetsEnabled=false",
                "-nologo",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(process);

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0,
            $"dotnet msbuild -getItem:{itemType} failed on {projectPath}:\n{stdout}\n{stderr}");

        var start = stdout.IndexOf('{');
        Assert.True(start >= 0, $"no JSON in the evaluation output:\n{stdout}");

        using var document = JsonDocument.Parse(stdout[start..]);
        if (!document.RootElement.TryGetProperty("Items", out var items)
            || !items.TryGetProperty(itemType, out var entries))
        {
            return [];
        }

        return entries.EnumerateArray()
            .Select(entry => entry.GetProperty("Identity").GetString() ?? string.Empty)
            .ToList();
    }
}
