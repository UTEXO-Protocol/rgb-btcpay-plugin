using System.IO.Compression;
using System.Text;
using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.AspNetCore.Http;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbBackupInflationMeasurementTests
{
    static IFormFile FromBytes(byte[] content) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", "backup.rgb");

    static byte[] DeflatedZip(params (string Name, long ZeroBytes)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var chunk = new byte[1024 * 1024];
            foreach (var (name, zeroBytes) in entries)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                var left = zeroBytes;
                while (left > 0)
                {
                    var take = (int)Math.Min(left, chunk.Length);
                    stream.Write(chunk, 0, take);
                    left -= take;
                }
            }
        }
        return ms.ToArray();
    }

    static int OverwriteDeclaredUncompressedSizes(byte[] zip, uint declaredBytes)
    {
        var eocd = -1;
        for (var i = zip.Length - 22; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(zip, i) == 0x06054b50) { eocd = i; break; }
        }
        Assert.True(eocd >= 0, "the fixture must be a well-formed ZIP with an end-of-central-directory record");

        var entryCount = BitConverter.ToUInt16(zip, eocd + 10);
        var cursor = (int)BitConverter.ToUInt32(zip, eocd + 16);
        var patched = 0;
        for (var e = 0; e < entryCount; e++)
        {
            Assert.Equal(0x02014b50u, BitConverter.ToUInt32(zip, cursor));
            var nameLength = BitConverter.ToUInt16(zip, cursor + 28);
            var extraLength = BitConverter.ToUInt16(zip, cursor + 30);
            var commentLength = BitConverter.ToUInt16(zip, cursor + 32);
            var localHeader = (int)BitConverter.ToUInt32(zip, cursor + 42);

            BitConverter.GetBytes(declaredBytes).CopyTo(zip, cursor + 24);
            Assert.Equal(0x04034b50u, BitConverter.ToUInt32(zip, localHeader));
            BitConverter.GetBytes(declaredBytes).CopyTo(zip, localHeader + 22);
            patched++;

            cursor += 46 + nameLength + extraLength + commentLength;
        }
        return patched;
    }

    [Fact]
    public async Task EntryDeclaringOneKilobyteWhileActuallyInflatingSixtyFourMegabytes_IsRefused()
    {
        var honest = DeflatedZip(("wallet.sqlite", 64L * 1024 * 1024));
        Assert.True(honest.Length < 5_242_880,
            "the hostile archive must fit inside the controller's 5MiB request limit for the finding to apply");
        var hostile = (byte[])honest.Clone();
        Assert.Equal(1, OverwriteDeclaredUncompressedSizes(hostile, 1024));

        var declaredOnlyView = new MemoryStream(hostile);
        using (var zip = new ZipArchive(declaredOnlyView, ZipArchiveMode.Read, leaveOpen: true))
            Assert.Equal(1024, zip.Entries[0].Length);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(hostile)));
        Assert.Contains("decompresses to more than", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("50MB", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusalOfALyingArchiveNamesTheLimitSoAnOperatorCanActOnIt()
    {
        var hostile = DeflatedZip(("wallet.sqlite", 64L * 1024 * 1024));
        OverwriteDeclaredUncompressedSizes(hostile, 4096);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(hostile)));
        Assert.Contains($"{RgbBackupValidator.MaxTotalUncompressedBytes / 1024 / 1024}MB", ex.Message);
        Assert.Contains($"{RgbBackupValidator.MaxEntryUncompressedBytes / 1024 / 1024}MB", ex.Message);
    }

    [Fact]
    public async Task ManyEntriesEachDeclaringKilobytesWhileInflatingMegabytes_IsRefused()
    {
        var entries = Enumerable.Range(0, 40).Select(i => ($"chunk{i}.bin", 4L * 1024 * 1024)).ToArray();
        var hostile = DeflatedZip(entries);
        Assert.Equal(40, OverwriteDeclaredUncompressedSizes(hostile, 512));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(hostile)));
        Assert.Contains("decompresses", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EntryDeclaringZeroBytesWhileProducingData_IsRefused()
    {
        var hostile = DeflatedZip(("wallet.sqlite", 2L * 1024 * 1024));
        OverwriteDeclaredUncompressedSizes(hostile, 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RgbBackupValidator.ValidateAsync(FromBytes(hostile)));
        Assert.Contains("decompresses to more than", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinarySmallArchiveWithHonestSizes_IsStillAccepted()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var pub = new StreamWriter(zip.CreateEntry("backup.pub_data", CompressionLevel.Optimal).Open()))
                pub.Write("{\"scrypt_params\":{\"log_n\":17,\"r\":8,\"p\":1,\"len\":32}}");
            using var enc = zip.CreateEntry("backup.enc", CompressionLevel.Optimal).Open();
            enc.Write(Encoding.UTF8.GetBytes(new string('x', 4096)));
        }

        await RgbBackupValidator.ValidateAsync(FromBytes(ms.ToArray()));
    }

    [Fact]
    public async Task HonestArchiveInflatingSeveralMegabytesUnderTheLimit_IsStillAcceptedSoRealWalletsRestore()
    {
        var honest = DeflatedZip(("backup.enc", 8L * 1024 * 1024));
        await RgbBackupValidator.ValidateAsync(FromBytes(honest));
    }

    [Fact]
    public async Task HonestArchiveJustUnderTheTotalLimit_IsStillAcceptedSoTheBoundIsNotEffectivelyLower()
    {
        var honest = DeflatedZip(("backup.enc", RgbBackupValidator.MaxTotalUncompressedBytes - 1024));
        await RgbBackupValidator.ValidateAsync(FromBytes(honest));
    }

    [Fact]
    public async Task ZstandardEntryAsWrittenByRgbLibBetaThirty_IsStillAccepted()
    {
        using var ms = new MemoryStream();
        using (var writer = new ZipWriter(ms, new ZipWriterOptions(CompressionType.ZStandard) { LeaveStreamOpen = true }))
        {
            using var pubData = new MemoryStream(
                Encoding.UTF8.GetBytes("{\"scrypt_params\":{\"log_n\":17,\"r\":8,\"p\":1,\"len\":32}}"));
            writer.Write("backup.pub_data", pubData);
            using var payload = new MemoryStream(new byte[512 * 1024]);
            writer.Write("backup.enc", payload);
        }

        await RgbBackupValidator.ValidateAsync(FromBytes(ms.ToArray()));
    }
}
