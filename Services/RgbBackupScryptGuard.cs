using System.Linq;
using System.Text.Json;
using SharpCompress.Archives;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Compressors.ZStandard.Unsafe;
using SharpCompress.Providers;
using SharpCompress.Providers.Default;
using SharpCompress.Readers;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

// WHY this exists: rgb-lib's restore_backup reads the scrypt KDF cost parameters out of the
// backup's own `backup.pub_data` entry and runs the KDF BEFORE it decrypts anything, so the cost
// is chosen by whoever produced the file, not by the operator. rgb-lib does not bound them and
// RustCrypto's scrypt Params::new only rejects arithmetic overflow, never a memory ceiling — so
// `log_n: 25, r: 8` asks for a single ~32 GiB allocation. Because scrypt is memory-hard it touches
// what it allocates, making the spike immediate rather than gradual, which is exactly the shape a
// sampling watchdog (RestoreProcessRunner's poll loop) cannot bound: it can only cut the spike's
// duration, never its magnitude. This guard is the missing plugin-side owner for the audit's
// "unbounded scrypt" clause, and it is deliberately a PRE-FLIGHT check on the file: it costs no
// child process, no staging directory, and it works identically on every platform, unlike an
// rlimit, which exists only on Linux.
//
// The parsed shape below was measured against a real rgb-lib 0.3.0-beta.30 backup, whose
// `backup.pub_data` is plaintext JSON in the outer archive:
//   {"scrypt_params":{"log_n":17,"r":8,"p":1,"len":32,...},"salt":"...","nonce":"...","version":1}
// Those honest defaults cost 128 * r * 2^log_n = 128 MiB, so the ceiling has to sit above that.
public static class RgbBackupScryptGuard
{
    public const string PubDataEntryName = "backup.pub_data";

    public const string UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath =
        "Backup file could not be read back from the server's own temporary upload storage, so its "
        + "key-derivation cost could not be checked before restoring. Nothing was restored and the "
        + "backup file you hold is untouched, so upload it again; if it keeps failing, the server is "
        + "out of temporary disk space or cannot write to it, and the underlying storage error is "
        + "recorded in the server log.";

    public const long DefaultMaxScryptMemoryBytes = 536_870_912;

    // scrypt's work is proportional to p as well as to N*r, and no honest producer needs
    // parallelism here: rgb-lib writes p = 1.
    public const int MaxParallelism = 16;

    // The derived key is a symmetric key; rgb-lib writes 32. A large `len` is another work
    // multiplier with no legitimate use.
    public const int MaxKeyLenBytes = 1024;

    // log_n is an exponent, so it is bounded FIRST and separately: 1L << log_n is undefined-ish
    // for large shifts (C# masks the count to 6 bits, silently wrapping 64 to 1), which would turn
    // an absurd request into a tiny computed cost. 40 is far above anything legitimate and keeps
    // 128 * r * 2^log_n inside long for every r this guard admits.
    public const int MaxLogN = 40;
    public const int MaxR = 1024;

    // `backup.pub_data` is a handful of bytes in practice (157 in the measured file). A large one is
    // not a legitimate shape, and this bounds the parse itself rather than trusting the 50 MiB
    // per-entry cap that RgbBackupValidator applies to wallet data.
    public const int MaxPubDataBytes = 64 * 1024;

    // Zstandard's streaming decoder otherwise accepts a 128 MiB window by default. This guard runs
    // in the BTCPay process before the child supervisor, so an attacker-controlled frame must not turn
    // a 64 KiB JSON read into a 128 MiB parent allocation even inside the process-wide restore gate.
    // An 8 MiB window matches the conservative RFC 9659 decoder ceiling and accepts the streaming
    // frames emitted by beta.30 while cutting the library default by 16x.
    internal const int MaxZstandardWindowLog = 23;

    // Only formats rgb-lib beta.30 has emitted (plus Stored for simple fixtures/future-compatible
    // archives) are available while parsing attacker-controlled data in the parent process.
    internal static readonly CompressionProviderRegistry BoundedProviders = CompressionProviderRegistry.Empty
        .With(new DeflateCompressionProvider())
        .With(new BoundedZstandardProvider());

    static readonly ReaderOptions ArchiveOptions = new() { Providers = BoundedProviders };

