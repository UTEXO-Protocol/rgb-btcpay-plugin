using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

internal static class RgbRegtestStackGate
{
    internal const string IndexerUrlEnvironmentVariable = "RGB_E2E_INDEXER_URL";
    internal const string RegtestScriptEnvironmentVariable = "RGB_E2E_REGTEST_SCRIPT";
    internal const string DefaultIndexerUrl = "127.0.0.1:50001";

    internal const string BringUpRecipe =
        "Bring up rgb-lib's own docker harness at the pinned tag v0.3.0-beta.30 "
        + "(commit 12da9a646d3a85c90e374fddb897c010600b5d58): "
        + "cd <rgb-lib checkout>/tests && ./regtest.sh prepare_bindings_examples_environment. "
        + "There is no 'start' subcommand, and the script's own port pre-check uses `ss`, which does not "
        + "exist on macOS and silently reports every port free, so check 18443 18444 50001 3000 8140 8141 "
        + "with lsof first. Point " + RegtestScriptEnvironmentVariable + " at that regtest.sh and, if the "
        + "Electrum endpoint is not " + DefaultIndexerUrl + ", override it with "
        + IndexerUrlEnvironmentVariable + ".";

    internal static string RequireReachableIndexer()
    {
        var indexerUrl = Environment.GetEnvironmentVariable(IndexerUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(indexerUrl)) indexerUrl = DefaultIndexerUrl;

        var separator = indexerUrl.LastIndexOf(':');
        Assert.True(separator > 0 && int.TryParse(indexerUrl[(separator + 1)..], out _),
            $"'{indexerUrl}' is not a host:port Electrum endpoint");
        var host = indexerUrl[..separator];
        var port = int.Parse(indexerUrl[(separator + 1)..]);

        try
        {
            using var client = new TcpClient();
            client.Connect(host, port);
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            var probe = Encoding.UTF8.GetBytes(
                "{\"jsonrpc\":\"2.0\",\"id\":0,\"method\":\"server.version\","
                + "\"params\":[\"rgb-plugin-e2e\",\"1.4\"]}\n");
            stream.Write(probe, 0, probe.Length);
            var buffer = new byte[512];
            var read = stream.Read(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, read);
            Assert.Contains("\"result\"", response);
        }
        catch (Exception fault) when (fault is not Xunit.Sdk.XunitException)
        {
            Assert.Fail($"the Electrum indexer at {indexerUrl} is not answering ({fault.GetType().Name}: "
                        + $"{fault.Message}). Nothing below can reach the clause it is credited to without "
                        + $"it. {BringUpRecipe}");
        }

        return indexerUrl;
    }

    internal static string RequireRegtestScript()
    {
        var script = Environment.GetEnvironmentVariable(RegtestScriptEnvironmentVariable);
        Assert.True(!string.IsNullOrWhiteSpace(script) && File.Exists(script),
            $"{RegtestScriptEnvironmentVariable} does not point at an existing regtest.sh (got "
            + $"'{script ?? "<unset>"}'). The chain must be funded and mined through it because this "
            + $"compose file does not publish bitcoind's RPC port. {BringUpRecipe}");
        return script!;
    }

    internal static string Run(string script, params string[] arguments)
    {
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(script)),
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.GetFullPath(script));
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
                            ?? throw new InvalidOperationException("could not start bash");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(180_000), $"regtest.sh {string.Join(' ', arguments)} did not finish");
        Assert.True(process.ExitCode == 0,
            $"regtest.sh {string.Join(' ', arguments)} exited {process.ExitCode}: {stdout} {stderr}");
        return stdout;
    }
}
