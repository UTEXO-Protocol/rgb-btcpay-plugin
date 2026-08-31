using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Covers the watchdog's own cost, which the original design left unbounded: each poll walked the whole
// staging tree with a stat per file, so an archive that inflated to very many small files stayed under
// the byte cap while making the PARENT process's scan the expensive part — work the child's
// prlimit --cpu cannot reach, and which also pushed the kill decision out past the deadline.
public class RestoreStagingBoundTests
{
    [Fact]
    public void MeasureStaging_SumsASmallTreeExactly()
    {
        using var dir = new TempDir();
        dir.WriteFile("a.dat", 100);
        dir.WriteFile("sub/b.dat", 250);

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, byteCap: 1_000_000, entryCap: 1_000);

        Assert.Equal(350, usage.Bytes);
        // Three entries, not two: a.dat, the `sub` directory, and sub/b.dat. Directories count because
        // they cost the same walk and delete that the entry cap exists to bound.
        Assert.Equal(3, usage.Entries);
    }

    [Fact]
    public void MeasureStaging_StopsEarlyOnceTheByteCapIsExceeded()
    {
        using var dir = new TempDir();
        for (var i = 0; i < 40; i++) dir.WriteFile($"f{i}.dat", 100);

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, byteCap: 250, entryCap: 1_000);

        // Short-circuited rather than totalled: the watchdog only needs to know a cap is exceeded.
        Assert.True(usage.Bytes > 250);
        Assert.True(usage.Entries < 40, $"scan visited {usage.Entries} of 40 entries; it should have stopped early");
    }

    [Fact]
    public void MeasureStaging_StopsEarlyOnceTheEntryCapIsExceeded()
    {
        using var dir = new TempDir();
        // Many tiny files: the shape that defeats a byte-only bound. Total is 200 bytes, far under any
        // realistic disk cap, yet the scan must still refuse to walk the whole tree.
        for (var i = 0; i < 200; i++) dir.WriteFile($"f{i}.dat", 1);

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, byteCap: 52_428_800, entryCap: 10);

        Assert.Equal(11, usage.Entries);
        Assert.True(usage.Bytes < 100);
    }

    [Fact]
    public void MeasureStaging_ReturnedValueStillTripsTheKillComparison()
    {
        // The early return must hand back a value that ShouldKill still judges over the cap; a scan
        // that stopped and reported a value at-or-under the cap would silently disable the kill.
        using var dir = new TempDir();
        for (var i = 0; i < 200; i++) dir.WriteFile($"f{i}.dat", 1);
        var limits = Limits(entryCap: 10);

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, limits.DiskCapBytes, limits.MaxStagingEntries);

        Assert.Equal(RestoreKillReason.Entries,
            RestoreWatchdog.ShouldKill(usage.Bytes, rssBytes: 1024, limits, usage.Entries));
    }

    [Fact]
    public void MeasureStaging_OnAMissingDirectoryReportsNothingRatherThanThrowing()
    {
        // The staging dir may not exist yet on the first poll; the watchdog must not fail the restore
        // for that, and must not report phantom usage either.
        var usage = RestoreProcessRunner.MeasureStaging(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), 1_000, 1_000);

        Assert.Equal(0, usage.Bytes);
        Assert.Equal(0, usage.Entries);
    }

    [Fact]
    public void MeasureStaging_CountsDirectoriesTowardTheEntryCap()
    {
        // Round 2 found this: counting only FILES let an output made of empty directories sit at
        // entries = 0, bytes = 0 and never trip the cap, while still costing a stat per directory on
        // every poll and a full recursive delete afterwards — the exact parent-side cost the bound
        // exists to remove. Directories are entries.
        using var dir = new TempDir();
        for (var i = 0; i < 200; i++) dir.MakeDir($"d{i}");

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, byteCap: 52_428_800, entryCap: 10);

        Assert.Equal(11, usage.Entries);
        Assert.Equal(0, usage.Bytes);
        Assert.Equal(RestoreKillReason.Entries,
            RestoreWatchdog.ShouldKill(usage.Bytes, rssBytes: 1024, Limits(entryCap: 10), usage.Entries));
    }

    [Fact]
    public void MeasureStaging_StillSumsFileBytesWhenDirectoriesArePresent()
    {
        // Guards the obvious way to get the above wrong: counting every entry but no longer adding up
        // file sizes would silently disable the disk cap.
        using var dir = new TempDir();
        dir.MakeDir("sub");
        dir.WriteFile("sub/a.dat", 500);

        var usage = RestoreProcessRunner.MeasureStaging(dir.Path, byteCap: 1_000_000, entryCap: 1_000);

        Assert.Equal(500, usage.Bytes);
        Assert.Equal(2, usage.Entries);
    }

    [Fact]
    public void ShouldKill_EntriesOverCapKillsForEntries()
    {
        Assert.Equal(RestoreKillReason.Entries,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 1024, Limits(entryCap: 10), stagingEntries: 11));
    }

    [Fact]
    public void ShouldKill_EntriesAtTheCapDoesNotKill()
    {
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 1024, Limits(entryCap: 10), stagingEntries: 10));
    }

    [Fact]
    public void ShouldKill_OmittingEntriesPreservesTheTwoMetricBehaviour()
    {
        // The parameter carries a default so existing call sites keep compiling; that default must be
        // inert rather than an accidental bound of zero.
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 1024, Limits(entryCap: 0)));
    }

    [Fact]
    public void DiskAndRamStillOutrankEntries()
    {
        var limits = Limits(entryCap: 1);
        Assert.Equal(RestoreKillReason.Disk,
            RestoreWatchdog.ShouldKill(52_428_801, 1024, limits, stagingEntries: 500));
        Assert.Equal(RestoreKillReason.Ram,
            RestoreWatchdog.ShouldKill(1024, 536_870_913, limits, stagingEntries: 500));
    }

    static RestoreLimits Limits(int entryCap) => new(
        Timeout: TimeSpan.FromSeconds(30),
        DiskCapBytes: 52_428_800,
        RamCapBytes: 536_870_912,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(500),
        ReapGrace: TimeSpan.FromSeconds(5),
        MaxStagingEntries: entryCap);

    sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"staging-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

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
