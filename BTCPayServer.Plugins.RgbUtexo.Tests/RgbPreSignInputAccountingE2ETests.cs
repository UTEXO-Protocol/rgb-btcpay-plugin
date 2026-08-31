using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.RgbUtexo.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public sealed class RgbPreSignInputAccountingE2ETests
{
    const string FixtureSet = "BTCPayServer.Plugins.RgbUtexo.Tests/fixtures/finding-b-input-accounting";
    const string StockWitnessVoutSuccessor = "stock-producer-witness-vout-successor";
    const string StockConcreteOutpointSuccessor = "stock-producer-concrete-outpoint-successor";
    const string HostilePatchedProducerTwoOutputCarryForward = "hostile-patched-producer-two-output-carry-forward";

    [IntegrationFact]
    public async Task RealCoSpendOfTwoContracts_AccountsBothInputsAndTheIntentGateAcceptsIt()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(StockWitnessVoutSuccessor);
        var result = Validate(fixture, indexerUrl, fascia: null, opretCommitmentBytes: null);

        Assert.True(result.InputsAccounted,
            "the exhaustive input scan must account every allocation on every prevout of a real "
            + "two-contract co-spend, or Finding 1's burn is reachable");
        Assert.Equal(
            new[] { fixture.CoSpentForeignContractId, fixture.SentContractId }.OrderBy(x => x, StringComparer.Ordinal),
            result.CommittedContractIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            result.CommittedContractIds.OrderBy(x => x, StringComparer.Ordinal),
            result.VerifiedContractIds.OrderBy(x => x, StringComparer.Ordinal));

        var carry = Assert.Single(result.CarryForwards);
        Assert.Equal(fixture.CoSpentForeignContractId, carry.ContractId);
        Assert.Equal(fixture.CoSpentPrevout, carry.InputOutpoint);
        Assert.Contains(fixture.CoSpentPrevout, result.Prevouts);

        var decoded = RgbVerifyNative.DecodeInvoice(fixture.RgbInvoiceNamingTheSentContract);
        using var signer = new MemoryWalletSigner(
            fixture.ThrowawayRegtestMnemonicNeverFundedOnAnyLiveNetwork, Network.RegTest);
        using var chainClient = new UnreachedChainClient(
            "the witness-vout fixture has no concrete-outpoint leg and no concrete-outpoint carry-forward "
            + "successor, so the intent verifier must never need to resolve a funding transaction");

        await RgbIntentVerifier.VerifyAsync(
            decoded,
            result,
            PSBT.Parse(ReadPsbtBase64(StockWitnessVoutSuccessor), Network.RegTest),
            fixture.UnsignedTxid,
            signer,
            Network.RegTest,
            fixture.OperatorApprovedAmount,
            fixture.SentContractId,
            decoded.Transports,
            chainClient);
    }

    [IntegrationFact]
    public void ReCommittedFasciaOmittingTheForeignContractsConsumer_IsRejectedAsUnaccounted()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(StockWitnessVoutSuccessor);
        var mutation = fixture.Mutations["omittedForeignConsumer"];

        var error = Assert.Throws<RgbIntentVerificationException>(
            () => Validate(fixture, indexerUrl, mutation.FasciaFile, mutation.ReCommittedOpretCommitmentBytes));

        Assert.Contains("has no fascia consumer", error.Message);
    }

    [IntegrationFact]
    public void OmittingTheForeignConsumerWithoutReCommitting_DiesOnTheCommitmentCheckAndCertifiesNothing()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(StockWitnessVoutSuccessor);
        var mutation = fixture.Mutations["omittedForeignConsumer"];

        var error = Assert.Throws<RgbIntentVerificationException>(
            () => Validate(fixture, indexerUrl, mutation.FasciaFile, opretCommitmentBytes: null));

        Assert.Contains("opret commitment does not commit the complete fascia", error.Message);
        Assert.DoesNotContain("has no fascia consumer", error.Message);
    }

    [IntegrationFact]
    public void FasciaThatChangesTheForeignContractsCarriedAmount_IsRejectedByRgbConsensus()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(StockWitnessVoutSuccessor);
        var mutation = fixture.Mutations["changedForeignAmount"];

        var error = Assert.Throws<RgbIntentVerificationException>(
            () => Validate(fixture, indexerUrl, mutation.FasciaFile, mutation.ReCommittedOpretCommitmentBytes));

        Assert.Contains("validation failed", error.Message);
        Assert.Contains(fixture.CoSpentForeignContractId, error.Message);
    }

    [IntegrationFact]
    public void HostilePatchedProducerSplittingTheCarryForwardAcrossTwoOutputs_IsRejectedByTheOneOutputNormalForm()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(HostilePatchedProducerTwoOutputCarryForward);
        Assert.StartsWith("HOSTILE.", fixture.Producer);

        var error = Assert.Throws<RgbIntentVerificationException>(
            () => Validate(fixture, indexerUrl, fascia: null, opretCommitmentBytes: null));

        Assert.Contains("has 2 outputs, expected one", error.Message);
    }

    [IntegrationFact]
    public void ConcreteOutpointSuccessor_IsProvenAgainstTheWalletsOwnBdkStore()
    {
        var indexerUrl = RequireCurrentVerifierBinaryAndReachableIndexer();
        var fixture = Load(StockConcreteOutpointSuccessor);
        var result = Validate(fixture, indexerUrl, fascia: null, opretCommitmentBytes: null);

        Assert.True(result.InputsAccounted, "the concrete-successor co-spend must also account every input");
        var carry = Assert.Single(result.CarryForwards);
        Assert.Equal(fixture.ExpectedCarryForwardSuccessorKind, carry.SuccessorKind);
        Assert.Equal(fixture.ExpectedCarryForwardSuccessorOutpoint, carry.SuccessorOutpoint);
        Assert.Equal(fixture.ExpectedCarryForwardDerivationPath, carry.DerivationPath);
        Assert.False(string.IsNullOrWhiteSpace(carry.DerivationPath),
            "a derivation path can only be reported by resolving the successor script against the "
            + "wallet's own bdk_db_watch_only store, so a populated path is the proof that the BDK "
            + "descriptor authentication path ran");
    }

    static string RequireCurrentVerifierBinaryAndReachableIndexer()
    {
        RequireVerifierBuiltFromCurrentNativeSource();
        return RgbRegtestStackGate.RequireReachableIndexer();
    }

    static RgbValidateV2Result Validate(
        E2EFixture fixture, string indexerUrl, string? fascia, string? opretCommitmentBytes)
    {
        var walletDir = FixtureDir(fixture.Directory);
        var snapshot = RgbStockDurability.SnapshotVerificationState(Path.Combine(walletDir, "rgb"), walletDir);
        try
        {
            return RgbVerifyNative.ValidateV2(new RgbValidateV2Request
            {
                ConsignmentPath = Path.Combine(walletDir, "consignment_out"),
                FasciaPath = Path.Combine(walletDir, fascia ?? "fascia"),
                UnsignedTxid = fixture.UnsignedTxid,
                OpretCommitmentBytes = opretCommitmentBytes ?? fixture.OpretCommitmentBytes,
                Entropy = fixture.Entropy,
                IndexerUrl = indexerUrl,
                Network = fixture.ChainNetPrefix,
                StockDir = snapshot.StockDir,
                BdkStorePath = snapshot.BdkStorePath,
                AccountXpubVanilla = fixture.AccountXpubVanilla,
                AccountXpubColored = fixture.AccountXpubColored,
                MasterFingerprint = fixture.MasterFingerprint
            });
        }
        finally
        {
            RgbStockDurability.DeleteSnapshot(snapshot.RootDir);
        }
    }

    static void RequireVerifierBuiltFromCurrentNativeSource()
    {
        var sourceDir = Path.Combine(PluginCompilation.RepoRootPath, "native", "rgb-verify", "src");
        Assert.True(Directory.Exists(sourceDir), $"native verifier source directory not found: {sourceDir}");

        var loadedPath = LoadedVerifierLibraryPath();
        var loadedWrittenUtc = File.GetLastWriteTimeUtc(loadedPath);

        var newest = Directory.EnumerateFiles(sourceDir, "*.rs", SearchOption.AllDirectories)
            .Select(path => new { Path = path, WrittenUtc = File.GetLastWriteTimeUtc(path) })
            .OrderByDescending(entry => entry.WrittenUtc)
            .First();

        Assert.True(loadedWrittenUtc >= newest.WrittenUtc,
            $"the librgbverifycffi this test loaded is STALE. Loaded {loadedPath} built {loadedWrittenUtc:O}, "
            + $"but {newest.Path} was last written {newest.WrittenUtc:O}. Every assertion in this class would "
            + "be measuring the previous build of the trust core rather than the source in this working tree, "
            + "so a pass would certify nothing. Rebuild and republish it: cd native/rgb-verify && "
            + "cargo build --release, copy target/release/librgbverifycffi.* over "
            + "native/rgb-verify/runtimes/<rid>/native/, then rebuild the test project.");
    }

    static string LoadedVerifierLibraryPath()
    {
        var baseDir = RgbVerifyNative.ResolveBaseDir(typeof(RgbVerifyNative).Assembly);
        var loaded = RgbVerifyNative.TryLoadFromCandidates(
            baseDir, out _, out var winningPath, out var searched, out var existedButFailed);

        Assert.True(loaded,
            $"librgbverifycffi did not load from any candidate under {baseDir}. Searched: "
            + $"{string.Join(", ", searched)}. Present but unloadable: "
            + $"{(existedButFailed.Count == 0 ? "none" : string.Join(", ", existedButFailed))}.");
        return winningPath!;
    }

    static string FixtureDir(string directory)
    {
        var path = Path.Combine(PluginCompilation.RepoRootPath, FixtureSet, directory);
        Assert.True(Directory.Exists(path), $"fixture directory not found: {path}");
        return path;
    }

    static string ReadPsbtBase64(string directory)
        => File.ReadAllText(Path.Combine(FixtureDir(directory), "unsigned.psbt")).Trim().Trim('"');

    static E2EFixture Load(string directory)
    {
        var path = Path.Combine(FixtureDir(directory), "fixture.json");
        var fixture = JsonSerializer.Deserialize<E2EFixture>(File.ReadAllText(path))
                      ?? throw new InvalidOperationException($"unparseable fixture descriptor: {path}");
        fixture.Directory = directory;
        return fixture;
    }

    sealed class E2EFixture
    {
        [JsonIgnore] public string Directory { get; set; } = "";
        [JsonPropertyName("producer")] public string Producer { get; set; } = "";
        [JsonPropertyName("sentContractId")] public string SentContractId { get; set; } = "";
        [JsonPropertyName("coSpentForeignContractId")] public string CoSpentForeignContractId { get; set; } = "";
        [JsonPropertyName("coSpentPrevout")] public string CoSpentPrevout { get; set; } = "";
        [JsonPropertyName("unsignedTxid")] public string UnsignedTxid { get; set; } = "";
        [JsonPropertyName("opretCommitmentBytes")] public string OpretCommitmentBytes { get; set; } = "";
        [JsonPropertyName("entropy")] public ulong Entropy { get; set; }
        [JsonPropertyName("chainNetPrefix")] public string ChainNetPrefix { get; set; } = "";
        [JsonPropertyName("accountXpubVanilla")] public string AccountXpubVanilla { get; set; } = "";
        [JsonPropertyName("accountXpubColored")] public string AccountXpubColored { get; set; } = "";
        [JsonPropertyName("masterFingerprint")] public string MasterFingerprint { get; set; } = "";

        [JsonPropertyName("throwawayRegtestMnemonicNeverFundedOnAnyLiveNetwork")]
        public string ThrowawayRegtestMnemonicNeverFundedOnAnyLiveNetwork { get; set; } = "";

        [JsonPropertyName("rgbInvoiceNamingTheSentContract")]
        public string RgbInvoiceNamingTheSentContract { get; set; } = "";

        [JsonPropertyName("operatorApprovedAmount")] public long OperatorApprovedAmount { get; set; }

        [JsonPropertyName("expectedCarryForwardSuccessorKind")]
        public string ExpectedCarryForwardSuccessorKind { get; set; } = "";

        [JsonPropertyName("expectedCarryForwardSuccessorOutpoint")]
        public string? ExpectedCarryForwardSuccessorOutpoint { get; set; }

        [JsonPropertyName("expectedCarryForwardDerivationPath")]
        public string? ExpectedCarryForwardDerivationPath { get; set; }

        [JsonPropertyName("mutations")]
        public Dictionary<string, E2EFasciaMutation> Mutations { get; set; } = [];
    }

    sealed class E2EFasciaMutation
    {
        [JsonPropertyName("fasciaFile")] public string FasciaFile { get; set; } = "";

        [JsonPropertyName("reCommittedOpretCommitmentBytes")]
        public string? ReCommittedOpretCommitmentBytes { get; set; }
    }

    sealed class UnreachedChainClient : IBitcoinChainClient
    {
        readonly string _why;

        internal UnreachedChainClient(string why) => _why = why;

        public Task ConnectAsync(CancellationToken ct = default) => throw Unreached();

        public Task<string> GetRawTransactionAsync(string txid, CancellationToken ct = default) => throw Unreached();

        public Task<string> BroadcastTransactionAsync(string rawTxHex, CancellationToken ct = default) => throw Unreached();

        public Task<IReadOnlyList<UnspentWithConfirmation>> ListUnspentWithConfirmationByScriptAsync(
            Script script, CancellationToken ct = default) => throw Unreached();

        public void Dispose() { }

        InvalidOperationException Unreached() => new($"chain client reached unexpectedly: {_why}");
    }
}
