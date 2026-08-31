using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal enum RgbSendRecoveryPhase
{
    Staged,
    SendEndIndeterminate
}

internal sealed record RgbSendRecoveryRecord(
    RgbSendRecoveryPhase Phase,
    int? BatchTransferIdx = null,
    string? RawTransaction = null,
    string? TransactionId = null,
    string? SignedPsbt = null)
{
    internal bool HasExactTransactionRecovery =>
        Phase == RgbSendRecoveryPhase.SendEndIndeterminate
        && BatchTransferIdx is > 0
        && !string.IsNullOrWhiteSpace(RawTransaction)
        && !string.IsNullOrWhiteSpace(TransactionId);

    internal bool HasSendEndReplay =>
        HasExactTransactionRecovery && !string.IsNullOrWhiteSpace(SignedPsbt);
}

internal static class RgbSendRecoveryJournal
{
    internal const string FileName = ".send-recovery";
    internal const string TransferFasciaFileName = "fascia";
    internal const string TransferSignedPsbtFileName = "signed.psbt";
    internal const int MaxBytes = 1_048_576;

    internal static string PathFor(string walletDataDir, string masterFingerprint) =>
        Path.Combine(walletDataDir, masterFingerprint, FileName);

    internal static RgbSendRecoveryRecord? Read(string path)
    {
        if (!File.Exists(path))
            return null;

        if (new FileInfo(path).Length > MaxBytes)
            throw new InvalidDataException("RGB send recovery journal exceeds its size bound");
        var value = File.ReadAllText(path, Encoding.UTF8).Trim();
        // Compatibility with phase-only journals written by the prior release. An indeterminate
        // legacy journal remains fail-closed unless the exact transaction can be recovered elsewhere.
        if (value is "staged" or "send-end-indeterminate")
            return new RgbSendRecoveryRecord(value == "staged"
                ? RgbSendRecoveryPhase.Staged
                : RgbSendRecoveryPhase.SendEndIndeterminate);

        RgbSendRecoveryRecord? record;
        try { record = JsonSerializer.Deserialize<RgbSendRecoveryRecord>(value); }
        catch (JsonException ex) { throw new InvalidDataException("Malformed RGB send recovery journal", ex); }
        if (record == null || !Enum.IsDefined(record.Phase))
            throw new InvalidDataException("RGB send recovery journal has an invalid phase");
        if (record.Phase == RgbSendRecoveryPhase.SendEndIndeterminate
            && (record.BatchTransferIdx is <= 0
                || string.IsNullOrWhiteSpace(record.RawTransaction)
                || string.IsNullOrWhiteSpace(record.TransactionId)))
        {
            throw new InvalidDataException("RGB send recovery journal lacks exact transaction recovery data");
        }
        return record;
    }

    internal static bool IsUnparseable(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            Read(path);
            return false;
        }
        catch (InvalidDataException) { return true; }
    }

    internal static void Write(string path, RgbSendRecoveryPhase phase)
        => Write(path, new RgbSendRecoveryRecord(phase));

    internal static void WriteSendEnd(
        string path, int batchTransferIdx, string rawTransaction, string transactionId,
        string signedPsbt)
        => Write(path, new RgbSendRecoveryRecord(
            RgbSendRecoveryPhase.SendEndIndeterminate,
            batchTransferIdx,
            rawTransaction,
            transactionId,
            signedPsbt));

    static void Write(string path, RgbSendRecoveryRecord record)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Recovery journal has no parent directory");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            if (bytes.Length > MaxBytes)
                throw new InvalidDataException("RGB send recovery journal exceeds its size bound");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                // The journal contains a signed PSBT. Harden the newly-created inode before writing
                // any bytes so a permissive process umask cannot create a disclosure window.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            using var committed = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read, 1, FileOptions.WriteThrough);
            committed.Flush(flushToDisk: true);
            FlushDirectory(directory);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal static void Delete(string path)
    {
        if (!File.Exists(path))
            return;
        File.Delete(path);
        FlushDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Recovery journal has no parent directory"));
    }

    // beta.30 writes the fascia before send_end but later needs it to perform the ACK-gated
    // broadcast. Make that artifact and its directory entries durable before send_end can commit
    // status 1; otherwise a power loss can preserve SQLite while losing the only broadcast fascia.
    internal static void FsyncPreSendEndArtifacts(string walletDir, string transactionId)
    {
        var transferDir = ResolveTransferDir(walletDir, transactionId);
        var transfersDir = Path.GetDirectoryName(transferDir)!;
        FsyncRequiredFile(Path.Combine(transferDir, TransferFasciaFileName), "RGB transfer fascia");
        FlushDirectory(transferDir);
        FlushDirectory(transfersDir);
        FlushDirectory(walletDir);
    }

    // The exact journal is the durable source of the signed PSBT. Re-publish beta.30's copy
    // atomically, then fsync both files it reads after the recipient ACK before status 1 is accepted.
    internal static void RestoreAndFsyncAckBroadcastArtifacts(
        string walletDir, string transactionId, string signedPsbt)
    {
        FsyncPreSendEndArtifacts(walletDir, transactionId);
        var transferDir = ResolveTransferDir(walletDir, transactionId);
        var signedPath = Path.Combine(transferDir, TransferSignedPsbtFileName);
        var temporary = Path.Combine(transferDir, $".{TransferSignedPsbtFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(signedPsbt);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                throw new InvalidDataException("RGB signed PSBT recovery artifact has an invalid size");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, signedPath, overwrite: true);
            FsyncRequiredFile(signedPath, "RGB signed PSBT");
            FlushDirectory(transferDir);
            FlushDirectory(Path.GetDirectoryName(transferDir)!);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    static string ResolveTransferDir(string walletDir, string transactionId)
    {
        if (transactionId.Length != 64 || transactionId.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("RGB recovery transaction id is invalid");
        var transfersDir = Path.Combine(walletDir,
            RgbWalletDirectoryReservedNames.PinnedRgbLibBeta30TransfersDirectoryNameReReadTransfersDirWhenBumpingRgbLib);
        var transferDir = Path.Combine(transfersDir, transactionId);
        if (!Directory.Exists(transferDir))
            throw new DirectoryNotFoundException(
                $"RGB transfer artifact directory not found: {transferDir}");
        return transferDir;
    }

    static void FsyncRequiredFile(string path, string description)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new FileNotFoundException($"{description} is missing or empty", path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    // Flushing the renamed file does not make its directory entry durable on Unix. Without this
    // barrier, power loss after send_end starts can resurrect the old phase-only record or lose the
    // exact PSBT record even though every file Flush(true) returned successfully.
    internal static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0)
            throw NativeIo("open recovery-journal directory");
        try
        {
            if (Fsync(descriptor) != 0)
                throw NativeIo("fsync recovery-journal directory");
        }
        finally { _ = Close(descriptor); }
    }

    static IOException NativeIo(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException($"Failed to {operation}", new Win32Exception(error));
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    static extern int Close(int descriptor);
}
