using System.Reflection;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class InProcessRestoreRemovedTests
{
    [Fact]
    public void IRgbLibService_HasNoRestoreBackupMember()
    {
        Assert.Null(typeof(IRgbLibService).GetMethod("RestoreBackup"));
    }

    [Fact]
    public void RgbLibService_HasNoRestoreBackupMethod()
    {
        Assert.Null(typeof(RgbLibService).GetMethod("RestoreBackup",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void RgbLibService_HasNoRestoreBackupMethodInfoField()
    {
        var fields = typeof(RgbLibService).GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.Name.Contains("restoreBackup", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(fields);
    }
}
