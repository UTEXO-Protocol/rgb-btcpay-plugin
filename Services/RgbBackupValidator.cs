using System.IO.Compression;
using System.Linq;
using Microsoft.AspNetCore.Http;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbBackupValidator
{
    public const long MaxEntryUncompressedBytes = 50 * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 50 * 1024 * 1024;
    public const int MaxEntryCount = 1000;

    const int MeasurementBufferBytes = 81920;

    public static async Task ValidateAsync(IFormFile file, CancellationToken ct = default)
    {
        using var memStream = new MemoryStream();
        using (var input = file.OpenReadStream())
            await input.CopyToAsync(memStream, ct);

        ValidateBytes(memStream);
    }

    internal static void ValidateBytes(MemoryStream memStream)
    {
        if (memStream.Length < 4)
            throw new InvalidOperationException("Backup file too small");

        var header = memStream.GetBuffer();
        if (header[0] != 'P' || header[1] != 'K' || header[2] != 0x03 || header[3] != 0x04)
            throw new InvalidOperationException("Invalid backup file — expected ZIP archive (rgb-lib backup format)");

        memStream.Position = 0;
        try
        {
            using var zip = new ZipArchive(memStream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count == 0)
                throw new InvalidOperationException("Backup archive is empty");
            if (zip.Entries.Count > MaxEntryCount)
                throw new InvalidOperationException("Backup archive contains too many entries");

            long totalUncompressed = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Backup archive contains path traversal entry");
                if (Path.IsPathRooted(entry.FullName) || entry.FullName.StartsWith("/", StringComparison.Ordinal)
                    || entry.FullName.StartsWith("\\", StringComparison.Ordinal))
                    throw new InvalidOperationException("Backup archive contains absolute path entry");
                if (entry.Length > MaxEntryUncompressedBytes)
                    throw new InvalidOperationException(
                        $"Backup entry '{entry.FullName}' declared uncompressed size ({entry.Length / 1024 / 1024}MB) exceeds limit");
                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxTotalUncompressedBytes)
                    throw new InvalidOperationException(
                        $"Backup total declared uncompressed size exceeds {MaxTotalUncompressedBytes / 1024 / 1024}MB limit (ZIP bomb protection)");
            }
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive");
        }

        RefuseIfEntriesProduceMoreBytesThanTheyDeclareOrThanTheLimitAllows(memStream);
    }

    static void RefuseIfEntriesProduceMoreBytesThanTheyDeclareOrThanTheLimitAllows(MemoryStream memStream)
    {
        memStream.Position = 0;
        var options = new ReaderOptions
        {
            LeaveStreamOpen = true,
            Providers = RgbBackupScryptGuard.BoundedProviders
        };

        IArchive archive;
        try
        {
            archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(memStream, options);
        }
        catch (Exception ex) when (ex is ArchiveException or InvalidDataException)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive", ex);
        }

        using var _ = archive;
        List<IArchiveEntry> entries;
        try
        {
            entries = archive.Entries.Cast<IArchiveEntry>().Take(MaxEntryCount + 1).ToList();
        }
        catch (Exception ex) when (ex is ArchiveException or InvalidDataException)
        {
            throw new InvalidOperationException("Backup file is not a valid ZIP archive", ex);
        }
        if (entries.Count > MaxEntryCount)
            throw new InvalidOperationException("Backup archive contains too many entries");

        var buffer = new byte[MeasurementBufferBytes];
        long measuredTotal = 0;
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
                continue;

            var name = entry.Key ?? string.Empty;
            if (entry.CompressionType is not (CompressionType.None or CompressionType.Deflate or CompressionType.ZStandard))
                throw new InvalidOperationException(
                    $"Backup entry '{name}' uses unsupported compression {entry.CompressionType}. Refusing to restore.");

            var declaredBytes = Math.Max(entry.Size, 0);
            var remainingTotalBudget = MaxTotalUncompressedBytes - measuredTotal;
            var stopReadingAfter = Math.Min(Math.Min(declaredBytes, MaxEntryUncompressedBytes), remainingTotalBudget) + 1;
            var measuredBytes = CountBytesTheEntryActuallyProduces(entry, buffer, stopReadingAfter, name);

            if (measuredBytes > declaredBytes)
                throw new InvalidOperationException(
                    $"Backup entry '{name}' decompresses to more than the {declaredBytes} bytes it declares, so the "
                    + "size recorded in the archive cannot be trusted. This plugin decompresses at most "
                    + $"{MaxTotalUncompressedBytes / 1024 / 1024}MB in total and "
                    + $"{MaxEntryUncompressedBytes / 1024 / 1024}MB per entry (ZIP bomb protection).");
            if (measuredBytes > MaxEntryUncompressedBytes)
                throw new InvalidOperationException(
                    $"Backup entry '{name}' decompresses past the {MaxEntryUncompressedBytes / 1024 / 1024}MB "
                    + "per-entry limit when actually decompressed (ZIP bomb protection)");
            if (measuredBytes > remainingTotalBudget)
                throw new InvalidOperationException(
                    "Backup archive decompresses past the "
                    + $"{MaxTotalUncompressedBytes / 1024 / 1024}MB total limit when actually decompressed "
                    + "(ZIP bomb protection)");

            measuredTotal += measuredBytes;
        }
    }

    static long CountBytesTheEntryActuallyProduces(IArchiveEntry entry, byte[] buffer, long stopReadingAfter, string name)
    {
        Stream? stream = null;
        long produced = 0;
        try
        {
            stream = entry.OpenEntryStream();
            while (produced < stopReadingAfter)
            {
                var wanted = (int)Math.Min(buffer.Length, stopReadingAfter - produced);
                var read = stream.Read(buffer, 0, wanted);
                if (read == 0)
                    break;
                produced += read;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Backup entry '{name}' could not be decompressed, so the size it declares cannot be checked "
                + "against the bytes it actually produces. Refusing to restore.", ex);
        }
        finally
        {
            try { stream?.Dispose(); } catch { }
        }
        return produced;
    }
}
