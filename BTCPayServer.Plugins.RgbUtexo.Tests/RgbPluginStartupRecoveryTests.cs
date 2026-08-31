using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.Loader;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection(RgbNativeConsoleErrorCollection.Name)]
public class RgbPluginStartupRecoveryTests
{
    [Fact]
    public void MissingGateNative_LogsButDoesNotAbortPluginRegistrations()
    {
        var hostOutput = HostOutputDirectory();
        Assembly? ResolveHostPrivateAssembly(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(hostOutput, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        AssemblyLoadContext.Default.Resolving += ResolveHostPrivateAssembly;
        var moved = new List<(string Hidden, string Original)>();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "rgb-startup-recovery-" + Guid.NewGuid());
        var originalError = Console.Error;
        using var captured = new StringWriter();

        try
        {
            moved = HideGateNativeCandidates();
            Directory.CreateDirectory(dataDirectory);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["datadir"] = dataDirectory })
                .Build();
            using var bootstrap = new ServiceCollection()
                .AddSingleton<IConfiguration>(configuration)
                .AddLogging()
                .BuildServiceProvider();
            var registrations = new ServiceCollection();
            var pluginServices = new PluginServiceCollection(registrations, bootstrap);

            Console.SetError(captured);

            // The native is absent, so this exercises the real default probe and the public plugin
            // startup method. Execute must return normally and reach the registrations below it.
            new RGBPlugin().Execute(pluginServices);

            Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(IRGBWalletService));
            Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(INativeSendProcessRunner));
            Assert.Contains(registrations, descriptor => descriptor.ServiceType == typeof(INotificationHandler));
            Assert.Equal(3, registrations.Count(descriptor => descriptor.ServiceType == typeof(IUIExtension)));

            var diagnostic = captured.ToString();
            Assert.Contains("All RGB asset sends will be rejected until this is fixed.", diagnostic,
                StringComparison.Ordinal);
            Assert.Contains("Receiving RGB assets and the rest of the plugin are unaffected.", diagnostic,
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            AssemblyLoadContext.Default.Resolving -= ResolveHostPrivateAssembly;
            foreach (var (hidden, original) in moved.AsEnumerable().Reverse())
                File.Move(hidden, original);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    static List<(string Hidden, string Original)> HideGateNativeCandidates()
    {
        var baseDirectory = RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly);
        var moved = new List<(string Hidden, string Original)>();
        foreach (var original in RgbVerifyNative.CandidatePaths(baseDirectory).Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(original)) continue;
            var hidden = original + ".startup-recovery-test-" + Guid.NewGuid();
            File.Move(original, hidden);
            moved.Add((hidden, original));
        }
        return moved;
    }

    static string HostOutputDirectory()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var targetFramework = output.Name;
        var configuration = output.Parent!.Name;
        var path = Path.Combine(PluginCompilation.RepoRootPath, "submodules", "btcpayserver",
            "BTCPayServer", "bin", configuration, targetFramework);
        Assert.True(Directory.Exists(path), $"host build did not produce {path}");
        return path;
    }
}
