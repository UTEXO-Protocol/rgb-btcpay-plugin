using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// A REAL PSBT captured from rgb-lib 0.3.0-beta.30's create_utxos_begin, on regtest, against a
// throwaway wallet funded with 0.01 regtest BTC. Every other test in this area builds synthetic
// PSBTs, so this is the only one that pins the guard against what rgb-lib actually emits.
//
// Capturing it caught two shapes a plausible implementation would have got wrong, and both would
// have refused every Create-UTXOs on every wallet — including the unattended listener-driven path,
// which is the only way colorable UTXOs get created and therefore the only way RGB can be received:
//
//   1. The origin's path is 86'/1'/0'/1/0 — chain **1**, the change chain, not chain 0.
//   2. The origin lives in HDTaprootKeyPaths, not HDKeyPaths.
//
// The wallet is disposable and holds only regtest coins; the mnemonic is committed deliberately so
// the fixture is self-contained.
public class RgbLiveCreateUtxosPsbtFixtureTests
{
    const string ThrowawayRegtestMnemonic =
        "spell identify maximum immense version tunnel rely side wrestle old already stand";

    // rgb-lib 0.3.0-beta.30, create_utxos_begin(num=4, size=1000, fee_rate=2), captured 2026-08-19.
    const string LiveCreateUtxosPsbt = "cHNidP8BAP0KAQIAAAABYDBmgcXXdj19+WfcKt1N9KQFJxlaBvH9ncT3UX+lidkAAAAAAP3///8FaTAPAAAAAAAiUSAxCduniMg3kfTWKMruwH73nDP9jH3EEfNmb8gPKFeMJ+gDAAAAAAAAIlEgI6eJhyOtV6wQ0sntKuFp3croAEUPdDLHz363eW4vaw/oAwAAAAAAACJRIFzbtxPk3n7r7lWQiFgkITzmDqtzEmfSMI9SwxHCpCFz6AMAAAAAAAAiUSCk1llg/9QtqWmW6Zbvf8eE/5HG7fLB8slfnAzeKv2fB+gDAAAAAAAAIlEgk0bSmVGk1pQnDynoHpopOQzznY7SQeRhKRcTb/Bg/jHEZwEAAAEBK0BCDwAAAAAAIlEgjLFms346jk1kKsRFvCmpUlhPOwMvppPkJKPd1uzYC4whFgWHLWKEdLxi/CD6vRtQxp7P7zuXE37mvR6FMj3kk9dxGQDCePoXVgAAgAEAAIAAAACAAQAAAAAAAAABFyAFhy1ihHS8Yvwg+r0bUMaez+87lxN+5r0ehTI95JPXcQABBSCYn17k1mv5dqYteo6YjWvUyAplgXB/2urZ+Yw+57RadSEHmJ9e5NZr+XamLXqOmI1r1MgKZYFwf9rq2fmMPue0WnUZAMJ4+hdWAACAAQAAgAAAAIABAAAAAQAAAAABBSCk4h8Hk9Bl7WahRAyJpmspD/Uev29I2P/O/vdmSoNrsiEHpOIfB5PQZe1moUQMiaZrKQ/1Hr9vSNj/zv73ZkqDa7IZAMJ4+hdWAACAH58MgAAAAIAAAAAAAwAAAAABBSBNRxdRCUNnsSxR/6X1pcWzkgjQghweSWMO6UNr+oIiYSEHTUcXUQlDZ7EsUf+l9aXFs5II0IIcHkljDulDa/qCImEZAMJ4+hdWAACAH58MgAAAAIAAAAAAAAAAAAABBSBI+DmCN8YoRm228yYbCMEUo0dHECKBZYf9g4OQLsrQISEHSPg5gjfGKEZttvMmGwjBFKNHRxAigWWH/YODkC7K0CEZAMJ4+hdWAACAH58MgAAAAIAAAAAAAQAAAAABBSAcClpQxaUzmAxfrHivd0aDkWUmnRzy1gIaod/a/zWGDSEHHApaUMWlM5gMX6x4r3dGg5FlJp0c8tYCGqHf2v81hg0ZAMJ4+hdWAACAH58MgAAAAIAAAAAAAgAAAAA=";

    const string WalletAddress = "bcrt1p3jckdvm7828y6ep2c3zmc2df2fvy7wcr97nf8epy50wadmxcpwxqsn2whc";

    static SigningPolicy CreateUtxosPolicy(Network network) => new()
    {
        // Mirrors CreateColorableUtxosInternalAsync's real policy.
        MaxUnknownOutputSats = 0,
        MaxFeeSats = 50_000,
        AllowedScripts = new HashSet<Script> { BitcoinAddress.Create(WalletAddress, network).ScriptPubKey },
        MaxOutputCount = 5,
        RequireRgbVanillaKeychainInputs = true
    };

    [Fact]
    public async Task LiveRgbLibCreateUtxosPsbt_IsAcceptedByTheGuard()
    {
        var network = Network.RegTest;
        using var signer = new MemoryWalletSigner(ThrowawayRegtestMnemonic, network);
        var signed = await signer.SignPsbtAsync(LiveCreateUtxosPsbt, network, CreateUtxosPolicy(network));
        Assert.False(string.IsNullOrWhiteSpace(signed));
    }

    // Pins the two shapes above as properties of what rgb-lib emits, so a future change to the guard
    // that stops handling either one fails here with a diagnosis rather than in production.
    [Fact]
    public void LiveRgbLibCreateUtxosPsbt_CarriesATaprootOriginOnTheVanillaAccountChangeChain()
    {
        var network = Network.RegTest;
        using var signer = new MemoryWalletSigner(ThrowawayRegtestMnemonic, network);
        var psbt = PSBT.Parse(LiveCreateUtxosPsbt, network);

        var input = Assert.Single(psbt.Inputs);
        Assert.NotNull(input.WitnessUtxo);
        Assert.Null(input.NonWitnessUtxo);
        Assert.Empty(input.HDKeyPaths);

        var origin = Assert.Single(input.HDTaprootKeyPaths).Value.RootedKeyPath;
        Assert.Equal(signer.MasterFingerprint, origin.MasterFingerprint.ToString(), ignoreCase: true);
        Assert.Equal("86'/1'/0'/1/0", origin.KeyPath.ToString());

        Assert.True(signer.TryVerifyClaimedPath(
            input.GetTxOut()!.ScriptPubKey, origin.KeyPath, network, out var account));
        Assert.Equal(MemoryWalletSigner.PrevoutAccount.RgbLibVanilla, account);
    }
}
