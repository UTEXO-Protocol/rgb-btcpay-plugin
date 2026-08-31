using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RgbRestoreHelper;

internal static class NativeSendResourceLimiter
{
    const uint JobObjectLimitProcessMemory = 0x00000100;
    const uint JobObjectLimitKillOnJobClose = 0x00002000;
    const uint JobObjectLimitProcessTime = 0x00000002;
    const int JobObjectExtendedLimitInformationClass = 9;
    static IntPtr _windowsJob;

    internal static void Apply(long additionalAddressSpaceBytes, int cpuLimitSeconds)
    {
        if (additionalAddressSpaceBytes <= 0)
            throw new InvalidDataException("native send memory limit is invalid");
        if (cpuLimitSeconds <= 0)
            throw new InvalidDataException("native send CPU limit is invalid");

        if (OperatingSystem.IsWindows())
        {
            ApplyWindowsJobLimit((ulong)additionalAddressSpaceBytes, cpuLimitSeconds);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            ApplyUnixAddressSpaceLimit((ulong)additionalAddressSpaceBytes);
            ApplyUnixCpuLimit((ulong)cpuLimitSeconds);
            return;
        }

        throw new PlatformNotSupportedException(
            "native send requires a hard process memory limit on this platform");
    }

    internal static ulong ComputeUnixAddressSpaceLimit(ulong currentVirtualBytes, ulong budgetBytes,
        ulong existingSoftLimit, ulong existingHardLimit)
    {
        var requested = ulong.MaxValue - currentVirtualBytes < budgetBytes
            ? ulong.MaxValue
            : currentVirtualBytes + budgetBytes;
        if (existingSoftLimit != ulong.MaxValue)
            requested = Math.Min(requested, existingSoftLimit);
        return existingHardLimit == ulong.MaxValue
            ? requested
            : Math.Min(requested, existingHardLimit);
    }

    static void ApplyUnixAddressSpaceLimit(ulong budgetBytes)
    {
        var resource = OperatingSystem.IsMacOS() ? 5 : 9; // RLIMIT_AS on Darwin and Linux.
        if (GetRLimit(resource, out var existing) != 0)
            throw NativeIo("read native send address-space limit");

        var currentVirtual = checked((ulong)Math.Max(
            Process.GetCurrentProcess().VirtualMemorySize64, 0));
        var soft = existing.Current == UIntPtr.MaxValue ? ulong.MaxValue : existing.Current.ToUInt64();
        var hard = existing.Maximum == UIntPtr.MaxValue ? ulong.MaxValue : existing.Maximum.ToUInt64();
        var limit = ComputeUnixAddressSpaceLimit(currentVirtual, budgetBytes, soft, hard);
        if (limit <= currentVirtual)
            throw new InvalidOperationException(
                "native send has no address-space budget under the existing process limit");

        var updated = new RLimit
        {
            Current = (UIntPtr)limit,
            Maximum = existing.Maximum
        };
        if (SetRLimit(resource, ref updated) != 0)
            throw NativeIo("apply native send address-space limit");
    }

    static void ApplyUnixCpuLimit(ulong cpuLimitSeconds)
    {
        const int resource = 0; // RLIMIT_CPU on Darwin and Linux.
        if (GetRLimit(resource, out var existing) != 0)
            throw NativeIo("read native send CPU limit");
        var soft = existing.Current == UIntPtr.MaxValue ? ulong.MaxValue : existing.Current.ToUInt64();
        var hard = existing.Maximum == UIntPtr.MaxValue ? ulong.MaxValue : existing.Maximum.ToUInt64();
        var requested = Math.Min(cpuLimitSeconds, Math.Min(soft, hard));
        if (requested == 0)
            throw new InvalidOperationException("native send has no CPU budget under the existing process limit");
        var updated = new RLimit { Current = (UIntPtr)requested, Maximum = existing.Maximum };
        if (SetRLimit(resource, ref updated) != 0)
            throw NativeIo("apply native send CPU limit");
    }

    static void ApplyWindowsJobLimit(ulong additionalMemoryBytes, int cpuLimitSeconds)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw NativeIo("create native send memory job");
        try
        {
            var currentPrivate = checked((ulong)Math.Max(
                Process.GetCurrentProcess().PrivateMemorySize64, 0));
            var memoryLimit = ulong.MaxValue - currentPrivate < additionalMemoryBytes
                ? ulong.MaxValue
                : currentPrivate + additionalMemoryBytes;
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitProcessMemory | JobObjectLimitKillOnJobClose
                        | JobObjectLimitProcessTime,
                    PerProcessUserTimeLimit = TimeSpan.FromSeconds(cpuLimitSeconds).Ticks
                },
                ProcessMemoryLimit = (UIntPtr)memoryLimit
            };
            var size = (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, size))
                throw NativeIo("apply native send memory job");
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw NativeIo("join native send memory job");
            _windowsJob = job;
            job = IntPtr.Zero;
        }
        finally
        {
            if (job != IntPtr.Zero) _ = CloseHandle(job);
        }
    }

    static IOException NativeIo(string operation) =>
        new($"Failed to {operation}", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    struct RLimit
    {
        internal UIntPtr Current;
        internal UIntPtr Maximum;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    static extern int GetRLimit(int resource, out RLimit limit);

    [DllImport("libc", EntryPoint = "setrlimit", SetLastError = true)]
    static extern int SetRLimit(int resource, ref RLimit limit);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetInformationJobObject(IntPtr job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
