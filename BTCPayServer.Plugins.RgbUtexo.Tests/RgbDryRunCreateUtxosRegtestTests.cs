using System.Reflection;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RgbRegtestSerial")]
public sealed class RgbDryRunCreateUtxosRegtestTests
{
    const string StoreId = "store-under-dry-run-test";
    const string ElectrumUrlEnvironmentVariable = "RGB_ELECTRUM_URL";
    const int ColorableUtxoCount = 2;
    const int SecondRequestCount = 1;
    const int ColorableUtxoSize = 1000;
    const float FeeRate = RGBWalletService.CreateUtxosFeeRate;

    [IntegrationFact]
    public async Task CreateUtxosEndSucceedsAfterADryRunBegin_AndTheBeginReservesNothing()
    {
        var indexerUrl = RgbRegtestStackGate.RequireReachableIndexer();
        var script = RgbRegtestStackGate.RequireRegtestScript();

        await using var wallet = await FundedRegtestWallet.CreateAsync(indexerUrl, script);

        var psbt = await wallet.Service.CreateUtxosBeginAsync(
            wallet.WalletId, ColorableUtxoCount, ColorableUtxoSize, FeeRate);
        Assert.False(string.IsNullOrWhiteSpace(psbt));

        var reservedAfterBegin =
            await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(wallet.RgbLibDbPath);
        Assert.Empty(reservedAfterBegin);
        Assert.Equal(
            RgbVanillaReservationState.Clean,
            RgbVanillaReservationInspector.Classify(reservedAfterBegin, []).State);

        var signed = await wallet.SignAsProductionCreateUtxosDoesAsync(psbt, ColorableUtxoCount);
        var created = await wallet.Service.CreateUtxosEndAsync(wallet.WalletId, signed);

        Assert.Equal(ColorableUtxoCount, int.Parse(created.Trim('"')));

        var reservedAfterEnd =
            await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(wallet.RgbLibDbPath);
        Assert.Empty(reservedAfterEnd);
    }

    [IntegrationFact]
    public async Task CreateUtxosCreatesExactlyTheRequestedCount_WithAllocatableUtxosAlreadyStanding()
    {
        var indexerUrl = RgbRegtestStackGate.RequireReachableIndexer();
        var script = RgbRegtestStackGate.RequireRegtestScript();

        await using var wallet = await FundedRegtestWallet.CreateAsync(indexerUrl, script);

        var firstPsbt = await wallet.Service.CreateUtxosBeginAsync(
            wallet.WalletId, ColorableUtxoCount, ColorableUtxoSize, FeeRate);
        var firstSigned = await wallet.SignAsProductionCreateUtxosDoesAsync(firstPsbt, ColorableUtxoCount);
        Assert.Equal(ColorableUtxoCount, int.Parse(
            (await wallet.Service.CreateUtxosEndAsync(wallet.WalletId, firstSigned)).Trim('"')));
        RgbRegtestStackGate.Run(script, "mine", "2");

        var standing = await wallet.CountColorableUtxosAsync();
        Assert.Equal(ColorableUtxoCount, standing);

        var secondPsbt = await wallet.Service.CreateUtxosBeginAsync(
            wallet.WalletId, SecondRequestCount, ColorableUtxoSize, FeeRate);
        Assert.False(string.IsNullOrWhiteSpace(secondPsbt),
            $"create_utxos_begin returned nothing for a request of {SecondRequestCount} while "
            + $"{standing} empty colorable UTXOs were standing. InterpretCreateUtxosBegin maps rgb-lib's "
            + "AllocationsAlreadyAvailable to the empty string, and rgb-lib raises it only inside its "
            + "`if up_to` branch — so an empty PSBT here means the up_to argument at the single "
            + "rgblib_create_utxos_begin call site is true again. With up_to = true the request is not the "
            + "number of NEW outputs: rgb-lib deducts its ALLOCATABLE count from it, which is neither the "
            + "standing colorable count nor zero, so the plugin cannot predict the transaction it is about "
            + "to sign and the consent screen's per-attempt and standing figures both stop holding.");

        var secondSigned = await wallet.SignAsProductionCreateUtxosDoesAsync(secondPsbt, SecondRequestCount);
        Assert.Equal(SecondRequestCount, int.Parse(
            (await wallet.Service.CreateUtxosEndAsync(wallet.WalletId, secondSigned)).Trim('"')));
        RgbRegtestStackGate.Run(script, "mine", "2");

        Assert.Equal(standing + SecondRequestCount, await wallet.CountColorableUtxosAsync());
    }

