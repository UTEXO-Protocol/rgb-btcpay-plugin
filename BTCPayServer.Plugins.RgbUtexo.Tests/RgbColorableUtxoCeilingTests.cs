using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbColorableUtxoCeilingTests
{
    const string WalletId = "w1";

    sealed class NonRetryingDbContextFactory : RGBPluginDbContextFactory
    {
        public NonRetryingDbContextFactory(IOptions<DatabaseOptions> options) : base(options) { }

        public override RGBPluginDbContext CreateContext(
            Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
            => base.CreateContext(o =>
            {
                o.ExecutionStrategy(d => new NonRetryingExecutionStrategy(d));
                npgsqlOptionsAction?.Invoke(o);
            });
    }

    sealed class CountingRgbLib : IRgbLibService
    {
        readonly RGBConfiguration _cfg;
        readonly int _colorable;
        readonly int _vanilla;

        public CountingRgbLib(RGBConfiguration cfg, int colorable, int vanilla = 1)
        {
            _cfg = cfg;
            _colorable = colorable;
            _vanilla = vanilla;
        }

        public int ListUnspentsCalls { get; private set; }
        public int CreateUtxosBeginCalls { get; private set; }

        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default)
        {
            ListUnspentsCalls++;
            var outputs = new List<UnspentOutput>();
            for (var i = 0; i < _colorable; i++)
                outputs.Add(new UnspentOutput(
                    new UtxoInfo { Outpoint = new Outpoint($"c{i}", 0), BtcAmount = 1000, Colorable = true },
                    []));
            for (var i = 0; i < _vanilla; i++)
                outputs.Add(new UnspentOutput(
                    new UtxoInfo
                    {
                        Outpoint = new Outpoint($"v{i}", 0), BtcAmount = 100_000, Colorable = false
                    },
                    []));
            return Task.FromResult(outputs);
        }

        public Task<string> CreateUtxosBeginAsync(string walletId, int count, int size, float feeRate,
            CancellationToken ct = default)
        {
            CreateUtxosBeginCalls++;
            throw new NotImplementedException();
        }

        public string GetWalletDataDir(string walletId, string walletNetwork)
            => _cfg.GetWalletDataDir(walletId, walletNetwork);

        public RgbKeys RestoreKeysFromMnemonic(string mnemonic, string network)
            => throw new NotImplementedException();

        public void Dispose() { }

        public Task<RgbLibWalletHandle> GetOrCreateWalletAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public bool UnloadWallet(string walletId) => throw new NotImplementedException();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw new NotImplementedException();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvoiceResponse> BlindReceiveAsync(string walletId, string? assetId, long? amount, long? expiration, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateUtxosEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbTransfer>> ListTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<RgbMatchedTransfer>> ListIncomingTransfersForRecipientsAsync(
            string walletId, IReadOnlyCollection<string> recipientIds, string? assetId = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task RefreshAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SnapshotStockAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbVerificationSnapshot> SnapshotVerificationStateAsync(string walletId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RgbAsset> IssueAssetNiaAsync(string walletId, string ticker, string name, List<long> amounts, int precision, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SendBeginAsync(string walletId, string recipientMapJson, float feeRate, int minConfirmations = 1, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SendEndAsync(string walletId, string signedPsbt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateConsignmentsAsync(string walletId, string psbt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task FailTransfersAsync(string walletId, int batchTransferIdx, bool noAssetOnly, bool skipSync, CancellationToken ct = default) => throw new NotImplementedException();
        public RgbInvoiceData DecodeInvoice(string invoiceString) => throw new NotImplementedException();
        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default) => throw new NotImplementedException();
        public RgbKeys GenerateKeys(string network) => throw new NotImplementedException();
    }

    static (RGBWalletService Svc, CountingRgbLib Lib) BuildService(
        int standingColorable, int? manualCeiling = null, int? autoCap = null)
    {
        var cfg = new RGBConfiguration(Path.Combine(Path.GetTempPath(), $"rgb-ceiling-{Guid.NewGuid():N}"));
        if (manualCeiling.HasValue) cfg.MaxManualColorableUtxos = manualCeiling.Value;
        if (autoCap.HasValue) cfg.MaxAutoColorableUtxos = autoCap.Value;
        var db = new NonRetryingDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString = "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var lib = new CountingRgbLib(cfg, standingColorable);
        var svc = new RGBWalletService(lib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, null!, null!);
        return (svc, lib);
    }

    [Fact]
    public void RoomExistsWhileTheRequestFitsUnderTheCeiling()
    {
        RGBWalletService.EnsureStandingColorableRoom(46, 4, 50);
        RGBWalletService.EnsureStandingColorableRoom(0, 4, 50);
        RGBWalletService.EnsureStandingColorableRoom(49, 1, 50);
    }

    [Fact]
    public void OneMoreThanTheCeilingIsRefusedRatherThanSilentlyClamped()
    {
        var ex = Assert.Throws<RgbColorableUtxoCeilingReachedException>(
            () => RGBWalletService.EnsureStandingColorableRoom(47, 4, 50));

        Assert.Contains("refusing to create 4 more colorable UTXOs", ex.Message);
        Assert.Contains("already holds 47", ex.Message);
        Assert.Contains("the manual ceiling is 50", ex.Message);
        Assert.Contains("raise the manual ceiling to at least 51", ex.Message);
        Assert.Contains("RGB_MAX_MANUAL_COLORABLE_UTXOS", ex.Message);
        Assert.Contains("max_manual_colorable_utxos", ex.Message);
    }

    [Fact]
    public void AtTheCeilingTheRefusalNamesTheKnobThatEscapesIt()
    {
        var ex = Assert.Throws<RgbColorableUtxoCeilingReachedException>(
            () => RGBWalletService.EnsureStandingColorableRoom(50, 4, 50));
        Assert.Contains("raise the manual ceiling to at least 54", ex.Message);

        RGBWalletService.EnsureStandingColorableRoom(50, 4, 54);
    }

    [Fact]
    public void ALoweredCeilingRefusesGrowthButNeverTheFirstBatchFromAnEmptyPool()
    {
        Assert.Throws<RgbColorableUtxoCeilingReachedException>(
            () => RGBWalletService.EnsureStandingColorableRoom(50, 4, 4));

        foreach (var ceiling in new[] { int.MinValue, -1, 0, 1 })
            foreach (var requested in new[] { RgbConfigBounds.UtxoCountMin, 4, RgbConfigBounds.UtxoCountMax })
                RGBWalletService.EnsureStandingColorableRoom(0, requested, ceiling);
    }

    [Fact]
    public void ARequestThatCannotOverflowIsStillRefusedAtTheCeiling()
    {
        var ex = Assert.Throws<RgbColorableUtxoCeilingReachedException>(
            () => RGBWalletService.EnsureStandingColorableRoom(int.MaxValue, 20, 50));
        Assert.Contains($"raise the manual ceiling to at least {(long)int.MaxValue + 20}", ex.Message);
    }

    [Fact]
    public async Task ManualCreationAtTheCeilingRefusesBeforeAnyPsbtIsBuilt()
    {
        var (svc, lib) = BuildService(standingColorable: 50, manualCeiling: 50);

        var ex = await Assert.ThrowsAsync<RgbColorableUtxoCeilingReachedException>(
            () => svc.CreateColorableUtxosAsync(WalletId, 4, 1000));

        Assert.Contains("already holds 50", ex.Message);
        Assert.Equal(1, lib.ListUnspentsCalls);
        Assert.Equal(0, lib.CreateUtxosBeginCalls);
    }

    [Fact]
    public async Task ManualCreationCountsOnlyColorableUtxosTowardsTheCeiling()
    {
        var (svc, lib) = BuildService(standingColorable: 46, manualCeiling: 50);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.CreateColorableUtxosAsync(WalletId, 4, 1000));

        Assert.IsNotType<RgbColorableUtxoCeilingReachedException>(ex);
        Assert.Equal(1, lib.ListUnspentsCalls);
    }

    [Fact]
    public async Task ManualCreationFromAnEmptyPoolIsNeverPermanentlyRefused()
    {
        var (svc, lib) = BuildService(standingColorable: 0, manualCeiling: 1);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.CreateColorableUtxosAsync(WalletId, 4, 1000));

        Assert.IsNotType<RgbColorableUtxoCeilingReachedException>(ex);
        Assert.Equal(1, lib.ListUnspentsCalls);
    }

    [Fact]
    public async Task AutomaticCreationIsNotBoundedHereAndMakesNoExtraRgbLibCall()
    {
        var (svc, lib) = BuildService(standingColorable: 500, manualCeiling: 50, autoCap: 50);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.CreateColorableUtxosAutomaticallyAsync(
                WalletId, 4, 1000, _ => Task.FromResult(true)));

        Assert.IsNotType<RgbColorableUtxoCeilingReachedException>(ex);
        Assert.Equal(0, lib.ListUnspentsCalls);
        Assert.Equal(0, lib.CreateUtxosBeginCalls);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ManualCreationWithAStandingPoolSurvivesEveryAutomaticCapIncludingTheKillSwitch(
        int autoCap)
    {
        var (svc, lib) = BuildService(standingColorable: 3, autoCap: autoCap);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.CreateColorableUtxosAsync(WalletId, 4, 1000));

        Assert.False(ex is RgbColorableUtxoCeilingReachedException,
            $"with MaxAutoColorableUtxos = {autoCap} and 3 colorable UTXOs standing, the manual Create "
            + "UTXOs button refused with " + ex.GetType().Name + ": " + ex.Message + ". That cap bounds "
            + "AUTOMATIC creation only — EvaluateReplenishDemand already returns SkipCapReached for any "
            + "value <= 0 — so reading it here leaves no path at all, manual or automatic, by which this "
            + "wallet can be given a colorable UTXO, and SendBtcInternalAsync filters !Colorable so the "
            + "standing pool cannot be recycled either. The manual path must consult "
            + "MaxManualColorableUtxos instead.");
        Assert.Equal(1, lib.ListUnspentsCalls);
    }

    [Fact]
    public async Task TheShippedNoticeTellingOperatorsToProvisionManuallyIsATrueStatement()
    {
        var cause = RgbReplenishmentNotice.Evaluate(
            paymentMethodEnabled: true, hasStoredConfig: true, configValuesValid: true,
            maxAutoColorableUtxos: 0, standingAuthorizationGranted: false);
        Assert.Equal(RgbReplenishmentNoticeCause.CapDisabledDeploymentWide, cause);
        Assert.Contains("Press Create UTXOs to provision manually",
            RgbReplenishmentNotice.MessageFor(cause));

        var (svc, lib) = BuildService(standingColorable: 7, autoCap: 0);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => svc.CreateColorableUtxosAsync(WalletId, 4, 1000));

        Assert.False(ex is RgbColorableUtxoCeilingReachedException,
            "the notice rendered at MaxAutoColorableUtxos = 0 instructs the operator to press Create "
            + "UTXOs, and pressing it with a non-empty pool refused with " + ex.Message + ". A shipped "
            + "instruction that cannot be acted on is the defect, not the notice.");
        Assert.Equal(1, lib.ListUnspentsCalls);
    }

    [Fact]
    public void TheManualCeilingRefusesExactlyTheBatchesThatCrossItAndNeverAnEmptyPool()
    {
        int[] standings = [0, 1, 2, 4, 20, 50, 249, 250, 251, int.MaxValue];
        int[] ceilings = [int.MinValue, -1, 0, 1, 2, 4, 20, 50, 250, int.MaxValue];
        int[] requests = [RgbConfigBounds.UtxoCountMin, 2, 4, RgbConfigBounds.UtxoCountMax];
        var refusals = 0;
        var permits = 0;

        foreach (var standing in standings)
            foreach (var ceiling in ceilings)
                foreach (var requested in requests)
                {
                    var effective = Math.Max((long)ceiling, requested);
                    var mustRefuse = standing + (long)requested > effective;

                    if (standing == 0)
                        Assert.False(mustRefuse,
                            $"an empty pool asking for {requested} under a ceiling of {ceiling} is "
                            + "expected to be permitted: refusing there is a permanent false-reject, "
                            + "because a wallet with no colorable UTXO can neither receive an RGB asset "
                            + "nor drain anything to make room.");

                    if (mustRefuse)
                    {
                        refusals++;
                        var ex = Assert.Throws<RgbColorableUtxoCeilingReachedException>(
                            () => RGBWalletService.EnsureStandingColorableRoom(
                                standing, requested, ceiling));
                        Assert.Contains("RGB_MAX_MANUAL_COLORABLE_UTXOS", ex.Message);
                        Assert.Contains(
                            $"raise the manual ceiling to at least {standing + (long)requested}",
                            ex.Message);
                    }
                    else
                    {
                        permits++;
                        var refused = Record.Exception(
                            () => RGBWalletService.EnsureStandingColorableRoom(
                                standing, requested, ceiling));
                        Assert.True(refused == null,
                            $"{standing} standing + {requested} requested fits the effective ceiling of "
                            + $"{effective} (configured {ceiling}, floored at one batch) and must be "
                            + $"permitted; it refused with: {refused?.Message}");
                    }
                }

        Assert.True(refusals >= 100 && permits >= 100,
            $"the cross-product exercised {refusals} refusals and {permits} permits; both regions must "
            + "be populated or this pin proves only one direction. A ceiling that never refuses is not a "
            + "bound, and a ceiling that always refuses is a lockout.");
    }
}
