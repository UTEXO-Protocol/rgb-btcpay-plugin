using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBackupHardeningTests
{
    sealed class WalletServiceThatCapturesTheBackupCancellationToken : IRGBWalletService
    {
        public CancellationToken CapturedCt;

        static readonly RGBWallet Wallet = new()
        {
            Id = "wallet-under-test",
            StoreId = "store-under-test",
            Name = "RGB Wallet",
            Network = "regtest"
        };

        public Task<RGBWallet?> GetWalletForStoreAsync(string storeId, CancellationToken ct = default)
            => Task.FromResult<RGBWallet?>(Wallet);

        public Task<string> BackupWalletAsync(string walletId, string password, CancellationToken ct = default)
        {
            CapturedCt = ct;
            return Task.FromResult(Path.Combine(Path.GetTempPath(), "rgb-backup-hardening-test-no-such-file.rgb"));
        }

        static NotSupportedException Unused() =>
            new("this backup-cancellation test must not reach any other wallet-service member");

        public Task<RGBWallet> CreateWalletAsync(string storeId, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Unused();
        public Task<RGBWallet> RestoreWalletAsync(string storeId, string mnemonic, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Unused();
        public Task<RGBWallet?> GetWalletAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<RGBAsset?> GetAssetAsync(string walletId, string assetId, CancellationToken ct = default) => throw Unused();
        public Task<string> GetAddressAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<BtcBalance> GetBtcBalanceAsync(string walletId, CancellationToken ct = default, bool sync = false) => throw Unused();
        public Task<RgbVanillaReservationReport> GetVanillaReservationReportAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<int> CreateColorableUtxosAsync(string walletId, int count = 4, int size = 1000, CancellationToken ct = default) => throw Unused();
        public Task<List<RgbAsset>> ListAssetsAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<List<UnspentOutput>> ListUnspentsAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<List<BtcTransaction>> ListBtcTransactionsAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<RgbAsset> IssueAssetAsync(string walletId, string ticker, string name, long amt, int precision = 0, CancellationToken ct = default) => throw Unused();
        public Task<bool> RefreshWalletAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<RGBInvoice> CreateInvoiceAsync(string walletId, string? assetId, long? amount, TimeSpan? expiration, string? btcPayInvoiceId = null, int minConfirmations = 1, long? monitoringExpirationTimestamp = null, CancellationToken ct = default) => throw Unused();
        public Task<List<RgbTransfer>> GetTransfersAsync(string walletId, string? assetId = null, CancellationToken ct = default) => throw Unused();
        public Task<RGBWallet> RestoreFromBackupAsync(string storeId, string mnemonic, string backupPath, string password, string selectedNetwork, string? name = null, int? maxAllocationsPerUtxo = null, CancellationToken ct = default) => throw Unused();
        public Task DeleteWalletAsync(string walletId, CancellationToken ct = default) => throw Unused();
        public Task<(string Txid, long AmountSent, long Fee)> SendBtcAsync(string walletId, string destinationAddress, long amountSats, float feeRate, CancellationToken ct = default) => throw Unused();
        public Task<(string Txid, long AmountSent, string AssetId, string AssetTicker, string? RecoveryAdvisory)> SendAssetAsync(string walletId, string rgbInvoice, string assetId, long amount, float feeRate, CancellationToken ct = default) => throw Unused();
    }

    static RGBController BuildController(IRGBWalletService wallets, out DefaultHttpContext httpContext)
    {
        var controller = new RGBController(
            wallets: wallets,
            stores: null!,
            handlers: null!,
            db: null!,
            log: NullLogger<RGBController>.Instance,
            userManager: null!,
            events: null!,
            cache: null!,
            btcPayOptions: Options.Create(new BTCPayServerOptions()),
            rateSource: null!,
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-backup-hardening-tests")),
            authorizations: null!);
        httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    [Fact]
    public async Task BackupPassesTheRequestsAbortSignalThrough_SoAnAbortedRequestIsNotSilentlyIgnored()
    {
        var wallets = new WalletServiceThatCapturesTheBackupCancellationToken();
        var controller = BuildController(wallets, out var httpContext);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        httpContext.RequestAborted = cts.Token;

        await controller.BackupWallet("store-under-test", "a-strong-enough-password");

        Assert.True(wallets.CapturedCt.IsCancellationRequested,
            "the controller must pass HttpContext.RequestAborted into BackupWalletAsync instead of "
            + "default(CancellationToken), so a request abort can stop the wait for the wallet lock "
            + "instead of being silently ignored");
    }

    static (string Source, int MethodStart, int MethodEnd) BackupWalletAsyncBody()
    {
        var source = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "Services", "RgbLibService.cs"));
        var methodStart = source.IndexOf(
            "public async Task<string> BackupWalletAsync(", StringComparison.Ordinal);
        Assert.True(methodStart > 0, "BackupWalletAsync must exist in RgbLibService");
        var methodEnd = source.IndexOf(
            "internal void Backup(RgbLibWallet wallet", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "Backup(RgbLibWallet, ...) must immediately follow BackupWalletAsync");
        return (source, methodStart, methodEnd);
    }

    [Fact]
    public void BackupWalletAsyncRefusesAConcurrentBackupWithAnImmediateSingleFlightGate()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        var gateWaitAt = body.IndexOf("WaitAsync(TimeSpan.Zero,", StringComparison.Ordinal);
        Assert.True(gateWaitAt >= 0,
            "BackupWalletAsync must take an immediate (TimeSpan.Zero) single-flight gate before the "
            + "expensive native backup call, mirroring the restore gate, so N concurrent backup requests "
            + "cannot each pin a scrypt arena and a zstd compression at once");

        var refusalAt = body.IndexOf(
            "DescribeBackupGateRefusal(",
            StringComparison.Ordinal);
        Assert.True(refusalAt > gateWaitAt,
            "a refused concurrent backup must get an actionable, operator-facing refusal naming what to "
            + "do next, placed after the gate is checked");
    }

    [Theory]
    [InlineData(59, 60, "Another wallet backup is currently in progress. Try again shortly.")]
    [InlineData(60, 60, "Another wallet backup is currently in progress. Try again shortly.")]
    public void DescribeBackupGateRefusalCallsItOrdinaryWhileWithinTheStuckThreshold(int heldSeconds, int thresholdSeconds, string expected)
    {
        Assert.Equal(expected, RgbLibService.DescribeBackupGateRefusal(
            TimeSpan.FromSeconds(heldSeconds), TimeSpan.FromSeconds(thresholdSeconds)));
    }

    [Fact]
    public void DescribeBackupGateRefusalNamesTheStuckCaseOnlyOnceThresholdIsExceeded()
    {
        var message = RgbLibService.DescribeBackupGateRefusal(
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Contains("restart BTCPay", message);
        Assert.DoesNotContain("healthy", message);
    }

    [Fact]
    public void DescribeBackupGateRefusalUsesNoneOfTheWordsThatWouldClaimAHungNativeCallWasStopped()
    {
        var ordinary = RgbLibService.DescribeBackupGateRefusal(TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var stuck = RgbLibService.DescribeBackupGateRefusal(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

        foreach (var message in new[] { ordinary, stuck })
        {
            Assert.DoesNotContain("ancel", message);
            Assert.DoesNotContain("abort", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stopped", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("will complete", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DescribeBackupGateRefusalIsNeverGivenAWalletIdItCouldLeak()
    {
        var method = typeof(RgbLibService).GetMethod("DescribeBackupGateRefusal",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(string));
    }

    [Fact]
    public void GetOrCreateBackupCooldownKeysByWalletId_SoOneTenantsBackupNeverCoolsDownAnothers()
    {
        var walletA = "wallet-cooldown-a-" + Guid.NewGuid();
        var walletB = "wallet-cooldown-b-" + Guid.NewGuid();

        var gateA = RgbLibService.GetOrCreateBackupCooldown(walletA,
            () => new RestoreCooldownGate(TimeSpan.FromSeconds(60)));
        var gateB = RgbLibService.GetOrCreateBackupCooldown(walletB,
            () => new RestoreCooldownGate(TimeSpan.FromSeconds(60)));

        Assert.NotSame(gateA, gateB);

        var now = DateTimeOffset.UtcNow;
        gateA.RecordAttempt(now);

        Assert.True(gateA.IsCoolingDown(now),
            "recording an attempt on wallet A's own cooldown must arm wallet A's own cooldown");
        Assert.False(gateB.IsCoolingDown(now),
            "wallet A's successful backup must never arm wallet B's cooldown; a per-wallet key is the "
            + "whole point of this fix");
    }

    [Fact]
    public void GetOrCreateBackupCooldownReturnsTheSameGateForRepeatCallsOnTheSameWalletId()
    {
        var walletId = "wallet-cooldown-same-" + Guid.NewGuid();

        var first = RgbLibService.GetOrCreateBackupCooldown(walletId,
            () => new RestoreCooldownGate(TimeSpan.FromSeconds(60)));
        var second = RgbLibService.GetOrCreateBackupCooldown(walletId,
            () => new RestoreCooldownGate(TimeSpan.FromSeconds(999)));

        Assert.Same(first, second);
    }

    [Fact]
    public void BackupWalletAsyncAppliesACooldownAfterEveryAttemptThatReachesTheNativeCall_SoATightLoopCannotRetriggerTheScryptArena()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        var coolingCheckAt = body.IndexOf("IsCoolingDown(", StringComparison.Ordinal);
        Assert.True(coolingCheckAt >= 0,
            "BackupWalletAsync must consult a cooldown before starting the native call, so a caller "
            + "cannot re-trigger the ~128 MiB scrypt arena in a tight loop");

        var refusalAt = body.IndexOf("DescribeBackupCooldownRefusal(", StringComparison.Ordinal);
        Assert.True(refusalAt > coolingCheckAt,
            "the cooldown refusal must be operator-facing and must follow the cooldown check");

        var recordAt = body.IndexOf("RecordAttempt(", StringComparison.Ordinal);
        Assert.True(recordAt > refusalAt,
            "every attempt that reaches the native call must record itself in the cooldown afterward, "
            + "exactly like restore's cooldown, regardless of whether the native call succeeded or failed");

        var configWiredAt = body.IndexOf("ResolveBackupCooldown(_config)", StringComparison.Ordinal);
        Assert.True(configWiredAt >= 0 && configWiredAt < recordAt,
            "the cooldown duration must be read from configuration rather than a literal, so an operator "
            + "can tune it the same way restore's cooldown is tunable");
    }

    [Fact]
    public void BackupWalletAsyncRechecksTheCooldownAfterTakingTheGate_SoTwoRacingCallersCannotBothSlipThrough()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        var gateAcquiredAt = body.IndexOf("_backupGate.WaitAsync(", StringComparison.Ordinal);
        Assert.True(gateAcquiredAt >= 0, "BackupWalletAsync no longer acquires the single-flight gate");

        var gateRefusalAt = body.IndexOf("DescribeBackupGateRefusal(", StringComparison.Ordinal);
        Assert.True(gateRefusalAt > gateAcquiredAt, "the failed-to-enter refusal no longer follows the gate");
        var holderStampAt = body.IndexOf(
            "_backupGateHolderSinceMonotonicTimestamp, Stopwatch.GetTimestamp()", StringComparison.Ordinal);
        Assert.True(holderStampAt > gateRefusalAt, "the gate-holder stamp no longer follows the refusal block");

        var checksBeforeTheGate = CountOccurrences(body[..gateAcquiredAt], "IsCoolingDown(");
        var recheckAt = body.IndexOf("IsCoolingDown(", gateRefusalAt, StringComparison.Ordinal);

        Assert.True(checksBeforeTheGate >= 1,
            "the cooldown is no longer consulted before the gate, so a caller pays the wait for a backup "
            + "that the cooldown was going to refuse anyway");
        Assert.True(recheckAt > gateRefusalAt && recheckAt < holderStampAt,
            "the cooldown is checked only BEFORE the single-flight gate is taken. Two callers can both pass "
            + "that check while a third backup is still running. _backupGate.WaitAsync(TimeSpan.Zero) never "
            + "blocks, so the race is not a queued waiter: it is a caller descheduled between its pre-check "
            + "and its zero-time acquisition. The running backup records the cooldown and releases the gate, "
            + "that caller then resumes, acquires, and starts another ~128 MiB scrypt arena without ever "
            + "re-consulting the cooldown. Under a request flood this check-then-acquire race defeats the "
            + "rate limit the cooldown exists to impose");
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    [Fact]
    public void BackupWalletAsyncHoldsItsOwnGateAndCooldown_SoARefusalNeverNamesTheWrongOperation()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        Assert.Contains("_backupGate", body);
        Assert.Contains("GetOrCreateBackupCooldown", body);
        Assert.DoesNotContain("_restoreGate", body);
        Assert.DoesNotContain("GetOrCreateRestoreCooldown", body);
    }

    [Fact]
    public void BackupWalletAsyncsWaitTimeoutNeverClaimsToStopNativeWorkAlreadyInProgress()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        var timedOutAt = body.IndexOf(
            "Timed out waiting to start the wallet backup. No backup was written; try again.",
            StringComparison.Ordinal);
        Assert.True(timedOutAt >= 0,
            "a caller stuck waiting for a busy wallet must eventually get an honest refusal rather than "
            + "hanging forever, bounded by a configured wait timeout, and the refusal must say plainly "
            + "that no backup was produced");

        var throwAt = body.LastIndexOf("throw new InvalidOperationException(", timedOutAt, StringComparison.Ordinal);
        Assert.True(throwAt >= 0, "the timeout refusal must be thrown as an InvalidOperationException, the only "
            + "exception type this message can safely use given it renders verbatim in the operator's browser");
        var messageEnd = body.IndexOf(");", timedOutAt, StringComparison.Ordinal);
        Assert.True(messageEnd > timedOutAt, "the refusal message literal must be closed before the catch clause ends");
        var messageOnly = body[throwAt..messageEnd];

        Assert.DoesNotContain("ancel", messageOnly);
        Assert.DoesNotContain("abort", messageOnly, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stopped the backup", messageOnly, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("because another operation", messageOnly, StringComparison.OrdinalIgnoreCase);

        var configWiredAt = body.IndexOf("ResolveBackupStartWaitTimeout(_config)", StringComparison.Ordinal);
        Assert.True(configWiredAt >= 0 && configWiredAt < timedOutAt,
            "the wait timeout must be read from configuration through the clamping resolver, not from a "
            + "literal and not from the raw setting — an unclamped value makes CancelAfter throw before the "
            + "inner try, and that exception is not one this message can reach the operator through");
    }

    [Fact]
    public void TheBackupGateIsReleasedInAFinallyThatEnclosesTheNativeCall_SoAFailedBackupDoesNotDenyEveryLaterBackup()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "BackupWalletAsync");

        var releasingTry = method.DescendantNodes().OfType<TryStatementSyntax>().FirstOrDefault(t =>
            t.Finally is not null
            && t.Finally.Block.ToString().Contains("_backupGate.Release()", StringComparison.Ordinal));

        Assert.True(releasingTry is not null,
            "BackupWalletAsync must release _backupGate from a finally block. Without it the single-flight "
            + "gate is never released, so every later backup on this process is refused forever with the "
            + "gate-busy refusal — a permanent refusal no operator can clear without restarting BTCPay. "
            + "Deleting the release line passes every other test in this file, which is why this pin exists.");

        Assert.True(
            releasingTry!.Block.ToString().Contains("handle.ExecuteAsync", StringComparison.Ordinal),
            "the finally that releases _backupGate must enclose the native backup call itself. A release "
            + "that does not guard handle.ExecuteAsync leaves the gate held whenever that call throws, "
            + "which is the same permanent refusal by a different route.");

        var waitAt = method.ToString().IndexOf("_backupGate.WaitAsync", StringComparison.Ordinal);
        var tryAt = method.ToString().IndexOf(releasingTry.ToString(), StringComparison.Ordinal);
        Assert.True(waitAt >= 0 && tryAt > waitAt,
            "the gate must be acquired BEFORE the try whose finally releases it; releasing a gate this "
            + "call never acquired would let a concurrent backup run alongside the native one.");

        var bodyStatements = method.Body!.Statements;
        var statementList = bodyStatements.ToList();
        var waitStatementIndex = statementList.FindIndex(s =>
            s.ToString().Contains("_backupGate.WaitAsync", StringComparison.Ordinal));
        Assert.True(waitStatementIndex >= 0,
            "the gate wait must be a top-level statement in BackupWalletAsync's body, not nested inside "
            + "another construct this pin cannot see into");

        var enteredCheckIndex = waitStatementIndex + 1;
        Assert.True(enteredCheckIndex < statementList.Count && statementList[enteredCheckIndex] is IfStatementSyntax,
            "the statement immediately after the gate wait must be the entered-check, with nothing else "
            + "sitting between the wait and its own refusal check");

        var releasingTryIndex = statementList.FindIndex(s => ReferenceEquals(s, releasingTry));
        Assert.True(releasingTryIndex == enteredCheckIndex + 1,
            "no statement may sit between the entered-check and the try whose finally releases the gate. "
            + "A statement placed there that can throw (Path.GetTempPath() did, before this pin) would "
            + "leak the gate forever on that exception, exactly like a hung native call — so any such "
            + "statement (for example computing tempPath) must live before the gate wait or as the first "
            + "line inside the try, never in this gap.");
    }

    [Theory]
    [InlineData(31, 30, "31 seconds")]
    [InlineData(50, 45, "50 seconds")]
    [InlineData(61, 60, "1 minute")]
    [InlineData(121, 120, "2 minutes")]
    [InlineData(330, 300, "5 minutes")]
    public void TheStuckBackupRefusalNeverOverstatesHowLongTheGateHasActuallyBeenHeld(
        int heldSeconds, int thresholdSeconds, string expectedElapsed)
    {
        var message = RgbLibService.DescribeBackupGateRefusal(
            TimeSpan.FromSeconds(heldSeconds), TimeSpan.FromSeconds(thresholdSeconds));

        Assert.True(message.Contains($"at least {expectedElapsed}.", StringComparison.Ordinal),
            $"held for {heldSeconds}s against a {thresholdSeconds}s threshold, the refusal must say "
            + $"\"at least {expectedElapsed}\" but said: {message}. This text renders VERBATIM in the "
            + "store operator's browser, and BackupStuckThresholdSeconds is operator-settable, so a "
            + "sub-minute threshold must not make the message round a 31-second hold up to \"1 minute\" "
            + "— an elapsed time the gate has not actually reached is a false statement to the operator.");
    }

    [Fact]
    public void TheGateRefusalFiresOnFailureToEnter_NotOnSuccess_SoAnInvertedCheckCannotMakeTheGateDecorative()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "BackupWalletAsync");
        var statements = method.Body!.Statements;

        var waitIndex = statements.ToList().FindIndex(
            st => st.ToString().Contains("_backupGate.WaitAsync", StringComparison.Ordinal));
        Assert.True(waitIndex >= 0, "BackupWalletAsync must acquire _backupGate with WaitAsync.");

        var enteredLocal = Assert.IsType<LocalDeclarationStatementSyntax>(statements[waitIndex])
            .Declaration.Variables.Single().Identifier.ValueText;

        var check = Assert.IsType<IfStatementSyntax>(statements[waitIndex + 1]);
        var negation = check.Condition as PrefixUnaryExpressionSyntax;

        Assert.True(
            negation is not null
            && negation.IsKind(SyntaxKind.LogicalNotExpression)
            && negation.Operand is IdentifierNameSyntax id
            && id.Identifier.ValueText == enteredLocal,
            $"the gate check must refuse when '{enteredLocal}' is FALSE, i.e. the condition must be "
            + $"'!{enteredLocal}'. It is currently '{check.Condition}'. Inverting it makes every "
            + "successful acquisition refuse AND lets every failed acquisition fall through to run the "
            + "native backup WITHOUT holding the gate, so two 128 MiB native backups could run at once — "
            + "the exact single-flight invariant this gate exists to enforce. Every other test in this "
            + "file passes under that inversion, which is why this pin checks the condition itself.");
    }

    [Fact]
    public void TheHeldDurationIsMeasuredWithAMonotonicClock_SoAClockAdjustmentCannotFalsifyTheRefusal()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RgbLibService.cs");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "BackupWalletAsync");

        var statements = method.Body!.Statements;
        var waitIndex = statements.ToList().FindIndex(
            st => st.ToString().Contains("_backupGate.WaitAsync", StringComparison.Ordinal));
        var refusalBlock = Assert.IsType<IfStatementSyntax>(statements[waitIndex + 1]).Statement.ToString();

        Assert.True(refusalBlock.Contains("Stopwatch.GetElapsedTime", StringComparison.Ordinal),
            "the held-duration the refusal reports must come from Stopwatch, a monotonic source. It "
            + $"currently reads: {refusalBlock}");
        Assert.True(!refusalBlock.Contains("DateTimeOffset", StringComparison.Ordinal),
            "the held-duration must not be derived from wall-clock time. DateTimeOffset.UtcNow is "
            + "adjustable: a forward correction while a backup holds the gate would report a "
            + "seconds-old hold as minutes and tell the operator to restart BTCPay during healthy "
            + "work, and a backward correction would keep a genuinely stuck gate below the threshold "
            + "forever, so the actionable restart instruction would never appear. Both make an "
            + $"operator-facing sentence false. It currently reads: {refusalBlock}");

        Assert.Contains(
            "Interlocked.Exchange(ref _backupGateHolderSinceMonotonicTimestamp, Stopwatch.GetTimestamp())",
            method.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartWaitTimeoutTellsTheOperatorWhatToDoWhenItKeepsTimingOut()
    {
        var (source, methodStart, methodEnd) = BackupWalletAsyncBody();
        var body = source[methodStart..methodEnd];

        var timedOutAt = body.IndexOf(
            "Timed out waiting to start the wallet backup.", StringComparison.Ordinal);
        Assert.True(timedOutAt >= 0, "the start-wait timeout refusal is gone");
        var messageEnd = body.IndexOf(");", timedOutAt, StringComparison.Ordinal);
        var message = body[timedOutAt..messageEnd];

        Assert.Contains("restart BTCPay", message, StringComparison.Ordinal);
        Assert.Contains("keeps timing out", message, StringComparison.Ordinal);
        Assert.DoesNotContain("another operation is still holding", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.2, "1 second")]
    [InlineData(1.0, "1 second")]
    [InlineData(1.2, "2 seconds")]
    [InlineData(59.0, "59 seconds")]
    [InlineData(59.5, "1 minute")]
    [InlineData(60.0, "1 minute")]
    [InlineData(61.0, "2 minutes")]
    [InlineData(3600.0, "60 minutes")]
    public void TheCooldownRetryDelayIsNeverUnderstatedAndNeverMisspelled(double remainingSeconds, string expected)
    {
        var remaining = TimeSpan.FromSeconds(remainingSeconds);
        var rendered = RgbLibService.DescribeRetryDelayWithoutUnderstatingIt(remaining);

        Assert.Equal(expected, rendered);
        Assert.DoesNotContain("1 seconds", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("1 minutes", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.999)]
    [InlineData(1.0)]
    [InlineData(30.4)]
    [InlineData(59.999)]
    [InlineData(60.0)]
    [InlineData(119.5)]
    [InlineData(3599.4)]
    public void TheCooldownRetryDelayNeverTellsTheOperatorToComeBackSoonerThanTheCooldownActuallyEnds(
        double remainingSeconds)
    {
        var remaining = TimeSpan.FromSeconds(remainingSeconds);
        var rendered = RgbLibService.DescribeRetryDelayWithoutUnderstatingIt(remaining);

        var parts = rendered.Split(' ');
        var quantity = int.Parse(parts[0]);
        var advertised = parts[1].StartsWith("minute", StringComparison.Ordinal)
            ? TimeSpan.FromMinutes(quantity)
            : TimeSpan.FromSeconds(quantity);

        Assert.True(advertised >= remaining,
            $"the refusal tells the operator to retry in {rendered}, but the cooldown still has "
            + $"{remaining} left. An operator who waits exactly as long as they were told is refused a "
            + "second time, and the sentence that sent them away was false when it was written");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(4_294_968)]
    [InlineData(0)]
    [InlineData(-1)]
    public void TheBackupStartWaitTimeoutIsAlwaysAValueCancelAfterAccepts(int configuredSeconds)
    {
        var cfg = new RGBConfiguration { BackupStartWaitTimeoutSeconds = configuredSeconds };
        var resolved = RgbLibService.ResolveBackupStartWaitTimeout(cfg);

        using var cts = new CancellationTokenSource();
        var thrown = Record.Exception(() => cts.CancelAfter(resolved));

        Assert.True(thrown == null,
            $"a configured backup start-wait of {configuredSeconds}s resolved to {resolved}, which "
            + "CancellationTokenSource.CancelAfter rejects above the runtime timer ceiling. That throw lands "
            + "before the gate's inner try, is not one of the operator-facing exception types, and repeats "
            + "for every later backup — so one out-of-range setting turns every backup into a generic "
            + $"failure with nothing naming the setting at fault. Got {thrown?.GetType().Name}");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    public void TheBackupCooldownAndStuckThresholdStayInsideTheirDeclaredRanges(int configuredSeconds)
    {
        var cfg = new RGBConfiguration
        {
            BackupCooldownSeconds = configuredSeconds,
            BackupStuckThresholdSeconds = configuredSeconds
        };

        var cooldown = RgbLibService.ResolveBackupCooldown(cfg);
        var stuck = RgbLibService.ResolveBackupStuckThreshold(cfg);

        Assert.InRange(cooldown.TotalSeconds,
            RGBConfiguration.BackupCooldownSecondsMin, RGBConfiguration.BackupCooldownSecondsMax);
        Assert.InRange(stuck.TotalSeconds,
            RGBConfiguration.BackupStuckThresholdSecondsMin, RGBConfiguration.BackupStuckThresholdSecondsMax);
    }
}