    [IntegrationFact]
    public async Task ABeginThatDoesReserve_IsSeenByTheInspectorAsLiveAndConstraining()
    {
        var indexerUrl = RgbRegtestStackGate.RequireReachableIndexer();
        var script = RgbRegtestStackGate.RequireRegtestScript();

        await using var wallet = await FundedRegtestWallet.CreateAsync(indexerUrl, script);

        wallet.CreateUtxosBeginWithReservation(ColorableUtxoCount, ColorableUtxoSize, FeeRate);

        var reserved =
            await RgbVanillaReservationInspector.ReadReservedOutpointsAsync(wallet.RgbLibDbPath);
        Assert.NotEmpty(reserved);
        Assert.All(reserved, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Txid));
            Assert.True(row.Vout >= 0);
        });

        var stillUnspent = reserved
            .Select(row => new Outpoint(row.Txid, row.Vout))
            .ToList();

        var live = RgbVanillaReservationInspector.Classify(reserved, stillUnspent);
        Assert.Equal(RgbVanillaReservationState.LiveAndConstraining, live.State);
        Assert.Equal(reserved.Count, live.StillUnspent.Count);

        var inert = RgbVanillaReservationInspector.Classify(reserved, []);
        Assert.Equal(RgbVanillaReservationState.InertAlreadyRecovered, inert.State);
        Assert.Empty(inert.StillUnspent);

        var unknown = RgbVanillaReservationInspector.Classify(reserved, null);
        Assert.Equal(RgbVanillaReservationState.Unknown, unknown.State);
    }

    sealed class FundedRegtestWallet : IAsyncDisposable
    {
        readonly RgbPluginDatabaseHarness _harness;
        readonly string? _previousElectrumUrl;
        readonly string _mnemonic;
        readonly string _fingerprint;

        internal string WalletId { get; }
        internal RgbLibService Service { get; }
        internal string RgbLibDbPath { get; }

        FundedRegtestWallet(RgbPluginDatabaseHarness harness, string? previousElectrumUrl, string walletId,
            string mnemonic, string fingerprint, RgbLibService rgbLib, string rgbLibDbPath)
        {
            _harness = harness;
            _previousElectrumUrl = previousElectrumUrl;
            _mnemonic = mnemonic;
            _fingerprint = fingerprint;
            WalletId = walletId;
            Service = rgbLib;
            RgbLibDbPath = rgbLibDbPath;
        }

        internal static async Task<FundedRegtestWallet> CreateAsync(string indexerUrl, string script)
        {
            var previousElectrumUrl = Environment.GetEnvironmentVariable(ElectrumUrlEnvironmentVariable);
            Environment.SetEnvironmentVariable(ElectrumUrlEnvironmentVariable, "tcp://" + indexerUrl);

            var harness = await RgbPluginDatabaseHarness.CreateAsync();
            await harness.RunPluginMigrationsAsync();

            var keys = JsonDocument.Parse(global::RgbLib.RgbLibWallet.GenerateKeys("Regtest")).RootElement;
            var mnemonic = keys.GetProperty("mnemonic").GetString()!;
            var fingerprint = keys.GetProperty("master_fingerprint").GetString()!;
            var walletId = Guid.NewGuid().ToString();

            await using (var ctx = harness.Factory.CreateContext())
            {
                ctx.RGBWallets.Add(new RGBWallet
                {
                    Id = walletId,
                    StoreId = StoreId,
                    Network = "regtest",
                    XpubVanilla = keys.GetProperty("account_xpub_vanilla").GetString()!,
                    XpubColored = keys.GetProperty("account_xpub_colored").GetString()!,
                    MasterFingerprint = fingerprint,
                    IsActive = true
                });
                await ctx.SaveChangesAsync();
            }

            var rgbLib = new RgbLibService(
                harness.Configuration, harness.Factory, NullLogger<RgbLibService>.Instance);
            var address = await rgbLib.GetAddressAsync(walletId);

            RgbRegtestStackGate.Run(script, "sendtoaddress", address, "0.01");
            RgbRegtestStackGate.Run(script, "mine", "2");

            var rgbLibDbPath = Path.Combine(
                harness.Configuration.GetWalletDataDir(walletId, "regtest"), fingerprint, "rgb_lib_db");

            return new FundedRegtestWallet(
                harness, previousElectrumUrl, walletId, mnemonic, fingerprint, rgbLib, rgbLibDbPath);
        }

        internal async Task<int> CountColorableUtxosAsync()
            => (await Service.ListUnspentsAsync(WalletId)).Count(u => u.Utxo.Colorable);

        internal async Task<string> SignAsProductionCreateUtxosDoesAsync(string psbt, int count)
        {
            var ownAddress = BitcoinAddress.Create(
                await Service.GetAddressAsync(WalletId), Network.RegTest);
            var policy = new SigningPolicy
            {
                MaxUnknownOutputSats = 0,
                MaxFeeSats = RGBWalletService.CreateUtxosMaxFeeSatsAtOneInput(count),
                MaxFeeSatsPerAdditionalInput =
                    RGBWalletService.CreateUtxosMaxFeeSatsPerAdditionalInput(count),
                AllowedScripts = new HashSet<Script> { ownAddress.ScriptPubKey },
                MaxOutputCount = count + 1,
                RequireRgbVanillaKeychainInputs = true
            };
            using var signer = new MemoryWalletSigner(_mnemonic, Network.RegTest);
            return await signer.SignPsbtAsync(psbt, Network.RegTest, policy);
        }

        internal void CreateUtxosBeginWithReservation(int count, int size, float feeRate)
        {
            var handle = Service.GetOrCreateWalletAsync(WalletId).GetAwaiter().GetResult();
            handle.ExecuteAsync(wallet =>
            {
                var walletType = typeof(global::RgbLib.RgbLibWallet);
                var walletField = walletType.GetField(
                    "_wallet", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var onlineField = walletType.GetField(
                    "_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var native = walletType.Assembly.GetType("RgbLib.NativeMethods")!;
                var method = native.GetMethod("rgblib_create_utxos_begin")!;

                var parameters = method.GetParameters();
                var dryRunIndex = Array.FindIndex(parameters, p => p.Name == "dry_run");
                Assert.True(dryRunIndex == parameters.Length - 1,
                    $"rgblib_create_utxos_begin's dry_run parameter moved to index {dryRunIndex} of "
                    + $"{parameters.Length}; this fixture builds the reservation state that the production "
                    + "path must never create, so it has to set that exact parameter rather than a position");

                var args = new object?[]
                {
                    walletField.GetValue(wallet)!, (string)onlineField.GetValue(wallet)!, true,
                    count.ToString(), size.ToString(), ((int)feeRate).ToString(), false, false
                };
                var result = method.Invoke(null, args)!;
                walletField.SetValue(wallet, args[0]);

                var resultType = result.GetType();
                Assert.Equal("Ok", resultType.GetField("result")!.GetValue(result)!.ToString());
                return 0;
            }).GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            Service.UnloadWallet(WalletId);
            Service.Dispose();
            await _harness.DisposeAsync();
            Environment.SetEnvironmentVariable(ElectrumUrlEnvironmentVariable, _previousElectrumUrl);
        }
    }
}
