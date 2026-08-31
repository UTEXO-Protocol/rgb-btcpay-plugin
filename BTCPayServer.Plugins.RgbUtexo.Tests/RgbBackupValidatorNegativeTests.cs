using System.IO.Compression;
using System.Text;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBackupValidatorNegativeTests
{
    static IFormFile FromBytes(byte[] content, string name = "backup.rgb")
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", name);
    }

    static byte[] ZipWithEntries(Action<ZipArchive> build)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            build(zip);
        return ms.ToArray();
    }

    [Fact]
    public async Task ZipBomb_CompressionRatio_RejectsWithSizeLimit()
    {
        var content = ZipWithEntries(zip =>
        {
            var entry = zip.CreateEntry("payload.bin", CompressionLevel.Optimal);
            using var s = entry.Open();
            var chunk = new byte[64 * 1024];
            for (int i = 0; i < (RgbBackupValidator.MaxEntryUncompressedBytes / chunk.Length) + 2; i++)
                s.Write(chunk, 0, chunk.Length);
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("exceeds limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryCount_1001_Rejects()
    {
        var content = ZipWithEntries(zip =>
        {
            for (int i = 0; i <= RgbBackupValidator.MaxEntryCount; i++)
                zip.CreateEntry($"e{i}.dat");
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("too many entries", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TotalUncompressed_51MB_Rejects()
    {
        var chunk = new byte[1024 * 1024];
        var content = ZipWithEntries(zip =>
        {
            for (int i = 0; i < 51; i++)
            {
                var e = zip.CreateEntry($"chunk{i}.bin", CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(chunk, 0, chunk.Length);
            }
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("exceeds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerEntry_51MB_Rejects()
    {
        var chunk = new byte[1024 * 1024];
        var content = ZipWithEntries(zip =>
        {
            var e = zip.CreateEntry("big.bin", CompressionLevel.NoCompression);
            using var s = e.Open();
            for (int i = 0; i < 51; i++)
                s.Write(chunk, 0, chunk.Length);
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("exceeds limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PathTraversal_DotDot_Rejects()
    {
        var content = ZipWithEntries(zip => zip.CreateEntry("../../etc/passwd"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("path traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AbsolutePath_Slash_Rejects()
    {
        var content = ZipWithEntries(zip => zip.CreateEntry("/etc/passwd"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("absolute path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PathTraversal_Backslash_Rejects()
    {
        var content = ZipWithEntries(zip => zip.CreateEntry("..\\..\\evil"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(content)));
        Assert.Contains("path traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Symlink_BenignEntry_AcceptedBecauseExternalAttributesAreNotInspected_ChangingThatIsAPolicyChange()
    {
        var content = ZipWithEntries(zip =>
        {
            var e = zip.CreateEntry("link_target.txt");
            using var s = new StreamWriter(e.Open());
            s.Write("regular content");
        });
        await RgbBackupValidator.ValidateAsync(FromBytes(content));
    }

    [Fact]
    public async Task EmptyFile_ZeroBytes_Rejects()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(Array.Empty<byte>())));
        Assert.Contains("too small", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonZipMagic_RandomBytes_Rejects()
    {
        var rnd = new byte[100];
        new Random(1234).NextBytes(rnd);
        rnd[0] = 0xFF; rnd[1] = 0xFE; rnd[2] = 0xFD; rnd[3] = 0xFC;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(rnd)));
        Assert.Contains("ZIP archive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WellFormedMinimalBackup_Accepts()
    {
        var content = ZipWithEntries(zip =>
        {
            var e = zip.CreateEntry("rgb-backup.json");
            using var s = new StreamWriter(e.Open());
            s.Write("{\"version\":1}");
        });
        await RgbBackupValidator.ValidateAsync(FromBytes(content));
    }

    sealed class SlowStream : Stream
    {
        readonly byte[] _buffer;
        int _pos;
        readonly TimeSpan _delay;

        public SlowStream(byte[] buffer, TimeSpan delay) { _buffer = buffer; _delay = delay; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _buffer.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            if (_pos >= _buffer.Length) return 0;
            int n = Math.Min(count, _buffer.Length - _pos);
            Array.Copy(_buffer, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    sealed class SlowFormFile : IFormFile
    {
        readonly byte[] _content;
        public SlowFormFile(byte[] content) { _content = content; }
        public string ContentType => "application/octet-stream";
        public string ContentDisposition => "";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _content.Length;
        public string Name => "file";
        public string FileName => "backup.rgb";
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => throw new NotSupportedException();
        public Stream OpenReadStream() => new SlowStream(_content, TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task CancellationDuringTheOnlyTokenObservingStep_TheFormFileCopy_Throws()
    {
        var content = ZipWithEntries(zip => zip.CreateEntry("anything.dat"));
        var file = new SlowFormFile(content);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RgbBackupValidator.ValidateAsync(file, cts.Token));
    }
}
