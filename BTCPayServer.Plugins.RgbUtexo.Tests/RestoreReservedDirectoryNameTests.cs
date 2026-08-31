using System.IO.Compression;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

[Collection("RestoreSerial")]
public class RestoreReservedDirectoryNameTests
{
    public RestoreReservedDirectoryNameTests()
    {
        typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, null);
    }

    const string SyntheticMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    const string FakeLibFingerprint = "00000000";

    const string SyntheticTransactionId =
        "0000000000000000000000000000000000000000000000000000000000000001";

    static string TransfersName =>
        RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30TransfersDirectoryNameReReadTransfersDirWhenBumpingRgbLib;

    [Fact]
    public void ARegularFileAtTheTransfersName_IsFoundBecauseEverySendAndEveryReceivedConsignmentCreatesADirectoryUnderIt()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, TransfersName), 0);

        Assert.Equal(TransfersName, RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    [Fact]
    public void AZeroByteFileAtTheTransfersNamePassesTheDiskCapAndTheFingerprintCheck_WhichIsWhyOnlyATypeCheckCanCatchIt()
    {
        using var staging = new TempTree();
        staging.MakeDir(FakeLibFingerprint);
        staging.WriteFile(Path.Combine(FakeLibFingerprint, TransfersName), 0);

        var summedBytes = new DirectoryInfo(staging.Path)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
        Assert.True(summedBytes == 0,
            $"the planted entry contributed {summedBytes} byte(s); the restore's disk cap sums file lengths, so a "
            + "zero-byte plant is invisible to it and no size limit can ever refuse this shape");

        var fingerprintDirs = Directory.GetDirectories(staging.Path).Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { FakeLibFingerprint }, fingerprintDirs);

        Assert.Null(RGBWalletService.FindDirectoryAtAReservedSingleFileName(staging.Path));
    }

    [Fact]
    public void AFileAtTheTransfersNameDefeatsEveryLaterCreateDirAllAndIsNeverUnlinked_WhichIsWhyRestoreMustRefuseIt()
    {
        using var walletData = new TempTree();
        var transfersPath = Path.Combine(walletData.Path, FakeLibFingerprint, TransfersName);
        Directory.CreateDirectory(Path.GetDirectoryName(transfersPath)!);
        File.WriteAllBytes(transfersPath, []);

        Assert.False(Directory.Exists(transfersPath),
            "Directory.Exists is false for a regular file, so nothing that probes for the transfers directory "
            + "notices the plant before rgb-lib tries to create a transfer directory beneath it");

        var thrown = Record.Exception(() =>
            Directory.CreateDirectory(Path.Combine(transfersPath, SyntheticTransactionId)));
        Assert.True(thrown is IOException or UnauthorizedAccessException,
            $"creating a transfer directory under the planted file threw {thrown?.GetType().Name ?? "nothing"}; "
            + "rgb-lib spells this as fs::create_dir_all in prepare_rgb_psbt on the send path and again on the "
            + "received-consignment write during refresh, and std's create_dir_all reports the mkdir error "
            + "unchanged when the existing entry is not a directory");
        Assert.True(File.Exists(transfersPath),
            "the failed creation removed the planted file, so the condition would be self-clearing; it is not, and "
            + "no path in rgb-lib or in this plugin ever unlinks it, so every send fails at every fee rate forever "
            + "and every incoming transfer fails to be written, stranding the wallet's assets");
    }

    [Fact]
    public void ADirectoryAtTheTransfersNameIsAccepted_BecauseEveryGenuineBackupOfAWalletThatHasTransactedCarriesOne()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, "rgb_lib_db"), 128);
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "assets"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "media_files"));
        staging.WriteFile(
            Path.Combine(FakeLibFingerprint, TransfersName, SyntheticTransactionId, "fascia"), 64);

        Assert.Null(RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    [Fact]
    public void ATreeWithNoTransfersEntryAtAllIsAccepted_BecauseAGenuineBackupOfANeverUsedWalletSimplyOmitsIt()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, "rgb_lib_db"), 128);
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "assets"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "media_files"));
        staging.MakeDir(Path.Combine(FakeLibFingerprint, "rgb"));

        Assert.False(Directory.Exists(Path.Combine(staging.Path, FakeLibFingerprint, TransfersName)),
            "this fixture must omit the transfers entry entirely; rgb-lib creates it lazily on the first send or "
            + "the first received consignment, so requiring its presence would refuse every backup of a funded "
            + "wallet that has not yet transacted");

        Assert.Null(RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    [Fact]
    public void ACaseVariantOfTheReservedDirectoryNameIsFound_BecauseACaseInsensitiveFilesystemResolvesItToTheSamePath()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, "TRANSFERS"), 0);

        Assert.Equal(TransfersName, RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    [Fact]
    public void TheReservedDirectoryNameSetIsExactlyTheOnePinnedRgbLibNameCreatedOnlyAfterTheRestoreIsPublished()
    {
        Assert.Equal(
            new[] { "transfers" },
            RgbWalletDirectoryReservedNames.NamesThatMustBeDirectoriesNotRegularFiles);

        Assert.Equal(
            new[] { "transfers" },
            RgbWalletDirectoryReservedNames
                .NamesCreatedAsDirectoriesByThePinnedRgbLibOnlyOnPathsReachedAfterTheRestoreIsAlreadyPublished);

        Assert.All(RgbWalletDirectoryReservedNames.NamesThatMustBeDirectoriesNotRegularFiles,
            name => Assert.DoesNotContain(name,
                RgbWalletDirectoryReservedNames.NamesThatMustBeRegularFilesNotDirectories));
    }

    [Theory]
    [InlineData("rgb")]
    [InlineData("assets")]
    [InlineData("media_files")]
    public void ADirectoryNameRgbLibCreatesButThisCheckDoesNotReserveIsAccepted_BecauseRefusingItWouldNarrowRestoreWithoutClosingABrick(
        string notReserved)
    {
        Assert.DoesNotContain(notReserved,
            RgbWalletDirectoryReservedNames.NamesThatMustBeDirectoriesNotRegularFiles);

        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, notReserved), 0);

        Assert.Null(RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    [Fact]
    public void TheRgbDirectoryIsDeliberatelyNotReserved_BecauseTheRestoresOwnEagerConsistencyCheckRollsThatShapeBackCleanly()
    {
        Assert.DoesNotContain("rgb", RgbWalletDirectoryReservedNames.NamesThatMustBeDirectoriesNotRegularFiles);

        using var staging = new TempTree();
        var rgbPath = Path.Combine(staging.Path, FakeLibFingerprint, "rgb");
        Directory.CreateDirectory(Path.GetDirectoryName(rgbPath)!);
        File.WriteAllBytes(rgbPath, []);

        var thrown = Record.Exception(() => Directory.CreateDirectory(rgbPath));
        Assert.True(thrown is IOException or UnauthorizedAccessException,
            $"creating the rgb runtime directory over the planted file threw {thrown?.GetType().Name ?? "nothing"}; "
            + "this shape must fail, because the whole reason it is not reserved is that it fails INSIDE "
            + "FsBinStore::new, which load_rgb_runtime reaches from setup_rgb during Wallet::new");
        Assert.True(RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path) == null,
            "a regular file at \"rgb\" must be accepted by this check: Wallet::new runs setup_rgb eagerly, so the "
            + "restore's own GetOrCreateWalletAsync/GetAddressAsync consistency check throws, the staging tree is "
            + "deleted, the wallet row is removed and nothing is published. Reserving it would refuse a restore "
            + "that already fails safe, which is a gratuitous narrowing and an invariant-3 risk of its own");
    }

    [Fact]
    public void TheRgbLibVersionThisReservedDirectoryNameWasReadOutOfIsStillTheOneReferenced_SoAnUpgradeForcesARereadOfGetTransfersDir()
    {
        var csproj = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "BTCPayServer.Plugins.RgbUtexo.csproj"));

        Assert.True(
            csproj.Contains("Include=\"RgbLib\" Version=\"0.3.0-beta.30\""),
            "the reserved directory name \"transfers\" has no managed creator behind it; it was read out of "
            + "rgb-lib 0.3.0-beta.30's wallet/offline.rs TRANSFERS_DIR, which get_transfers_dir joins onto the "
            + "wallet directory. prepare_rgb_psbt calls fs::create_dir_all on a directory beneath it on every "
            + "send, and the received-consignment write during refresh does the same, and neither runs during "
            + "Wallet::new, which creates only the assets and media_files directories and only when the wallet "
            + "directory is absent. If RgbLib is being bumped, re-read get_transfers_dir, prepare_rgb_psbt and "
            + "wait_consignment in the new version and re-derive this name before changing this pin");
    }

    [Fact]
    public void TheRecoveryJournalResolvesItsTransferDirectoryThroughTheSameReservedConstant_SoTheNameCannotDriftInOnePlaceOnly()
    {
        var journalSource = File.ReadAllText(
            Path.Combine(PluginCompilation.RepoRootPath, "Services", "RgbSendRecoveryJournal.cs"));

        Assert.DoesNotContain("\"transfers\"", journalSource);
    }

    [Fact]
    public void TheRefusalMessageNamesTheOffendingEntryAndTellsTheOperatorWhatToDoWithoutShellAccess()
    {
        var message = RGBWalletService.ReservedDirectoryNameUsedAsRegularFileRefusal(TransfersName);

        Assert.Contains(TransfersName, message);
        Assert.Contains("Restore a backup taken by this plugin", message);
        Assert.NotEqual(RGBWalletService.ReservedSingleFileNameUsedAsDirectoryRefusal(TransfersName), message);
    }

    [Fact]
    public async Task ARestoreWhoseBackupHoldsARegularFileAtTheTransfersName_IsRefusedBeforeAnythingIsFinalized()
    {
        var relative = Path.Combine(FakeLibFingerprint, TransfersName);
        var runner = new StagingShapingRunner(staging =>
        {
            var full = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, []);
        });
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-reserved-dir-{Guid.NewGuid():N}"));
        var svc = BuildService(runner, cfg);
        using var backup = new TempBackup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));

        Assert.Equal(
            RGBWalletService.ReservedDirectoryNameUsedAsRegularFileRefusal(TransfersName),
            ex.Message);
        Assert.False(Directory.Exists(runner.StagingDir),
            $"staging dir {runner.StagingDir} survived the refusal; a rejected restore must leave no tree behind");
        var walletsParent = Path.GetDirectoryName(cfg.GetWalletDataDir("probe", "signet"))!;
        var finalizedDirs = Directory.Exists(walletsParent)
            ? Directory.GetDirectories(walletsParent)
            : Array.Empty<string>();
        Assert.True(finalizedDirs.Length == 0,
            $"{finalizedDirs.Length} wallet data dir(s) were finalized under {walletsParent}; the refusal must "
            + "happen before Directory.Move so no unusable wallet dir is ever published");
        try { Directory.Delete(cfg.RgbBaseDir, true); } catch { }
    }

    [Fact]
    public async Task ARestoreWhoseBackupCarriesTransfersAsADirectoryIsNotRefusedByThisCheck_SoAHealthyBackupIsNeverStranded()
    {
        var runner = new StagingShapingRunner(staging =>
            Directory.CreateDirectory(
                Path.Combine(staging, FakeLibFingerprint, TransfersName, SyntheticTransactionId)));
        var cfg = new RGBConfiguration(
            Path.Combine(Path.GetTempPath(), $"rgb-reserved-dir-{Guid.NewGuid():N}"));
        var svc = BuildService(runner, cfg);
        using var backup = new TempBackup();

        var ex = await Record.ExceptionAsync(
            () => svc.RestoreFromBackupAsync("store1", SyntheticMnemonic, backup.Path, "pw", "signet"));

        Assert.True(
            ex is null || !ex.Message.Contains("reserved for a directory", StringComparison.Ordinal),
            $"a backup carrying transfers as a directory was refused with \"{ex?.Message}\"; the refusal must fire "
            + "only on a regular file at that name, never on the shape every genuine backup has");
        try { Directory.Delete(cfg.RgbBaseDir, true); } catch { }
    }

    [Fact]
    public void TheReservedDirectoryNameCheckRunsInsideRestoreFromBackupAsyncAheadOfDirectoryMove()
    {
        var plugin = PluginCompilation.Shared;
        var tree = plugin.Tree("Services/RGBWalletService.cs");
        var method = RoslynPins.Method(tree, "RGBWalletService", "RestoreFromBackupAsync");
        var body = RoslynPins.BodyOf(method);

        var checks = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is IdentifierNameSyntax { Identifier.ValueText:
                "FindRegularFileAtAReservedDirectoryName" })
            .ToList();
        Assert.True(checks.Count == 1,
            $"RestoreFromBackupAsync invokes FindRegularFileAtAReservedDirectoryName {checks.Count} time(s); "
            + "exactly one call must stand between extraction and finalization, or a restored regular file at a "
            + "reserved directory name reaches disk and the wallet can then never send or receive again");
        RoslynPins.AssertBindsToMemberOf(plugin, tree, checks[0].Expression, SymbolKind.Method,
            "BTCPayServer.Plugins.RgbUtexo.Services.RGBWalletService",
            "FindRegularFileAtAReservedDirectoryName",
            "RestoreFromBackupAsync's reserved-directory-name check");

        var moves = body.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax access
                        && RoslynPins.NamesBclMember(access, "Directory", "Move"))
            .ToList();
        Assert.True(moves.Count == 1,
            $"RestoreFromBackupAsync performs {moves.Count} Directory.Move call(s); the pin compares the check "
            + "against exactly one finalization point");
        Assert.True(checks[0].SpanStart < moves[0].SpanStart,
            "the reserved-directory-name check must precede Directory.Move; running it afterwards leaves the "
            + "hostile regular file inside the live wallet data dir, which is the permanently unusable wallet this "
            + "refusal exists to prevent");

        var deferredHost = checks[0].Ancestors()
            .TakeWhile(node => !ReferenceEquals(node, body))
            .FirstOrDefault(node => node is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        Assert.True(deferredHost == null,
            $"the only call to FindRegularFileAtAReservedDirectoryName sits inside a {deferredHost?.GetType().Name}; "
            + "a call reachable only through a local function or lambda that nothing invokes satisfies every "
            + "lexical clause above while no restore is ever checked");

        var declarator = checks[0].Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
        Assert.True(declarator != null
                    && ReferenceEquals(declarator.Initializer?.Value, checks[0]),
            "the result of FindRegularFileAtAReservedDirectoryName must initialize a local; a call whose returned "
            + "value is discarded satisfies a call-site pin while every hostile backup is still accepted");
        var resultName = declarator!.Identifier.ValueText;
        RoslynPins.AssertNeverReassigned(method, resultName);

        var guards = body.DescendantNodes().OfType<IfStatementSyntax>()
            .Where(statement => statement.SpanStart > checks[0].SpanStart)
            .Where(statement => statement.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => id.Identifier.ValueText == resultName))
            .Where(statement => statement.Statement is BlockSyntax block
                                && block.Statements.OfType<ThrowStatementSyntax>().Any())
            .ToList();
        Assert.True(guards.Count == 1,
            $"'{resultName}' flows into {guards.Count} condition(s) that directly throw; exactly one must exist, "
            + "or the checked value never refuses the restore it was computed for");
        Assert.True(guards[0].Span.End < moves[0].SpanStart,
            $"the throw guarded by '{resultName}' does not complete before Directory.Move; the refusal must be "
            + "raised while the tree is still staging, not after finalization");

        RoslynPins.AssertNoLocalShadow(method, "FindRegularFileAtAReservedDirectoryName");
    }

    [Fact]
    public void TheReservedDirectoryNameWalkEnumeratesFilesNotDirectories_SoADotPrefixedOrNestedPlantIsStillSeen()
    {
        using var staging = new TempTree();
        staging.WriteFile(Path.Combine(FakeLibFingerprint, ".hidden", TransfersName), 0);

        Assert.Equal(TransfersName, RGBWalletService.FindRegularFileAtAReservedDirectoryName(staging.Path));
    }

    static RGBWalletService BuildService(IRestoreProcessRunner runner, RGBConfiguration cfg)
    {
        var rgbLib = new FakeRgbLib(cfg);
        var db = new RGBPluginDbContextFactory(Options.Create(new DatabaseOptions
        {
            ConnectionString =
                "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1;Command Timeout=1"
        }));
        var mnemonic = new MnemonicProtectionService(new EphemeralDataProtectionProvider(),
            NullLogger<MnemonicProtectionService>.Instance);
        var exec = new RestoreExecutor(runner, cfg, NullLogger<RestoreExecutor>.Instance);
        return new RGBWalletService(rgbLib, db, cfg, mnemonic, null!, null!, null!,
            NullLogger<RGBWalletService>.Instance, exec, null!);
    }

    sealed class StagingShapingRunner : IRestoreProcessRunner
    {
        readonly Action<string> _shape;
        public string StagingDir { get; private set; } = "";

        public StagingShapingRunner(Action<string> shape) => _shape = shape;

        public Task<RestoreRunResult> RunAsync(
            string backupPath, string stagingDir, string password, RestoreLimits limits, CancellationToken ct)
        {
            StagingDir = stagingDir;
            Directory.CreateDirectory(stagingDir);
            _shape(stagingDir);
            return Task.FromResult(new RestoreRunResult(RestoreOutcome.Exited, 0, "", true));
        }
    }

    sealed class TempBackup : IDisposable
    {
        public string Path { get; }

        public TempBackup()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"rgb-reserved-dir-backup-{Guid.NewGuid():N}.rgb");
            using var fs = File.Create(Path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            using (var enc = zip.CreateEntry("backup.enc").Open())
                enc.Write(new byte[16]);
            using var pub = new StreamWriter(zip.CreateEntry("backup.pub_data").Open());
            pub.Write("""{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""");
        }

        public void Dispose() { try { File.Delete(Path); } catch { } }
    }

    sealed class TempTree : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rgb-reserved-dir-staging-{Guid.NewGuid():N}");

        public TempTree() => Directory.CreateDirectory(Path);

        public void MakeDir(string relative) =>
            Directory.CreateDirectory(System.IO.Path.Combine(Path, relative));

        public void WriteFile(string relative, int bytes)
        {
            var full = System.IO.Path.Combine(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[bytes]);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
