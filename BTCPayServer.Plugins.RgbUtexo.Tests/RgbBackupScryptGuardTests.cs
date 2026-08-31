using System.IO.Compression;
using System.Text;
using BTCPayServer.Plugins.RgbUtexo.Services;
using SharpCompress.Common;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Compressors.ZStandard.Unsafe;
using SharpCompress.Providers;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The JSON shape asserted here was read out of a REAL rgb-lib 0.3.0-beta.30 backup produced by this
// plugin's own backup path, not inferred from rgb-lib's source: `backup.pub_data` is a plaintext
// entry in the outer archive reading
//   {"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32,"version":null,"algorithm":null},
//    "salt":"...","nonce":"...","version":1}
// If rgb-lib ever changes that shape, HonestRgbLibParameters_AreAccepted is the test that fails, and
// it must be re-derived from a fresh backup rather than adjusted to match a guess.
public class RgbBackupScryptGuardTests
{
    const string HonestPubData =
        """{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32,"version":null,"algorithm":null},"salt":"N9Kvm1/X1qvh2cMDcu030w","nonce":"gWczEOGYnxeXa2itnCL","version":1}""";

    static string PubData(object logN, object r, object p = null!, object len = null!) =>
        $$"""{"scrypt_params":{"log_n":{{logN}},"r":{{r}},"p":{{p ?? 1}},"len":{{len ?? 32}}},"salt":"x","nonce":"y","version":1}""";

    [Fact]
    public void HonestRgbLibParameters_AreAccepted()
    {
        // 128 * 8 * 2^17 = 128MB, which is what an honest backup genuinely costs — so the default
        // ceiling MUST sit above it or every real restore breaks.
        RgbBackupScryptGuard.ValidatePubData(HonestPubData);
    }

    [Fact]
    public void HonestParameters_CostExactlyOneHundredAndTwentyEightMegabytes()
    {
        var honestCost = 128L * 8 * (1L << 17);
        Assert.Equal(134_217_728L, honestCost);
        Assert.True(RgbBackupScryptGuard.DefaultMaxScryptMemoryBytes > honestCost,
            "rgb-lib writes Params::RECOMMENDED_LOG_N (17) at r=8, so this is what every genuine "
            + "backup costs and a ceiling at or below it refuses every restore. The ceiling bounds "
            + "the scrypt arena ONLY; whether a backup admitted here can actually complete is a "
            + "separate question about the helper's whole resident set, pinned by "
            + "RgbRestoreLimitClampTests against RGBConfiguration.RestoreRamMinBytes.");
    }

    [Fact]
    public void LargeLogN_IsRefused()
    {
        // log_n 25 at r 8 asks for 32GB in a single memory-hard allocation.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(25, 8)));
        Assert.Contains("32768MB", ex.Message);
    }

    [Fact]
    public void LargeR_IsRefused()
    {
        // The same cost reached through r instead of log_n: 128 * 512 * 2^17 = 8GB. A guard that only
        // bounded log_n would pass this.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(17, 512)));
        Assert.Contains("8192MB", ex.Message);
    }

    [Fact]
    public void AbsurdLogN_IsRefusedWithoutShiftWrapping()
    {
        // THE trap this ordering exists for: C# masks a shift count to 6 bits, so `1L << 64` is 1 and
        // log_n = 64 would compute a 1KB cost and sail through a memory-only check. The shape bound
        // must therefore be evaluated before the arithmetic.
        //
        // The discriminator is the SIZE-path phrasing, not the bare word "memory": every refusal names
        // the memory knob in its recovery hint, so asserting on "memory" alone was satisfied by the
        // shape path and the size path alike. Ablation-checked: deleting the log_n bound makes
        // 1L << 64 evaluate to 1, the computed cost becomes 1KB, nothing is thrown at all, and
        // Assert.Throws is what fails.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(64, 8)));
        Assert.Contains("log_n (64) exceeds the maximum", ex.Message);
        Assert.DoesNotContain("MB of memory", ex.Message);
    }

