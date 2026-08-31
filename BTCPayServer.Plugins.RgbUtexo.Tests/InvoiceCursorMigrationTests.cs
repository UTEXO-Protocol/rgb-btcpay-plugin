using BTCPayServer.Plugins.RgbUtexo.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class InvoiceCursorMigrationTests
{
    [Fact]
    public void MigrationIsDiscoverableAndAddsBothDurableCursors()
    {
        var type = typeof(AddRgbInvoiceScanCursors);
        Assert.Equal("20260820120000_AddRgbInvoiceScanCursors",
            type.GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
                .Cast<MigrationAttribute>().Single().Id);

        var columns = new AddRgbInvoiceScanCursors().UpOperations
            .OfType<AddColumnOperation>().Select(o => o.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "DiscoveryAssetPage", "DiscoveryScanCursor", "InvoiceScanCursor" }, columns);
    }
}
