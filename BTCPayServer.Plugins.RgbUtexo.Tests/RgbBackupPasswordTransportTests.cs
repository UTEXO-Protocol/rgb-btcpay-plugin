using BTCPayServer.Configuration;
using BTCPayServer.Plugins.RgbUtexo.Controllers;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;
using BTCPayServer.Plugins.RgbUtexo.Tests.Stubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBackupPasswordTransportTests
{
    sealed class WalletServiceThatRecordsBackupAttempts : IRGBWalletService
    {
        public int BackupAttempts;
        public string? LastPassword;

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
            BackupAttempts++;
            LastPassword = password;
            throw new InvalidOperationException(
                "the test stops here: reaching the native Backup call is itself the regression, because "
                + "an unrestorable artifact must never be produced");
        }

        static NotSupportedException Unused() =>
            new("this backup-password test must not reach any other wallet-service member");

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

    static RGBController BuildController(IRGBWalletService wallets)
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
            cfg: new RGBConfiguration(Path.Combine(Path.GetTempPath(), "rgb-backup-password-tests")),
            authorizations: null!);
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    [Theory]
    [InlineData("line\nbreak-pass")]
    [InlineData("line\r\nbreak-pass")]
    [InlineData("carriage\rreturn-pass")]
    [InlineData("trailing-newline-pass\n")]
    [InlineData("\nleading-newline-pass")]
    public async Task BackupRefusesAPasswordWithALineBreak_BecauseRestoreReadsOnlyTheFirstLineAndCouldNeverDecryptIt(
        string password)
    {
        var wallets = new WalletServiceThatRecordsBackupAttempts();
        var controller = BuildController(wallets);

        var result = await controller.BackupWallet("store-under-test", password);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(0, wallets.BackupAttempts);
        Assert.Equal(
            RestoreProcessRunner.BackupPasswordLineBreakRefusal,
            controller.TempData["ErrorMessage"]);
    }

    [Fact]
    public void TheBackupRefusalMessageNamesTheCause_SoTheMerchantCanFixThePasswordRatherThanRetryBlindly()
    {
        var message = RestoreProcessRunner.BackupPasswordLineBreakRefusal;

        Assert.Contains("line break", message);
        Assert.Contains("CR or LF", message);
        Assert.Contains("truncated", message);
        Assert.Contains("Restore", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("Tr0ub4dor&3 !#$%^&*()_+-=[]{};':\",./<>?")]
    [InlineData("passwörd-mit-ümlaut-ÄÖÜß")]
    [InlineData("пароль-который-достаточно-длинный")]
    [InlineData("密码密码密码密码")]
    [InlineData("emoji-pass-\U0001F510\U0001F511")]
    [InlineData("  leading and trailing spaces  ")]
    [InlineData("tab\tseparated-is-carried-by-a-single-line")]
    public async Task BackupStillAcceptsAnOrdinaryStrongPassword_SoTheRefusalCannotStrandAMerchantWithNoBackup(
        string password)
    {
        var wallets = new WalletServiceThatRecordsBackupAttempts();
        var controller = BuildController(wallets);

        await controller.BackupWallet("store-under-test", password);

        Assert.Equal(1, wallets.BackupAttempts);
        Assert.Equal(password, wallets.LastPassword);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void TheLineBreakPredicateFlagsBothCharactersThatTerminateTheHelpersReadLine(string injected)
    {
        Assert.True(
            RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(
                "ordinary-prefix" + injected + "ordinary-suffix"),
            $"U+{(int)injected[0]:X4} terminates the restore helper's ReadLine, so everything after it "
            + "is silently dropped and the backup encrypted with the whole password can never be opened");
    }

    [Theory]
    [InlineData("\u0009")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u001B")]
    [InlineData("\u007F")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void TheLineBreakPredicateLeavesOtherControlAndSeparatorCharactersAlone_BecauseReadLineCarriesThemIntact(
        string injected)
    {
        Assert.False(
            RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(
                "ordinary-prefix" + injected + "ordinary-suffix"),
            $"U+{(int)injected[0]:X4} is not a TextReader.ReadLine terminator, so it round-trips intact; "
            + "refusing it would block a backup for no reason — itself a way to strand assets");
    }

    [Fact]
    public void ANulCharacterIsNotRefused_BecauseItTruncatesIdenticallyOnBothSidesSoSuchABackupStillRestores()
    {
        Assert.False(
            RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry("prefix\0suffix"),
            "NUL terminates the UTF-8 C string handed to rgb-lib when the backup is written AND the one "
            + "handed to the native decryptor on restore, so both sides use the same prefix and such a "
            + "backup restores today; refusing it on the restore side would permanently reject a backup "
            + "that currently works, which is the forbidden failure mode");
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("Tr0ub4dor&3 !#$%^&*()_+-=[]{};':\",./<>?")]
    [InlineData("passwörd-mit-ümlaut-ÄÖÜß")]
    [InlineData("emoji-pass-\U0001F510\U0001F511")]
    [InlineData("tab\there")]
    [InlineData("  spaces  ")]
    public void TheLineBreakPredicateAcceptsOrdinaryUnicodePunctuationAndWhitespace(string password)
    {
        Assert.False(
            RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(password),
            $"'{password.Length}-character password' contains no control character, so refusing it would "
            + "block a legitimate backup — itself a way to strand assets");
    }

    [Fact]
    public void TheLineBreakPredicateTreatsANullPasswordAsNothingToRefuse_SoTheLengthCheckStaysTheAuthority()
    {
        Assert.False(
            RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(null));
    }

    static RestoreLimits Limits() => new(
        Timeout: TimeSpan.FromMilliseconds(200),
        DiskCapBytes: 1000,
        RamCapBytes: 1000,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(10),
        ReapGrace: TimeSpan.FromMilliseconds(50));

    [Theory]
    [InlineData("line\nbreak-pass")]
    [InlineData("carriage\rreturn-pass")]
    public async Task RestoreRefusesTheSamePasswordsBeforeLaunchingAnything_SoTheFailureNamesItsCauseInsteadOfLookingLikeAWrongPassword(
        string password)
    {
        var runner = new RestoreProcessRunner(
            NullLogger<RestoreProcessRunner>.Instance,
            handleFactory: _ => throw new Xunit.Sdk.XunitException(
                "the restore helper must not be launched for a password the stdin transport cannot carry"),
            resolveHelperDll: () => throw new Xunit.Sdk.XunitException(
                "the refusal must precede helper resolution so nothing is started"),
            resolveDotnetHost: () => throw new Xunit.Sdk.XunitException(
                "the refusal must precede dotnet-host resolution so nothing is started"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("backup.rgb", Path.GetTempPath(), password, Limits(), CancellationToken.None));

        Assert.Contains("line break", ex.Message);
        Assert.Contains("The wallet was not restored.", ex.Message);
    }

    [Fact]
    public void RgbLibServiceRefusesTheTransportUnsafePasswordBeforeItTouchesAWallet()
    {
        var source = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "Services", "RgbLibService.cs"));
        var backupAt = source.IndexOf(
            "public async Task<string> BackupWalletAsync(", StringComparison.Ordinal);
        Assert.True(backupAt > 0, "BackupWalletAsync must exist in RgbLibService");

        var guardAt = source.IndexOf(
            "RestoreProcessRunner.ContainsALineBreakTheSingleLineStdinTransportCannotCarry(password)",
            backupAt, StringComparison.Ordinal);
        var walletAt = source.IndexOf("GetOrCreateWalletAsync(walletId, ct)", backupAt, StringComparison.Ordinal);

        Assert.True(guardAt > backupAt,
            "RgbLibService.BackupWalletAsync must consult the stdin-transport password guard, so no "
            + "caller of the service — not just the controller — can produce an unrestorable backup");
        Assert.True(guardAt < walletAt,
            "the guard must run before the wallet is opened, so a refused password costs nothing and "
            + "cannot reach wallet.Backup");
    }
}
