using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RestoreWatchdogTests
{
    static RestoreLimits Limits() => new(
        Timeout: TimeSpan.FromSeconds(30),
        DiskCapBytes: 52_428_800,
        RamCapBytes: 536_870_912,
        CpuLimit: TimeSpan.FromSeconds(30),
        Poll: TimeSpan.FromMilliseconds(500),
        ReapGrace: TimeSpan.FromSeconds(5));

    [Fact]
    public void UnderBothCaps_NoKill()
    {
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 1024, Limits()));
    }

    [Fact]
    public void OverDiskCap_KillsDisk()
    {
        Assert.Equal(RestoreKillReason.Disk,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 52_428_801, rssBytes: 1024, Limits()));
    }

    [Fact]
    public void OverRamCap_KillsRam()
    {
        Assert.Equal(RestoreKillReason.Ram,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 1024, rssBytes: 536_870_913, Limits()));
    }

    [Fact]
    public void DiskTakesPrecedenceWhenBothOver()
    {
        Assert.Equal(RestoreKillReason.Disk,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 99_000_000, rssBytes: 999_000_000, Limits()));
    }

    [Fact]
    public void AtCapExactly_NoKill()
    {
        Assert.Equal(RestoreKillReason.None,
            RestoreWatchdog.ShouldKill(dirSizeBytes: 52_428_800, rssBytes: 536_870_912, Limits()));
    }
}