    public static void ValidateFile(string backupPath, long maxMemoryBytes = DefaultMaxScryptMemoryBytes)
    {
        IArchive zip;
        try
        {
            zip = ZipArchive.OpenArchive(backupPath, ArchiveOptions);
        }
        catch (ArchiveException ex)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive", ex);
        }
        catch (InvalidDataException ex)
        {
            // Mirrors RgbBackupValidator's wording for the same condition, so a malformed upload reads
            // the same to an operator whichever check happens to see it first.
            throw new InvalidOperationException("Backup file is not a valid ZIP archive", ex);
        }
        catch (IOException ex)
        {
            // Includes FileNotFoundException and DirectoryNotFoundException. Whatever this security
            // check cannot read becomes a clear restore refusal instead of an unhandled IO exception.
            throw new InvalidOperationException(
                UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                UnreadableBackupFileRefusalWithoutTheFrameworkIoTextThatWouldNameTheServerPath, ex);
        }

        using var _ = zip;
        List<IArchiveEntry> entries;
        try
        {
            // SharpCompress loads central-directory entries lazily. Take one past the controller's
            // public limit so direct service callers get the same bound without materialising an
            // attacker-sized directory in the BTCPay process.
            entries = zip.Entries.Cast<IArchiveEntry>().Take(RgbBackupValidator.MaxEntryCount + 1).ToList();
        }
        catch (ArchiveException ex)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive", ex);
        }
        if (entries.Count > RgbBackupValidator.MaxEntryCount)
            throw new InvalidOperationException("Backup archive contains too many entries");

        // A ZIP entry's name, sizes, and method exist in both the central directory and its local
        // header. Check EVERY entry before using any central name for collision policy: otherwise an
        // innocuous-looking extra entry can carry a local name that aliases backup.pub_data. Opening
        // the stream is what makes SharpCompress load the local header; the restricted provider set
        // above ensures this probe cannot instantiate an unbounded decoder, and no payload is read.
        foreach (var candidate in entries)
            ValidateLocalHeader(candidate);

        // AMBIGUITY IS REFUSED, never resolved — and ambiguity is judged under FILESYSTEM path
        // semantics, not string equality.
        //
        // Two rounds of review broke earlier versions of this check, both by the same mechanism, and
        // the mechanism is now understood rather than guessed at. rgb-lib EXTRACTS the archive and then
        // reads the file by path, so the filesystem — not a ZIP name lookup — decides what
        // `backup.pub_data` means. Measured, reproducing it locally: an archive holding
        // `backup.pub_data` and `./backup.pub_data` extracts to ONE file whose contents are the SECOND
        // entry. So:
        //   • byte-identical duplicate names (round 2): .NET's GetEntry reads the first, extraction
        //     leaves the last — validated parameters were not the executed ones.
        //   • `./backup.pub_data` beside `backup.pub_data` (round 3): an ordinal comparer sees two
        //     unrelated names, extraction sees one file. Measured at 4.33 GB peak RSS against the real
        //     librgblibcffi 0.3.0-beta.30 while this guard returned ACCEPT on the honest copy.
        //
        // Enumerating variants is what failed twice: the first fix closed exactly byte-identical names,
        // the second would have had to guess every canonicalisation rgb-lib and the filesystem might
        // apply (`.\`, `//`, case on a case-insensitive volume, trailing space, ...). So the rule is
        // inverted: canonicalise AGGRESSIVELY — deliberately a superset of plausible reader behaviour —
        // and refuse when two entries could possibly denote the same file to anyone. Over-refusal here
        // costs a legitimate-but-bizarre archive; under-refusal costs a false ACCEPT, which the trust
        // invariant forbids.
        var collisions = entries
            .GroupBy(e => CanonicalName(e.Key ?? string.Empty), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g.Select(e => e.Key ?? string.Empty)))
            .ToList();
        if (collisions.Count > 0)
            throw new InvalidOperationException(
                $"Backup archive contains entries that denote the same file ({string.Join("; ", collisions)}). "
                + "Different readers resolve those differently, so this archive is refused rather than "
                + "guessed at.");

        // Canonicalisation cannot safely enumerate every filesystem alias. In particular, NTFS alternate
        // data streams (`name::$DATA`) and generated 8.3 names (`NAME~1.EXT`) can address an existing file
        // without looking equal here. Restrict outer names to the portable grammar rgb-lib itself emits;
        // ordinary future entries remain allowed, while platform-specific aliases are refused outright.
        var nonPortable = entries.Select(e => e.Key ?? string.Empty).Where(n => !IsPortableName(n)).ToList();
        if (nonPortable.Count > 0)
            throw new InvalidOperationException(
                $"Backup archive contains non-portable entry names ({string.Join("; ", nonPortable)}). "
                + "Their filesystem meaning is ambiguous, so this archive is refused.");

        // Anything that canonicalises to the sensitive name must ALSO be spelled exactly that way. An
        // archive whose only pub_data is `./backup.pub_data` is refused rather than read: rgb-lib would
        // extract and run it, and matching that behaviour would mean re-deriving its path handling here
        // and being wrong the next time it changes.
        var canonicalTarget = CanonicalName(PubDataEntryName);
        var canonicalMatches = entries
            .Where(e => string.Equals(CanonicalName(e.Key ?? string.Empty), canonicalTarget, StringComparison.Ordinal))
            .ToList();
        if (canonicalMatches.Count == 0)
            throw new InvalidOperationException(
                $"Backup archive has no '{PubDataEntryName}' entry, so its key-derivation cost cannot be "
                + "checked before restoring. Refusing to restore.");

        var matches = canonicalMatches
            .Where(e => string.Equals(e.Key, PubDataEntryName, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Backup archive spells its '{PubDataEntryName}' entry as "
                + $"'{canonicalMatches[0].Key}'. Only the exact name is accepted, because any other "
                + "spelling is resolved differently by different readers. Refusing to restore.");

        var entry = matches[0];
        if (entry.Size > MaxPubDataBytes)
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' is implausibly large ({entry.Size} bytes). Refusing to restore.");
        if (entry.CompressionType is not (CompressionType.None or CompressionType.Deflate or CompressionType.ZStandard))
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' uses unsupported compression {entry.CompressionType}. Refusing to restore.");

        try
        {
            ValidatePubData(ReadBounded(entry), maxMemoryBytes);
        }
        catch (ZstdException ex)
        {
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' uses an invalid or excessive Zstandard frame. Refusing to restore.", ex);
        }
    }

    // Split out from ValidateFile so the cost policy is testable without building a ZIP.
    internal static void ValidatePubData(string pubDataJson, long maxMemoryBytes = DefaultMaxScryptMemoryBytes)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(pubDataJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' is not valid JSON, so its key-derivation cost cannot be "
                + "checked before restoring. Refusing to restore.", ex);
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("scrypt_params", out var p)
            || p.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' declares no scrypt parameters. Refusing to restore.");

        var logN = ReadPositiveInt(p, "log_n");
        var r = ReadPositiveInt(p, "r");
        var parallelism = ReadPositiveInt(p, "p");
        var keyLen = ReadPositiveInt(p, "len");

        // Shape before arithmetic: every bound below must hold before 1L << logN is evaluated.
        if (logN > MaxLogN)
            throw Refuse($"scrypt log_n ({logN}) exceeds the maximum this plugin will attempt ({MaxLogN})");
        if (r > MaxR)
            throw Refuse($"scrypt r ({r}) exceeds the maximum this plugin will attempt ({MaxR})");
        if (parallelism > MaxParallelism)
            throw Refuse($"scrypt p ({parallelism}) exceeds the maximum this plugin will attempt ({MaxParallelism})");
        if (keyLen > MaxKeyLenBytes)
            throw Refuse($"scrypt len ({keyLen}) exceeds the maximum this plugin will attempt ({MaxKeyLenBytes})");

        var memoryBytes = 128L * r * (1L << logN);
        if (memoryBytes > maxMemoryBytes)
            throw Refuse(
                $"restoring it would ask scrypt for {memoryBytes / (1024 * 1024)}MB of memory "
                + $"(log_n={logN}, r={r}), above the {maxMemoryBytes / (1024 * 1024)}MB limit");
    }

    // Deliberately a SUPERSET of the normalisations a ZIP reader, an extractor, or a filesystem might
    // apply, because the cost of canonicalising too much is refusing a strange-but-honest archive,
    // while the cost of canonicalising too little is a false ACCEPT. Case folding is included because
    // the default macOS and Windows volumes are case-insensitive, so `BACKUP.PUB_DATA` and
    // `backup.pub_data` are the same extracted file there.
    internal static string CanonicalName(string raw)
    {
        var value = raw.Replace('\\', '/').Trim();
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];
        value = value.TrimStart('/');
        // Win32 strips trailing spaces and periods from EACH path component. Applying that rule only
        // to the whole entry left `backup.pub_data.` distinct here while extraction on Windows could
        // overwrite `backup.pub_data`, recreating the validate-one/execute-another bypass.
        value = string.Join('/', value.Split('/').Select(segment => segment.TrimEnd(' ', '.')));
        return value.ToLowerInvariant();
    }

    internal static bool IsPortableName(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Contains('\\') || raw.StartsWith('/')) return false;
        var value = raw.EndsWith('/') ? raw[..^1] : raw;
        if (value.Length == 0) return false;
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.')) return false;
            if (segment.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))) return false;
        }
        return true;
    }

    // entry.Size is the DECLARED uncompressed size out of the central directory — attacker input.
    // ReadToEnd would trust it and then read whatever the stream actually produces, into a string in
    // the BTCPay process. This reads one byte past the cap and refuses, so the parent's allocation is
    // bounded by the policy rather than by the archive's own claim about itself.
    static string ReadBounded(IArchiveEntry entry)
    {
        var buffer = new byte[MaxPubDataBytes + 1];
        using var stream = entry.OpenEntryStream();
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxPubDataBytes)
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' is larger than the {MaxPubDataBytes} bytes this plugin will "
                + "read, regardless of the size it declares. Refusing to restore.");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    static void ValidateLocalHeader(IArchiveEntry entry)
    {
        if (entry.CompressionType is not (CompressionType.None or CompressionType.Deflate or CompressionType.ZStandard))
            throw new InvalidOperationException(
                $"Backup entry '{entry.Key}' uses unsupported compression {entry.CompressionType}. Refusing to restore.");

        var centralKey = entry.Key;
        var centralSize = entry.Size;
        var centralCompressedSize = entry.CompressedSize;
        var centralCompression = entry.CompressionType;
        try
        {
            using var stream = entry.OpenEntryStream();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Backup entry '{centralKey}' has an invalid or unsupported local ZIP header. Refusing to restore.", ex);
        }

        if (!string.Equals(entry.Key, centralKey, StringComparison.Ordinal)
            || entry.Size != centralSize
            || entry.CompressedSize != centralCompressedSize
            || entry.CompressionType != centralCompression)
            throw new InvalidOperationException(
                $"Backup entry '{centralKey}' has contradictory local and central ZIP headers. "
                + "Different readers resolve those differently, so this archive is refused.");
    }

    static int ReadPositiveInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v) || !v.TryGetInt32(out var value))
            throw new InvalidOperationException(
                $"Backup '{PubDataEntryName}' has no usable scrypt '{name}'. Refusing to restore.");
        if (value <= 0)
            throw Refuse($"scrypt {name} ({value}) is not a positive value");
        return value;
    }

    // The limit is named in the message on purpose: this guard can only ever false-REJECT, and a
    // false reject here would otherwise be an unrecoverable "your backup will not restore". An
    // operator who sees the number can raise RestoreScryptMemoryCapBytes and retry.
    static InvalidOperationException Refuse(string detail) =>
        new($"Refusing to restore this backup: {detail}. "
            + "If this backup is genuinely yours, raise the restore scrypt memory limit "
            + "(RGB_RESTORE_SCRYPT_MEMORY_CAP_BYTES) and try again.");

    sealed class BoundedZstandardProvider : CompressionProviderBase
    {
        public override CompressionType CompressionType => CompressionType.ZStandard;
        public override bool SupportsCompression => false;
        public override bool SupportsDecompression => true;

        public override Stream CreateCompressStream(Stream destination, int compressionLevel) =>
            throw new NotSupportedException("The backup guard only reads Zstandard data.");

        public override Stream CreateDecompressStream(Stream source)
        {
            var stream = new DecompressionStream(source);
            stream.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxZstandardWindowLog);
            return stream;
        }
    }
}
