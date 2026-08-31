using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbCheckoutScanTieringTests
{
    const string Wallet = "w1";
    static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
    static readonly long HotFloorUnix = (Now - TimeSpan.FromHours(72)).ToUnixTimeSeconds();
    static readonly long MonitoringFloorUnix =
        RGBInvoiceListener.MonitoringFloorUnix(Now.ToUnixTimeSeconds());

    static RGBInvoice Row(
        string id,
        DateTimeOffset createdAt,
        RGBInvoiceStatus status = RGBInvoiceStatus.Pending,
        string? assetId = "rgb:asset",
        string walletId = Wallet,
        bool withExpiry = true,
        DateTimeOffset? monitoringExpiration = null) =>
        new()
        {
            Id = id,
            WalletId = walletId,
            RecipientId = "utxob:" + id,
            AssetId = assetId,
            Status = status,
            CreatedAt = createdAt,
            ExpirationTimestamp = withExpiry ? createdAt.AddMinutes(15).ToUnixTimeSeconds() : null,
            MonitoringExpirationTimestamp = monitoringExpiration?.ToUnixTimeSeconds()
        };

    static IQueryable<RGBInvoice> ScanSet(IEnumerable<RGBInvoice> rows) =>
        rows.AsQueryable().Where(RGBInvoiceListener.CheckoutSettlementScanPredicate(Wallet));

    static List<RGBInvoice> Newest(IEnumerable<RGBInvoice> rows) =>
        RGBInvoiceListener.NewestCheckoutInvoiceSlice(ScanSet(rows)).ToList();

    static List<RGBInvoice> Hot(IEnumerable<RGBInvoice> rows, string? cursor = null) =>
        RGBInvoiceListener.HotCheckoutInvoicePage(
            ScanSet(rows), HotFloorUnix, MonitoringFloorUnix, cursor).ToList();

    static List<RGBInvoice> Cold(IEnumerable<RGBInvoice> rows, string? cursor = null) =>
        RGBInvoiceListener.ColdCheckoutInvoicePage(
            ScanSet(rows), HotFloorUnix, MonitoringFloorUnix, cursor).ToList();

    static List<RGBInvoice> Sweep(IEnumerable<RGBInvoice> rows, string? hotCursor, string? coldCursor) =>
        Newest(rows).Concat(Hot(rows, hotCursor)).Concat(Cold(rows, coldCursor))
            .DistinctBy(i => i.Id, StringComparer.Ordinal).ToList();

    static List<RGBInvoice> DeadBacklog(int count) =>
        Enumerable.Range(0, count)
            .Select(n => Row($"dead-{n:D6}", Now - TimeSpan.FromDays(400) + TimeSpan.FromMinutes(n)))
            .ToList();

    static List<RGBInvoice> InFlight(int count) =>
        Enumerable.Range(0, count)
            .Select(n => Row($"live-{n:D6}", Now - TimeSpan.FromHours(1) - TimeSpan.FromSeconds(n)))
            .ToList();

    [Fact]
    public void AFreshInvoiceIsInspectedOnTheNextSweepDespiteATenThousandRowBacklog()
    {
        var rows = DeadBacklog(10_000);
        var fresh = Row("zzzz-fresh", Now - TimeSpan.FromSeconds(5));
        rows.Insert(0, fresh);

        Assert.Contains(fresh, Sweep(rows, hotCursor: null, coldCursor: null));
    }

    [Fact]
    public void AFreshInvoiceIsInspectedOnTheNextSweepEvenWhileBothCursorsAreMidWrap()
    {
        var rows = DeadBacklog(5_000);
        rows.AddRange(InFlight(500));
        var fresh = Row("zzzz-fresh", Now - TimeSpan.FromSeconds(5));
        rows.Add(fresh);

        var midWrap = Sweep(rows, hotCursor: "live-000250", coldCursor: "dead-002500");

        Assert.Contains(fresh, midWrap);
        Assert.Same(fresh, Newest(rows)[0]);
    }

    [Fact]
    public void TheHotWrapTimeHasNoLifetimeTermSoADeadBacklogCannotSlowItDown()
    {
        var inFlight = InFlight(120);
        var withBacklog = inFlight.Concat(DeadBacklog(20_000)).ToList();

        Assert.Equal(HotSweepsToVisitAll(inFlight, inFlight.Count),
            HotSweepsToVisitAll(withBacklog, inFlight.Count));
    }

    [Fact]
    public void EveryStillPayableInvoiceIsVisitedWithinItsInFlightCountDividedByTheHotPage()
    {
        var inFlight = InFlight(120);
        var rows = inFlight.Concat(DeadBacklog(20_000)).ToList();

        var sweeps = HotSweepsToVisitAll(rows, inFlight.Count);
        var bound = (inFlight.Count + RGBInvoiceListener.DurableInvoiceHotPageSize - 1)
                    / RGBInvoiceListener.DurableInvoiceHotPageSize;

        Assert.True(sweeps <= bound,
            $"the still-payable tier took {sweeps} sweeps to wrap, above the in-flight bound {bound}; "
            + "a lifetime-count term has crept back into payment-detection latency");
    }

    static int HotSweepsToVisitAll(IEnumerable<RGBInvoice> rows, int expectedVisits)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var sweep = 1; sweep <= 1_000; sweep++)
        {
            var page = Hot(rows, cursor);
            foreach (var row in page) visited.Add(row.Id);
            cursor = DurableInvoiceScan.NextCursor(
                page.Select(r => r.Id).ToList(), RGBInvoiceListener.DurableInvoiceHotPageSize);
            if (visited.Count >= expectedVisits) return sweep;
            if (cursor == null) return sweep;
        }
        return int.MaxValue;
    }

    [Fact]
    public void TheColdTailKeepsItsOwnCursorSoNothingIsEverPermanentlyUnvisited()
    {
        var rows = DeadBacklog(200);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (var sweep = 0; sweep < 100; sweep++)
        {
            var page = Cold(rows, cursor);
            if (page.Count == 0) break;
            foreach (var row in page) visited.Add(row.Id);
            cursor = DurableInvoiceScan.NextCursor(
                page.Select(r => r.Id).ToList(), RGBInvoiceListener.DurableInvoiceColdPageSize);
            if (cursor == null) break;
        }

        Assert.Equal(rows.Count, visited.Count);
    }

    [Fact]
    public void TheTwoCursoredTiersPartitionTheScanSetSoNeitherWastesTheOthersBudget()
    {
        var rows = InFlight(10).Concat(DeadBacklog(10)).ToList();

        Assert.All(Hot(rows), row => Assert.True(row.ExpirationTimestamp > HotFloorUnix));
        Assert.All(Hot(rows), row => Assert.Null(row.MonitoringExpirationTimestamp));
        Assert.All(Cold(rows), row =>
            Assert.True(row.ExpirationTimestamp == null || row.ExpirationTimestamp <= HotFloorUnix,
                "a still-payable row must not consume the cold tail's budget"));
        Assert.Empty(Hot(rows).Select(r => r.Id).Intersect(Cold(rows).Select(r => r.Id)));
    }

    [Fact]
    public void ARowWithNoExpiryIsCoveredByTheColdTailAndTheNewestSliceRatherThanBeingHotForever()
    {
        var row = Row("no-expiry", Now, withExpiry: false);

        Assert.Empty(Hot([row]));
        Assert.Single(Cold([row]));
        Assert.Single(Newest([row]));
    }

    [Fact]
    public void OneSweepNeverExceedsTheBoundedRecipientQuerysWorkLimit()
    {
        Assert.True(
            RGBInvoiceListener.DurableInvoiceNewestSliceSize
            + RGBInvoiceListener.DurableInvoiceHotPageSize
            + RGBInvoiceListener.DurableInvoiceColdPageSize
            <= RGBInvoiceListener.DurableInvoicePageSize,
            "one sweep's rows all become recipients of the bounded rgb-lib transfer query, which throws "
            + "above DurableInvoicePageSize");

        var rows = InFlight(500).Concat(DeadBacklog(500)).ToList();
        Assert.True(Sweep(rows, null, null).Count <= RGBInvoiceListener.DurableInvoicePageSize);
    }

    [Fact]
    public void TheNewestSliceIsOrderedByRecencySoAnIdThatSortsFirstCannotDisplaceAFreshRow()
    {
        var rows = Enumerable.Range(0, 50)
            .Select(n => Row($"aaa-{n:D4}", Now - TimeSpan.FromDays(10) - TimeSpan.FromMinutes(n)))
            .ToList();
        var fresh = Row("zzz-newest", Now);
        rows.Add(fresh);

        Assert.Same(fresh, Newest(rows)[0]);
        Assert.Equal(RGBInvoiceListener.DurableInvoiceNewestSliceSize, Newest(rows).Count);
    }

    [Theory]
    [InlineData(RGBInvoiceStatus.Pending)]
    [InlineData(RGBInvoiceStatus.WaitingConfirmations)]
    [InlineData(RGBInvoiceStatus.Underpaid)]
    public void EveryNonTerminalCheckoutStatusStaysInTheScanSetForever(RGBInvoiceStatus status)
    {
        Assert.Single(Cold([Row("r1", Now - TimeSpan.FromDays(500), status)]));
    }

    [Theory]
    [InlineData(RGBInvoiceStatus.Settled)]
    [InlineData(RGBInvoiceStatus.Failed)]
    [InlineData(RGBInvoiceStatus.Expired)]
    public void TerminalRowsAreOutsideTheScanSet(RGBInvoiceStatus status)
    {
        Assert.Empty(Sweep([Row("r1", Now, status)], null, null));
    }

    [Fact]
    public void WalletUiReceiveRowsAndOtherWalletsRowsAreOutsideTheScanSet()
    {
        Assert.Empty(Sweep([Row("r1", Now, assetId: null)], null, null));
        Assert.Empty(Sweep([Row("r2", Now, walletId: "w2")], null, null));
    }

    [Fact]
    public void TheHotWindowAlwaysOutlastsTheStoresBtcPayMonitoringWindow()
    {
        var monitoring = TimeSpan.FromDays(24);
        var window = RGBInvoiceListener.ResolveHotScanWindow(monitoring, configuredWindowHours: 1);

        Assert.True(window > monitoring,
            $"window {window} must outlast the monitoring window {monitoring}, or an invoice BTCPay would "
            + "still credit is demoted to the cold tail");
        Assert.Equal(
            monitoring + TimeSpan.FromHours(RGBConfiguration.CheckoutInvoiceMonitoringSafetyMarginHours),
            window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(47)]
    [InlineData(int.MaxValue)]
    public void AConfiguredHotWindowOutsideItsBoundsIsClampedRatherThanHonoured(int configured)
    {
        var cfg = new RGBConfiguration { CheckoutInvoiceHotScanWindowHours = configured };
        Assert.InRange(cfg.CheckoutInvoiceHotScanWindowHours,
            RGBConfiguration.MinCheckoutInvoiceHotScanWindowHours,
            RGBConfiguration.MaxCheckoutInvoiceHotScanWindowHours);
    }

    [Fact]
    public void AnAbsurdConfiguredWindowCannotThrowInsteadOfBoundingTheScan()
    {
        Assert.Equal(
            TimeSpan.FromHours(RGBConfiguration.MaxCheckoutInvoiceHotScanWindowHours),
            RGBInvoiceListener.ResolveHotScanWindow(TimeSpan.Zero, int.MaxValue));
    }

    [Fact]
    public void TheDefaultHotWindowCoversBtcPaysDefaultMonitoringWindowPlusTheSafetyMargin()
    {
        Assert.Equal(
            TimeSpan.FromDays(1) + TimeSpan.FromHours(
                RGBConfiguration.CheckoutInvoiceMonitoringSafetyMarginHours),
            TimeSpan.FromHours(new RGBConfiguration().CheckoutInvoiceHotScanWindowHours));
    }

    [Fact]
    public void EveryTierQueryTranslatesEntirelyToServerSideSql()
    {
        var options = new DbContextOptionsBuilder<RGBPluginDbContext>()
            .UseNpgsql("Host=localhost;Database=none", o => o.SetPostgresVersion(12, 0))
            .Options;
        using var db = new RGBPluginDbContext(options);
        var scanSet = db.RGBInvoices.Where(RGBInvoiceListener.CheckoutSettlementScanPredicate(Wallet));

        var newest = RGBInvoiceListener.NewestCheckoutInvoiceSlice(scanSet).ToQueryString();
        Assert.Contains("ORDER BY r.\"CreatedAt\" DESC, r.\"Id\" DESC", newest, StringComparison.Ordinal);

        var hot = RGBInvoiceListener.HotCheckoutInvoicePage(
            scanSet, HotFloorUnix, MonitoringFloorUnix, "inv-1").ToQueryString();
        Assert.Equal(
            "WHERE r.\"WalletId\" = @walletId AND r.\"AssetId\" IS NOT NULL "
            + "AND r.\"Status\" IN (0, 1, 5) AND ((r.\"MonitoringExpirationTimestamp\" IS NOT NULL "
            + "AND r.\"MonitoringExpirationTimestamp\" > @monitoringFloorUnix) "
            + "OR (r.\"MonitoringExpirationTimestamp\" IS NULL "
            + "AND r.\"ExpirationTimestamp\" IS NOT NULL "
            + "AND r.\"ExpirationTimestamp\" > @hotScanFloorUnix)) AND r.\"Id\" > @cursor",
            hot[hot.IndexOf("WHERE ", StringComparison.Ordinal)
                ..hot.IndexOf("ORDER BY", StringComparison.Ordinal)].Trim());

        var cold = RGBInvoiceListener.ColdCheckoutInvoicePage(
            scanSet, HotFloorUnix, MonitoringFloorUnix, "inv-1").ToQueryString();
        Assert.Equal(
            "WHERE r.\"WalletId\" = @walletId AND r.\"AssetId\" IS NOT NULL "
            + "AND r.\"Status\" IN (0, 1, 5) AND ((r.\"MonitoringExpirationTimestamp\" IS NOT NULL "
            + "AND r.\"MonitoringExpirationTimestamp\" <= @monitoringFloorUnix) "
            + "OR (r.\"MonitoringExpirationTimestamp\" IS NULL "
            + "AND (r.\"ExpirationTimestamp\" IS NULL "
            + "OR r.\"ExpirationTimestamp\" <= @hotScanFloorUnix))) AND r.\"Id\" > @cursor",
            cold[cold.IndexOf("WHERE ", StringComparison.Ordinal)
                ..cold.IndexOf("ORDER BY", StringComparison.Ordinal)].Trim());
        Assert.Contains("ORDER BY r.\"Id\"", cold, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHotCursorIsPersistedAlongsideTheColdOneSoAWrapSurvivesRestart()
    {
        Assert.NotNull(typeof(RGBWallet).GetProperty(nameof(RGBWallet.HotInvoiceScanCursor)));
        Assert.NotNull(typeof(RGBWallet).GetProperty(nameof(RGBWallet.InvoiceScanCursor)));
    }

    [Fact]
    public void AnInvoiceMonitoredFarLongerThanTheStoreDefaultStaysHotUntilItsOwnDeadline()
    {
        var row = Row("long-monitoring", Now - TimeSpan.FromDays(10),
            monitoringExpiration: Now + TimeSpan.FromDays(4));

        Assert.Single(Hot([row]));
        Assert.Empty(Cold([row]));

        var storeDerived = RGBInvoiceListener.StillPayableCheckoutPredicate(
            HotFloorUnix, MonitoringFloorUnix).Compile();
        Assert.True(storeDerived(row),
            "BTCPay persists the monitoring deadline PER INVOICE and checkout.monitoringMinutes is not "
            + "bounded by the store setting, so a row whose own deadline is still open must stay in the "
            + "tier that is visited on an in-flight schedule");
    }

    [Fact]
    public void AnInvoiceWhoseOwnMonitoringDeadlinePassedLongAgoIsColdEvenThoughItWasCreatedRecently()
    {
        var row = Row("done-monitoring", Now - TimeSpan.FromMinutes(5),
            monitoringExpiration: Now - TimeSpan.FromDays(30));

        Assert.Empty(Hot([row]));
        Assert.Single(Cold([row]));
    }

    [Fact]
    public void ARowIsHotUntilItsOwnDeadlinePlusTheSafetyMarginAndColdImmediatelyAfter()
    {
        var margin = TimeSpan.FromHours(RGBConfiguration.CheckoutInvoiceMonitoringSafetyMarginHours);
        var justInside = Row("inside", Now, monitoringExpiration: Now - margin + TimeSpan.FromMinutes(1));
        var justOutside = Row("outside", Now, monitoringExpiration: Now - margin - TimeSpan.FromMinutes(1));

        Assert.Single(Hot([justInside]));
        Assert.Single(Cold([justOutside]));
    }

    [Fact]
    public void ALegacyRowWithNoMonitoringColumnIsPartitionedExactlyAsItIsToday()
    {
        var payable = Row("legacy-live", Now - TimeSpan.FromHours(1));
        var stale = Row("legacy-dead", Now - TimeSpan.FromDays(400));

        Assert.Single(Hot([payable]));
        Assert.Empty(Cold([payable]));
        Assert.Empty(Hot([stale]));
        Assert.Single(Cold([stale]));

        Assert.Null(payable.MonitoringExpirationTimestamp);
        Assert.Equal(
            payable.ExpirationTimestamp > HotFloorUnix,
            RGBInvoiceListener.StillPayableCheckoutPredicate(HotFloorUnix, MonitoringFloorUnix)
                .Compile()(payable));
    }

    public static IEnumerable<object?[]> PartitionCases()
    {
        long?[] monitoring =
        [
            null,
            MonitoringFloorUnix - 1,
            MonitoringFloorUnix,
            MonitoringFloorUnix + 1,
            Now.AddYears(1).ToUnixTimeSeconds()
        ];
        long?[] expiry = [null, HotFloorUnix - 1, HotFloorUnix, HotFloorUnix + 1];
        foreach (var m in monitoring)
            foreach (var e in expiry)
                yield return [m, e];
    }

    [Theory]
    [MemberData(nameof(PartitionCases))]
    public void TheTwoTierPredicatesRemainExactComplementsWithAndWithoutTheColumn(
        long? monitoringExpirationTimestamp, long? expirationTimestamp)
    {
        var row = new RGBInvoice
        {
            Id = "r1",
            WalletId = Wallet,
            RecipientId = "utxob:r1",
            AssetId = "rgb:asset",
            Status = RGBInvoiceStatus.Pending,
            CreatedAt = Now,
            ExpirationTimestamp = expirationTimestamp,
            MonitoringExpirationTimestamp = monitoringExpirationTimestamp
        };

        var hot = RGBInvoiceListener.StillPayableCheckoutPredicate(HotFloorUnix, MonitoringFloorUnix)
            .Compile()(row);
        var cold = RGBInvoiceListener.BeyondPayableCheckoutPredicate(HotFloorUnix, MonitoringFloorUnix)
            .Compile()(row);

        Assert.True(hot != cold,
            $"monitoring={monitoringExpirationTimestamp} expiry={expirationTimestamp} landed in "
            + (hot ? "both tiers" : "neither tier")
            + "; the partition must be exhaustive and non-overlapping or a row is either scanned twice "
            + "out of one budget or never scanned at all");
    }

    [Fact]
    public void TheHandlerPersistsTheInvoicesOwnMonitoringDeadlineAtCreation()
    {
        var parameter = typeof(IRGBWalletService)
            .GetMethod(nameof(IRGBWalletService.CreateInvoiceAsync))!
            .GetParameters()
            .SingleOrDefault(p => p.Name == "monitoringExpirationTimestamp");
        Assert.NotNull(parameter);
        Assert.Equal(typeof(long?), parameter!.ParameterType);
        Assert.NotNull(typeof(RGBInvoice).GetProperty(
            nameof(RGBInvoice.MonitoringExpirationTimestamp)));
    }
}