    [Fact]
    public void LargeParallelism_IsRefused()
    {
        // p multiplies WORK, not memory, so it is invisible to a memory-only ceiling: p = 1000 at
        // honest memory still buys a thousandfold CPU cost.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(17, 8, p: 1000)));
        Assert.Contains("scrypt p", ex.Message);
    }

    [Fact]
    public void LargeKeyLength_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(17, 8, len: 1_000_000)));
        Assert.Contains("scrypt len", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLogN_IsRefused(int logN)
    {
        Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData(PubData(logN, 8)));
    }

    [Fact]
    public void MissingScryptParams_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData("""{"salt":"x","version":1}"""));
        Assert.Contains("no scrypt parameters", ex.Message);
    }

    [Fact]
    public void MalformedJson_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(
            () => RgbBackupScryptGuard.ValidatePubData("not json at all"));
    }

    [Fact]
    public void MalformedArchive_IsReportedAsAValidationFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scrypt-malformed-{Guid.NewGuid():N}.rgb");
        File.WriteAllText(path, "not a zip");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("not a valid ZIP archive", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AVanishedUploadFile_RefusesWithoutNamingTheServerPathTheFrameworkPutInItsMessage()
    {
        var vanished = Path.Combine(Path.GetTempPath(), $"scrypt-vanished-{Guid.NewGuid():N}.rgb");

        var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(vanished));

        Assert.Equal(
            RgbBackupScryptGuard.UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath,
            ex.Message);
        Assert.DoesNotContain(vanished, ex.Message);
        Assert.DoesNotContain(Path.GetTempPath(), ex.Message);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Contains(vanished, ex.InnerException!.Message);
    }

    [Fact]
    public void AnUploadPathTheProcessCannotOpen_RefusesWithoutNamingIt_OnTheUnauthorizedAccessClause()
    {
        var directoryStandingWhereTheUploadShouldBe =
            Path.Combine(Path.GetTempPath(), $"scrypt-not-a-file-{Guid.NewGuid():N}.rgb");
        Directory.CreateDirectory(directoryStandingWhereTheUploadShouldBe);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => RgbBackupScryptGuard.ValidateFile(directoryStandingWhereTheUploadShouldBe));

            Assert.Equal(
                RgbBackupScryptGuard.UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath,
                ex.Message);
            Assert.DoesNotContain(directoryStandingWhereTheUploadShouldBe, ex.Message);
            Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
            Assert.Contains(directoryStandingWhereTheUploadShouldBe, ex.InnerException!.Message);
        }
        finally { Directory.Delete(directoryStandingWhereTheUploadShouldBe); }
    }

    [Fact]
    public void TheUnreadableUploadRefusal_ReachesTheStoreOwnerVerbatimSoHeKnowsToUploadItAgain()
    {
        var shown = Controllers.RgbOperatorFacingFailure.OperatorFacingLayerMessageOrFallback(
            new InvalidOperationException(
                RgbBackupScryptGuard.UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath),
            Controllers.RgbOperatorFacingFailure.EscalateToServerLogs);

        Assert.Contains("upload it again", shown);
        Assert.Contains("Nothing was restored", shown);
        Assert.NotEqual(Controllers.RgbOperatorFacingFailure.EscalateToServerLogs, shown);
    }

    [Fact]
    public void RaisedCeiling_AdmitsWhatTheDefaultRefuses()
    {
        // The recovery path for a false reject. Without this the guard could strand a genuine backup
        // permanently, which the trust invariant forbids; the message names the knob for this reason.
        var hostile = PubData(21, 8);
        Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidatePubData(hostile));
        RgbBackupScryptGuard.ValidatePubData(hostile, maxMemoryBytes: 4L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void ValidateFile_AcceptsAnHonestArchiveAndRefusesOneWithoutPubData()
    {
        var withPubData = WriteZip(("backup.enc", new byte[16]), (RgbBackupScryptGuard.PubDataEntryName,
            Encoding.UTF8.GetBytes(HonestPubData)));
        var withoutPubData = WriteZip(("backup.enc", new byte[16]));
        try
        {
            RgbBackupScryptGuard.ValidateFile(withPubData);

            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(withoutPubData));
            Assert.Contains("no 'backup.pub_data' entry", ex.Message);
        }
        finally
        {
            File.Delete(withPubData);
            File.Delete(withoutPubData);
        }
    }

    [Fact]
    public void ValidateFile_AcceptsTheZstandardZipMethodUsedByRgbLibBeta30()
    {
        var path = WriteZstandardZip(
            ("backup.enc", new byte[16]),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HonestPubData)));
        try
        {
            RgbBackupScryptGuard.ValidateFile(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateFile_RefusesZstandardFramesAboveTheParentMemoryLimit()
    {
        var path = WriteZstandardZip(
            new LargeWindowZstandardProvider(),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HonestPubData)));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("excessive Zstandard frame", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateFile_RefusesContradictoryLocalAndCentralEntryNames()
    {
        var path = WriteZip((RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HonestPubData)));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var name = Encoding.ASCII.GetBytes(RgbBackupScryptGuard.PubDataEntryName);
            var localNameOffset = bytes.AsSpan().IndexOf(name);
            Assert.True(localNameOffset >= 0);
            bytes[localNameOffset] = (byte)'B';
            File.WriteAllBytes(path, bytes);

            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("contradictory local and central ZIP headers", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateFile_RefusesAContradictoryLocalNameOnAnExtraEntry()
    {
        const string centralExtra = "x/backup.pub_data";
        var path = WriteZip(
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HonestPubData)),
            (centralExtra, Encoding.UTF8.GetBytes(HOSTILE_22_DECL)));
        try
        {
            var bytes = File.ReadAllBytes(path);
            var name = Encoding.ASCII.GetBytes(centralExtra);
            var localNameOffset = bytes.AsSpan().IndexOf(name);
            Assert.True(localNameOffset >= 0);
            bytes[localNameOffset] = (byte)'.';
            File.WriteAllBytes(path, bytes);

            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("contradictory local and central ZIP headers", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateFile_RefusesHostileParametersInsideARealArchive()
    {
        // End-to-end through the ZIP reader, so the test cannot pass on a guard that validates JSON
        // but never actually reaches into the file.
        var path = WriteZip(("backup.enc", new byte[16]),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(PubData(25, 8))));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("32768MB", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ValidateFile_RefusesAnImplausiblyLargePubData()
    {
        var path = WriteZip((RgbBackupScryptGuard.PubDataEntryName,
            new byte[RgbBackupScryptGuard.MaxPubDataBytes + 1]));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("implausibly large", ex.Message);
        }
        finally { File.Delete(path); }
    }

    // MEASURED, not assumed: .NET's ZipArchive.GetEntry returns the FIRST entry with a duplicated name,
    // while rgb-lib's reader takes the LAST (verified against the real librgblibcffi beta.30, and
    // Python's zipfile agrees with rgb-lib). Two consumers of the same bytes therefore disagree about
    // which parameters apply. A guard that reads one and a KDF that runs the other is not a guard at
    // all: honest log_n=17 in the first entry, log_n=25 (32GB) in the last, and the check passes while
    // the child allocates.
    //
    // The fix refuses duplicates rather than trying to match rgb-lib's tie-break, because matching it
    // would make this plugin's safety depend on an undocumented ordering detail inside a third-party
    // library that is free to change it.
    [Fact]
    public void DuplicatePubDataEntries_AreRefusedRatherThanPicked()
    {
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HOSTILE_JSON_DECL)));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            // The collision check fires first and is the stronger one; the pub_data-specific refusal
            // remains behind it as a backstop for anyone who removes the collision check.
            Assert.Contains("denote the same file", ex.Message);
            Assert.Contains(RgbBackupScryptGuard.PubDataEntryName, ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicatePubDataEntries_AreRefusedWhicheverOrderTheyAppearIn()
    {
        // Order-independent on purpose: refusing only when the hostile copy happens to be last would
        // still leave the bypass open against a reader with the opposite tie-break.
        var path = WriteZipAllowingDuplicates(
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HOSTILE_JSON_DECL)),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)));
        try
        {
            Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicateEntryNamesOfAnyKind_AreRefused()
    {
        // The same first-vs-last divergence applies to every entry rgb-lib reads by name, not just
        // pub_data, so the archive shape itself is refused rather than one entry being special-cased.
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            ("backup.enc", ENC_BYTES),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("denote the same file", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PubDataLargerThanItDeclares_IsRefusedWithoutReadingItAll()
    {
        // entry.Length is the DECLARED uncompressed size from the central directory, i.e. attacker
        // input. Trusting it and then calling ReadToEnd would read the real stream, however large it
        // turns out to be, into a string in the BTCPay process. The read is bounded instead.
        var payload = new byte[RgbBackupScryptGuard.MaxPubDataBytes * 4];
        Array.Fill(payload, (byte)' ');
        var path = WriteZipAllowingDuplicates((RgbBackupScryptGuard.PubDataEntryName, payload));
        try
        {
            Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
        }
        finally { File.Delete(path); }
    }

    // ROUND 3, MEASURED against the real librgblibcffi 0.3.0-beta.30: an archive holding an honest
    // `backup.pub_data` AND a hostile `./backup.pub_data` was ACCEPTED by the previous version of this
    // guard (which compared names ordinally, so the two looked unrelated) while rgb-lib ran the hostile
    // one — 4.33 GB peak RSS at log_n 22, against 170 MB for the honest file.
    //
    // Verified mechanism, reproduced locally: extracting an archive with both names produces ONE file
    // on disk containing the SECOND entry. rgb-lib extracts and reads by path, so the filesystem — not
    // a name lookup — decides what `backup.pub_data` means. That is why `./` collapses, why the later
    // entry wins, and why case matters on the default macOS and Windows volumes.
    [Theory]
    [InlineData("./backup.pub_data")]
    [InlineData(".\\backup.pub_data")]
    [InlineData("././backup.pub_data")]
    [InlineData("/backup.pub_data")]
    [InlineData("BACKUP.PUB_DATA")]
    [InlineData("Backup.Pub_Data")]
    [InlineData(" backup.pub_data ")]
    [InlineData("backup.pub_data.")]
    [InlineData("backup.pub_data .")]
    public void AnEntryThatDenotesTheSameFileAsPubData_IsRefusedAlongsideIt(string decoyName)
    {
        // The decoy carries the HOSTILE parameters and the exactly-named entry carries honest ones, so
        // a guard that reads the exact name and ignores the decoy returns ACCEPT — the false-ACCEPT
        // this closes. Every spelling here collapses to the same extracted file.
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)),
            (decoyName, Encoding.UTF8.GetBytes(HOSTILE_22_DECL)));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            // Names the colliding entry, so an operator can see WHICH spelling was rejected rather than
            // just that something was.
            Assert.Contains("denote the same file", ex.Message);
            Assert.Contains(decoyName.Trim(), ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("./backup.pub_data")]
    [InlineData("BACKUP.PUB_DATA")]
    public void AMisspelledPubDataIsRefusedEvenWhenItIsTheOnlyOne(string onlyName)
    {
        // rgb-lib would extract and run this; matching that behaviour would mean re-deriving its path
        // handling here and being wrong the next time it changes. Refuse instead.
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            (onlyName, Encoding.UTF8.GetBytes(HOSTILE_22_DECL)));
        try
        {
            Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnrelatedExtraEntries_AreStillAccepted()
    {
        // The rule refuses AMBIGUITY, not unfamiliarity: a future rgb-lib that adds an entry must still
        // restore, or this guard becomes a permanent false-REJECT of legitimate backups.
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            ("some_future_file.dat", new byte[8]),
            ("nested/other.dat", new byte[8]),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)));
        try { RgbBackupScryptGuard.ValidateFile(path); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("backup.pub_data::$DATA")]
    [InlineData("BACKUP~1.PUB")]
    [InlineData("backup.pub_data\u200b")]
    public void PlatformSpecificAliasesAreRefusedByThePortableNameRule(string alias)
    {
        var path = WriteZipAllowingDuplicates(
            ("backup.enc", ENC_BYTES),
            (RgbBackupScryptGuard.PubDataEntryName, Encoding.UTF8.GetBytes(HONEST_JSON_DECL)),
            (alias, Encoding.UTF8.GetBytes(HOSTILE_22_DECL)));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => RgbBackupScryptGuard.ValidateFile(path));
            Assert.Contains("non-portable entry names", ex.Message);
            Assert.Contains(alias, ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("some_future_file.dat")]
    [InlineData("nested/other-file_2.dat")]
    [InlineData("nested/directory/")]
    public void OrdinaryFutureEntryNamesRemainPortable(string name)
        => Assert.True(RgbBackupScryptGuard.IsPortableName(name));

    [Fact]
    public void CanonicalName_CollapsesEverySpellingOfTheSameFile()
    {
        var target = RgbBackupScryptGuard.CanonicalName("backup.pub_data");
        foreach (var spelling in new[]
                 {
                     "./backup.pub_data", ".\\backup.pub_data", "././backup.pub_data",
                     "/backup.pub_data", "//backup.pub_data", "BACKUP.PUB_DATA", " backup.pub_data ",
                     "backup.pub_data.", "backup.pub_data .",
                 })
            Assert.Equal(target, RgbBackupScryptGuard.CanonicalName(spelling));

        // ...and does NOT collapse genuinely different files, or the ambiguity rule would refuse
        // honest archives.
        Assert.NotEqual(target, RgbBackupScryptGuard.CanonicalName("backup.enc"));
        Assert.NotEqual(target, RgbBackupScryptGuard.CanonicalName("sub/backup.pub_data"));
    }

    const string HOSTILE_22_DECL = """{"scrypt_params":{"log_n":22,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""";

    const string HONEST_JSON_DECL = """{"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""";
    const string HOSTILE_JSON_DECL = """{"scrypt_params":{"log_n":25,"r":8,"p":1,"len":32},"salt":"x","nonce":"y","version":1}""";
    static readonly byte[] ENC_BYTES = new byte[16];

    // Deliberately does NOT dedupe: .NET's ZipArchive.CreateEntry permits duplicate names, which is
    // what makes the archive above constructible by an attacker in the first place.
    static string WriteZipAllowingDuplicates(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scrypt-dup-{Guid.NewGuid():N}.rgb");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
        return path;
    }

    static string WriteZip(params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scrypt-guard-{Guid.NewGuid():N}.rgb");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(content);
        }
        return path;
    }

    static string WriteZstandardZip(params (string Name, byte[] Content)[] entries)
        => WriteZstandardZip(provider: null, entries);

    static string WriteZstandardZip(
        ICompressionProvider? provider,
        params (string Name, byte[] Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scrypt-zstd-{Guid.NewGuid():N}.rgb");
        using var fs = File.Create(path);
        var options = new ZipWriterOptions(CompressionType.ZStandard);
        if (provider is not null)
            options.Providers = CompressionProviderRegistry.Default.With(provider);
        using var writer = new ZipWriter(fs, options);
        foreach (var (name, content) in entries)
        {
            using var input = new MemoryStream(content);
            writer.Write(name, input);
        }
        return path;
    }

    sealed class LargeWindowZstandardProvider : CompressionProviderBase
    {
        public override CompressionType CompressionType => CompressionType.ZStandard;
        public override bool SupportsCompression => true;
        public override bool SupportsDecompression => false;

        public override Stream CreateCompressStream(Stream destination, int compressionLevel)
        {
            var stream = new CompressionStream(destination, compressionLevel);
            stream.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, RgbBackupScryptGuard.MaxZstandardWindowLog + 1);
            return stream;
        }

        public override Stream CreateDecompressStream(Stream source) =>
            throw new NotSupportedException();
    }

}
