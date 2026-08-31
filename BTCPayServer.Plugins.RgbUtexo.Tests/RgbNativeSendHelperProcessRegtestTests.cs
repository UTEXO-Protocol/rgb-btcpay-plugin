using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RgbRegtestSerial")]
public sealed class RgbNativeSendHelperProcessRegtestTests
{
    const string StoreId = "store-under-helper-free-path-test";
    const string WalletNetwork = "regtest";
    const string RgbLibNetwork = "Regtest";
    const string ElectrumUrlEnvironmentVariable = "RGB_ELECTRUM_URL";
    const string ProxyEndpointEnvironmentVariable = "RGB_PROXY_ENDPOINT";
    const string LocalProxyEndpoint = "rpc://localhost:3000/json-rpc";
    const string UnheldAssetId = "rgb:7WB6CE0U-rRMZALI-EXCAT1L-OzXOu82-6502mza-zJosGHU";
    const string WellFormedRecipientId =
        "bcrt:utxob:VRxeDMMF-ojxE~MQ-yfpAbil-_ugtXWJ-0mP8urf-3Kjkg5N-g0tLT";
    const string RgbLibErrorForAnUnheldAsset = "AssetNotFound";
    const int MaxAllocationsPerUtxo = 5;
    const int MinConfirmations = 1;
    const float SendFeeRate = 2f;
    const long SentAmount = 100;

    [IntegrationFact]
    public async Task TheHelperProcessReadsAndFreesTheRealSendBeginResultInsideTheChildProcess()
    {
        var indexerUrl = RgbRegtestStackGate.RequireReachableIndexer();
        var previousElectrumUrl = Environment.GetEnvironmentVariable(ElectrumUrlEnvironmentVariable);
        var previousProxyEndpoint = Environment.GetEnvironmentVariable(ProxyEndpointEnvironmentVariable);
        Environment.SetEnvironmentVariable(ElectrumUrlEnvironmentVariable, "tcp://" + indexerUrl);
        Environment.SetEnvironmentVariable(ProxyEndpointEnvironmentVariable, LocalProxyEndpoint);

        try
        {
            await using var harness = await RgbPluginDatabaseHarness.CreateAsync();
            await harness.RunPluginMigrationsAsync();
            var rgbLib = new RgbLibService(
                harness.Configuration, harness.Factory, NullLogger<RgbLibService>.Instance);

            var keys = JsonDocument
                .Parse(global::RgbLib.RgbLibWallet.GenerateKeys(RgbLibNetwork)).RootElement;
            var walletId = Guid.NewGuid().ToString();
            var masterFingerprint = keys.GetProperty("master_fingerprint").GetString()!;
            var xpubVanilla = keys.GetProperty("account_xpub_vanilla").GetString()!;
            var xpubColored = keys.GetProperty("account_xpub_colored").GetString()!;

            await using (var ctx = harness.Factory.CreateContext())
            {
                ctx.RGBWallets.Add(new RGBWallet
                {
                    Id = walletId,
                    StoreId = StoreId,
                    Network = WalletNetwork,
                    XpubVanilla = xpubVanilla,
                    XpubColored = xpubColored,
                    MasterFingerprint = masterFingerprint,
                    IsActive = true
                });
                await ctx.SaveChangesAsync();
            }

            await rgbLib.GetAddressAsync(walletId);
            var dataDir = rgbLib.GetWalletDataDir(walletId, WalletNetwork);
            var leaseWalletDir = Path.Combine(dataDir, masterFingerprint);
            Assert.True(Directory.Exists(leaseWalletDir),
                $"rgb-lib did not create {leaseWalletDir}; the helper lease lives in the wallet directory "
                + "rgb-lib derives from the master fingerprint");
            Assert.True(rgbLib.UnloadWallet(walletId),
                "the cached parent wallet must be quiesced before the helper opens the same SQLite state");

            var recipientMap = JsonSerializer.Serialize(new Dictionary<string, object[]>
            {
                [UnheldAssetId] =
                [
                    new
                    {
                        recipient_id = WellFormedRecipientId,
                        witness_data = (object?)null,
                        assignment = new { Fungible = SentAmount },
                        transport_endpoints = new[] { LocalProxyEndpoint }
                    }
                ]
            });

            using var parentLease = RgbNativeSendLease.AcquireParent(leaseWalletDir);
            var request = JsonSerializer.Serialize(new
            {
                DataDir = dataDir,
                BitcoinNetwork = RgbLibNetwork,
                ElectrumUrl = RGBConfiguration.GetNetworkSettings(WalletNetwork).ElectrumUrl,
                XpubVanilla = xpubVanilla,
                XpubColored = xpubColored,
                MasterFingerprint = masterFingerprint,
                LeaseWalletDir = leaseWalletDir,
                LeaseToken = RgbNativeSendLease.GetWorkerTokenForCurrentContext(leaseWalletDir),
                MaxAllocationsPerUtxo = MaxAllocationsPerUtxo,
                RecipientMapJson = recipientMap,
                FeeRate = SendFeeRate,
                MinConfirmations = MinConfirmations,
                SignedPsbt = (string?)null
            });

            var runner = new NativeSendProcessRunner(NullLogger<NativeSendProcessRunner>.Instance);
            var result = await runner.RunAsync("send-begin", request, leaseWalletDir, () => true,
                new RGBConfiguration().ToNativeSendLimits(), CancellationToken.None);

            Assert.True(result.ChildReaped, "the helper child process was not reaped");
            Assert.Equal(NativeSendOutcome.Exited, result.Outcome);
            Assert.True(result.StdErr.Contains(RgbLibErrorForAnUnheldAsset, StringComparison.Ordinal),
                $"the helper process reported '{result.StdErr.Trim()}' rather than rgb-lib's own "
                + $"{RgbLibErrorForAnUnheldAsset} for an asset this wallet does not hold. That text is "
                + "allocated by rgb-lib and can only reach stderr if RgbNativeSend.ReadResult read the "
                + "CResultString and completed its finally block, where the rgb-lib string free lives. A "
                + "free that cannot be reached throws from that finally and replaces every send_begin and "
                + "send_end outcome, success included. Nothing else in the suite launches this child "
                + "process, which is why a green managed suite says nothing about it.");
            Assert.DoesNotContain("rgblib_string_free", result.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("MissingMethod", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ElectrumUrlEnvironmentVariable, previousElectrumUrl);
            Environment.SetEnvironmentVariable(ProxyEndpointEnvironmentVariable, previousProxyEndpoint);
        }
    }
}
