using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

/// <summary>
/// Cross-process exclusion for the isolated native send helper. The parent and worker hold different
/// files so a replacement parent can claim both without a hand-off gap: if it wins before an old worker
/// starts, that worker's exclusive open fails before it constructs an rgb-lib wallet; if the worker wins,
/// recovery remains quarantined until the worker exits.
/// </summary>
internal sealed class RgbNativeSendLease : IDisposable
{
    internal const string ParentFileName = ".send-helper-parent";
    internal const string WorkerFileName = ".send-helper-worker";
    internal const string WalletAccessFileName = ".wallet-native-access";
    internal const string RgbRuntimeLockFileName = "rgb_runtime.lock";

    static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static readonly object ProcessGateRegistry = new();
    static readonly Dictionary<string, ProcessGateEntry> ProcessGates = new(PathComparer);
    static readonly object AccessGateRegistry = new();
    static readonly Dictionary<string, ProcessGateEntry> AccessGates = new(PathComparer);
    static readonly TimeSpan ProcessGateWait = TimeSpan.FromSeconds(30);
    static readonly TimeSpan InProcessAccessWait = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CrossProcessAccessWait = TimeSpan.FromSeconds(5);
    static readonly AsyncLocal<FlowOwnership?> FlowOwner = new();
    static readonly Action<string> RealWorkerFileHardener = HardenWorkerFileCore;
    internal static Action<string> WorkerFileHardener { get; set; } = RealWorkerFileHardener;

    internal static void ResetWorkerFileHardener() => WorkerFileHardener = RealWorkerFileHardener;

    readonly FileStream _first;
    FileStream? _second;
    readonly FlowOwnership? _ownership;
    readonly FlowOwnership? _previousOwnership;
    bool _disposed;

    sealed class FlowOwnership(
        string walletDir, string? workerToken, FileStream? workerLease, FlowOwnership? previous)
    {
        internal readonly string WalletDir = walletDir;
        internal readonly FlowOwnership? Previous = previous;
        internal string? WorkerToken = workerToken;
        internal FileStream? WorkerLease = workerLease;
        internal bool Active = true;
    }

    sealed class WalletAccessLease(FileStream stream, IDisposable gateLease) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { stream.Dispose(); }
            finally { gateLease.Dispose(); }
        }
    }

    sealed class ProcessGateEntry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
    }

    sealed class ProcessGateLease(
        object registry, Dictionary<string, ProcessGateEntry> gates,
        string key, ProcessGateEntry entry) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Gate.Release();
            lock (registry)
            {
                if (--entry.References == 0
                    && gates.TryGetValue(key, out var current)
                    && ReferenceEquals(current, entry))
                    gates.Remove(key);
            }
        }
    }

    RgbNativeSendLease(FileStream first, FileStream? second = null, string? ownedWalletDir = null,
        string? workerToken = null)
    {
        _first = first;
        _second = second;
        if (ownedWalletDir != null)
        {
            _previousOwnership = FlowOwner.Value;
            _ownership = new FlowOwnership(
                ownedWalletDir, workerToken, second, _previousOwnership);
            FlowOwner.Value = _ownership;
        }
    }

    internal static string ParentPathFor(string walletDir) => Path.Combine(walletDir, ParentFileName);
    internal static string WorkerPathFor(string walletDir) => Path.Combine(walletDir, WorkerFileName);
    internal static string WalletAccessPathFor(string walletDir) => Path.Combine(walletDir, WalletAccessFileName);
    internal static string RgbRuntimeLockPathFor(string walletDir) =>
        Path.Combine(walletDir, RgbRuntimeLockFileName);

    // The parent file is a permanent mutex. Only the worker file is the durable "helper may run"
    // marker, so removing that marker while the parent mutex is held closes the release/delete gap.
    internal static bool Exists(string walletDir) => File.Exists(WorkerPathFor(walletDir));

    internal static RgbNativeSendLease AcquireParent(string walletDir)
    {
        return WithProcessGate(walletDir, () =>
        {
            Directory.CreateDirectory(walletDir);
            var parent = OpenExclusive(ParentPathFor(walletDir), FileMode.OpenOrCreate);
            try
            {
                using var access = AcquireWalletAccessCore(walletDir, allowMarked: true);
                var workerToken = EnsureDurableWorkerFile(WorkerPathFor(walletDir));
                return new RgbNativeSendLease(parent, ownedWalletDir: Normalize(walletDir),
                    workerToken: workerToken);
            }
            catch
            {
                parent.Dispose();
                throw;
            }
        });
    }

    internal static RgbNativeSendLease AcquireWorker(string walletDir, string workerToken)
    {
        if (string.IsNullOrWhiteSpace(workerToken))
            throw new InvalidDataException("native send helper has no worker authorization");
        var worker = OpenExclusiveWithRetry(
            WorkerPathFor(walletDir), FileMode.Open, CrossProcessAccessWait);
        try
        {
            VerifyWorkerToken(worker, workerToken);
            return new RgbNativeSendLease(worker);
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    internal static RgbNativeSendLease AcquireRecovery(string walletDir)
    {
        return WithProcessGate(walletDir, () =>
        {
            Directory.CreateDirectory(walletDir);
            var parent = OpenExclusive(ParentPathFor(walletDir), FileMode.OpenOrCreate);
            try
            {
                using var access = AcquireWalletAccessCore(walletDir, allowMarked: true);
                // Keep one ownership interval for journal-only recovery too. CreateNew returns the
                // worker handle atomically, so a delayed orphan helper cannot win between publication
                // and recovery claiming the marker.
                var workerPath = WorkerPathFor(walletDir);
                var worker = Exists(walletDir)
                    ? OpenExclusive(workerPath, FileMode.Open)
                    : CreateDurableWorkerFile(workerPath);
                try
                {
                    HardenWorkerFile(workerPath);
                    return new RgbNativeSendLease(parent, worker, Normalize(walletDir));
                }
                catch
                {
                    worker.Dispose();
                    throw;
                }
            }
            catch
            {
                parent?.Dispose();
                throw;
            }
        });
    }

    // GetOrCreateWalletAsync uses the same gate as AcquireParent so no caller can pass the marker
    // check and publish a new cached handle in the interval before the helper lease becomes visible.
    internal static T WithProcessGate<T>(
        string walletDir, Func<T> operation, TimeSpan? wait = null)
    {
        using var lease = AcquireProcessGate(walletDir, wait ?? ProcessGateWait);
        return operation();
    }

    static IDisposable AcquireProcessGate(string walletDir, TimeSpan wait)
        => AcquireKeyedGate(
            ProcessGateRegistry, ProcessGates, walletDir, wait,
            "another operation owns this RGB wallet — wallet construction remained busy");

    static IDisposable AcquireKeyedGate(
        object registry, Dictionary<string, ProcessGateEntry> gates,
        string walletDir, TimeSpan wait, string timeoutMessage)
    {
        var key = Normalize(walletDir);
        ProcessGateEntry entry;
        lock (registry)
        {
            if (!gates.TryGetValue(key, out entry!))
                gates.Add(key, entry = new ProcessGateEntry());
            entry.References++;
        }

        if (entry.Gate.Wait(wait))
            return new ProcessGateLease(registry, gates, key, entry);

        lock (registry)
        {
            if (--entry.References == 0
                && gates.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
                gates.Remove(key);
        }
        throw new RgbWalletQuarantinedException(timeoutMessage);
    }

    internal static bool IsOwnedByCurrentContext(string walletDir)
        => CurrentOwnershipFor(walletDir) != null;

    static FlowOwnership? CurrentOwnershipFor(string walletDir)
    {
        var key = Normalize(walletDir);
        for (var owner = FlowOwner.Value; owner != null; owner = owner.Previous)
        {
            if (Volatile.Read(ref owner.Active) && PathComparer.Equals(owner.WalletDir, key))
                return owner;
        }
        return null;
    }

    internal static string GetWorkerTokenForCurrentContext(string walletDir)
    {
        var key = Normalize(walletDir);
        for (var owner = FlowOwner.Value; owner != null; owner = owner.Previous)
        {
            if (Volatile.Read(ref owner.Active) && PathComparer.Equals(owner.WalletDir, key))
                return owner.WorkerToken
                    ?? throw new InvalidOperationException("the wallet lease has not authorized a worker");
        }
        throw new InvalidOperationException("the current context does not own this RGB wallet");
    }

    // Recovery normally owns the worker file so no delayed child can enter. For an exact send_end
    // replay it rotates the authorization while holding that file, then releases only the worker
    // handle. The parent mutex and durable marker stay owned for the entire hand-off.
    internal string PrepareWorkerReplay(string walletDir)
    {
        if (_disposed || _second == null || _ownership == null
            || !IsOwnedByCurrentContext(walletDir))
            throw new InvalidOperationException("recovery does not own the worker lease");
        var workerToken = NewWorkerToken();
        HardenWorkerFile(WorkerPathFor(walletDir));
        WriteWorkerToken(_second, workerToken);
        _ownership.WorkerToken = workerToken;
        Volatile.Write(ref _ownership.WorkerLease, null);
        _second.Dispose();
        _second = null;
        return workerToken;
    }

    // Called only after NativeSendProcessRunner has confirmed the replay child reaped (or a
    // pre-launch failure proved there was no child). Failure means some worker can still touch the
    // wallet, so the caller must retain the durable quarantine and its in-process send semaphore.
    internal void ReclaimWorkerAfterReplay(string walletDir)
    {
        if (_disposed || _second != null || _ownership == null
            || !IsOwnedByCurrentContext(walletDir))
            throw new InvalidOperationException("recovery cannot reclaim the worker lease");
        var worker = OpenExclusiveWithRetry(
            WorkerPathFor(walletDir), FileMode.Open, CrossProcessAccessWait);
        try
        {
            VerifyWorkerToken(worker, _ownership.WorkerToken
                ?? throw new InvalidOperationException("recovery worker authorization is missing"));
            // Reclaim can beat a helper whose process started but whose managed handle was never
            // returned to the supervisor. Rotate while holding the file so that delayed process cannot
            // enter after recovery later releases it with the token it received before launch.
            WriteWorkerToken(worker, NewWorkerToken());
            _ownership.WorkerToken = null;
            _second = worker;
            Volatile.Write(ref _ownership.WorkerLease, worker);
        }
        catch
        {
            worker.Dispose();
            throw;
        }
    }

    static string Normalize(string walletDir) => Path.GetFullPath(walletDir);

    // Every in-process native call holds this mutex. Parent/recovery publication takes the same
    // mutex first, so a call either finishes before the marker exists or observes it and fails closed.
    // A helper that already owns WorkerPath, or disposal that first takes this exclusive mutex, may
    // bypass the marker check. All ordinary construction and execution must prove flow ownership.
    internal static IDisposable AcquireWalletAccess(
        string walletDir, bool allowMarked = false, TimeSpan? wait = null) =>
        AcquireWalletAccessCore(walletDir, allowMarked, wait);

    internal static IDisposable AcquireWalletConstructionAccess(
        string walletDir, TimeSpan? wait = null)
    {
        var access = AcquireWalletAccessCore(walletDir, allowMarked: false, wait);
        try
        {
            ReclaimRgbRuntimeLockForConstruction(walletDir);
            return access;
        }
        catch
        {
            access.Dispose();
            throw;
        }
    }

    static void ReclaimRgbRuntimeLockForConstruction(string walletDir)
    {
        var runtimeLock = RgbRuntimeLockPathFor(walletDir);
        if (!File.Exists(runtimeLock)) return;

        if (Exists(walletDir))
        {
            var ownership = CurrentOwnershipFor(walletDir)
                ?? throw new RgbWalletQuarantinedException(
                    "native send helper may still own this wallet — refusing runtime lock reclamation");
            var heldWorker = Volatile.Read(ref ownership.WorkerLease);
            if (heldWorker != null)
            {
                WriteWorkerToken(heldWorker, NewWorkerToken());
                ownership.WorkerToken = null;
            }
            else
            {
                using var worker = OpenExclusiveWithRetry(
                    WorkerPathFor(walletDir), FileMode.Open, CrossProcessAccessWait);
                VerifyWorkerToken(worker, ownership.WorkerToken
                    ?? throw new InvalidOperationException(
                        "the wallet lease has no worker authorization to rotate"));
                var replacementToken = NewWorkerToken();
                WriteWorkerToken(worker, replacementToken);
                ownership.WorkerToken = replacementToken;
            }
        }

        File.Delete(runtimeLock);
        FlushDirectory(walletDir);
    }

    static IDisposable AcquireWalletAccessCore(
        string walletDir, bool allowMarked, TimeSpan? wait = null)
    {
        Directory.CreateDirectory(walletDir);
        var gateLease = AcquireKeyedGate(
            AccessGateRegistry, AccessGates, walletDir, wait ?? InProcessAccessWait,
            "another operation owns this RGB wallet — native access remained busy");
        FileStream access;
        try
        {
            access = OpenExclusiveWithRetry(WalletAccessPathFor(walletDir), FileMode.OpenOrCreate,
                CrossProcessAccessWait);
        }
        catch (IOException ex) when (!allowMarked)
        {
            gateLease.Dispose();
            throw new RgbWalletQuarantinedException(
                "another process owns this RGB wallet — native access remained busy", ex);
        }
        catch
        {
            gateLease.Dispose();
            throw;
        }
        try
        {
            if (!allowMarked && Exists(walletDir) && !IsOwnedByCurrentContext(walletDir))
                throw new RgbWalletQuarantinedException(
                    "another operation owns this RGB wallet — refusing concurrent native access");
            return new WalletAccessLease(access, gateLease);
        }
        catch
        {
            access.Dispose();
            gateLease.Dispose();
            throw;
        }
    }

    internal static void Delete(string walletDir)
    {
        var worker = WorkerPathFor(walletDir);
        if (!File.Exists(worker)) return;
        File.Delete(worker);
        FlushDirectory(walletDir);
    }

    // Recovery also owns the worker file. Close that handle before deletion for Windows, while the
    // parent mutex remains held so no replacement recovery or send can enter the transition.
    internal void ClearActiveMarker(string walletDir)
    {
        if (_ownership != null)
            Volatile.Write(ref _ownership.WorkerLease, null);
        _second?.Dispose();
        _second = null;
        Delete(walletDir);
    }

    static FileStream OpenExclusive(string path, FileMode mode)
    {
        var stream = new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
        return stream;
    }

    static FileStream OpenExclusiveWithRetry(string path, FileMode mode, TimeSpan wait)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return OpenExclusive(path, mode); }
            catch (IOException) when (clock.Elapsed < wait) { Thread.Sleep(25); }
        }
    }

    static string EnsureDurableWorkerFile(string path)
    {
        var workerToken = NewWorkerToken();
        var stream = OpenExclusive(path, FileMode.CreateNew);
        try
        {
            HardenWorkerFile(path);
            WriteWorkerToken(stream, workerToken);
            FlushDirectory(Path.GetDirectoryName(path)!);
            stream.Dispose();
            return workerToken;
        }
        catch
        {
            stream.Dispose();
            RollBackNewWorkerFile(path);
            throw;
        }
    }

    static FileStream CreateDurableWorkerFile(string path)
    {
        var stream = OpenExclusive(path, FileMode.CreateNew);
        try
        {
            HardenWorkerFile(path);
            FlushDirectory(Path.GetDirectoryName(path)!);
            return stream;
        }
        catch
        {
            stream.Dispose();
            RollBackNewWorkerFile(path);
            throw;
        }
    }

    static void HardenWorkerFile(string path)
        => WorkerFileHardener(path);

    static void HardenWorkerFileCore(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode required = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, required);
        var actual = File.GetUnixFileMode(path);
        var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((actual & required) != required || (actual & forbidden) != 0)
            throw new IOException("Native-send worker authorization file permissions are not private");
    }

    static void RollBackNewWorkerFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { FlushDirectory(Path.GetDirectoryName(path)!); } catch { }
    }

    static string NewWorkerToken() => RandomNumberGenerator.GetHexString(32);

    static void WriteWorkerToken(FileStream stream, string workerToken)
    {
        var bytes = Encoding.ASCII.GetBytes(workerToken);
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    static void VerifyWorkerToken(FileStream stream, string expectedToken)
    {
        var expected = Encoding.ASCII.GetBytes(expectedToken);
        if (stream.Length != expected.Length)
            throw new InvalidDataException("native send worker authorization is stale");
        var actual = new byte[expected.Length];
        stream.Position = 0;
        stream.ReadExactly(actual);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("native send worker authorization is stale");
    }

    static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0)
            throw NativeIo("open native-send lease directory");
        try
        {
            if (Fsync(descriptor) != 0)
                throw NativeIo("fsync native-send lease directory");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Revoke execution-context ownership before releasing either cross-process handle. A captured
        // continuation must never observe authorization during the close interval.
        if (_ownership != null)
            Volatile.Write(ref _ownership.Active, false);
        if (_ownership != null)
            Volatile.Write(ref _ownership.WorkerLease, null);
        _second?.Dispose();
        _second = null;
        _first.Dispose();
        if (_ownership != null)
        {
            if (ReferenceEquals(FlowOwner.Value, _ownership))
                FlowOwner.Value = _previousOwnership;
        }
    }
}
