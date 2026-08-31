using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class DurableInvoiceScanTests
{
    [Fact]
    public void ImmortalPrefixCannotStarveTheTail()
    {
        const int pageSize = 16;
        var durable = Enumerable.Range(0, 200).Select(i => $"invoice-{i:D4}").ToList();
        var seen = new HashSet<string>();
        string? cursor = null;

        for (var sweep = 0; sweep < 20; sweep++)
        {
            var page = durable.Where(id => cursor == null || string.CompareOrdinal(id, cursor) > 0)
                .OrderBy(id => id, StringComparer.Ordinal).Take(pageSize).ToList();
            foreach (var id in page) seen.Add(id);
            cursor = DurableInvoiceScan.NextCursor(page, pageSize);
        }

        Assert.Equal(durable.Count, seen.Count);
        Assert.Contains(durable[^1], seen);
    }

    [Fact]
    public void CursorWrapsAndNewWorkBeforeTheCursorIsEventuallyVisited()
    {
        const int pageSize = 4;
        var durable = new List<string> { "b", "c", "d", "e", "f", "g" };
        var seen = new HashSet<string>();
        string? cursor = null;

        for (var sweep = 0; sweep < 5; sweep++)
        {
            if (sweep == 1) durable.Add("a");
            var page = durable.Where(id => cursor == null || string.CompareOrdinal(id, cursor) > 0)
                .OrderBy(id => id, StringComparer.Ordinal).Take(pageSize).ToList();
            foreach (var id in page) seen.Add(id);
            cursor = DurableInvoiceScan.NextCursor(page, pageSize);
        }

        Assert.Contains("a", seen);
        Assert.Contains("g", seen);
    }

    [Fact]
    public void RotatingAssetPagesAreBoundedAndVisitTheTail()
    {
        var assets = Enumerable.Range(0, 101).Select(i => $"asset-{i:D3}").ToList();
        var seen = new HashSet<string>();
        for (var epoch = 0; epoch < 4; epoch++)
        {
            var page = DurableInvoiceScan.RotatingPage(assets, x => x, 32, epoch);
            Assert.InRange(page.Count, 1, 32);
            foreach (var asset in page) seen.Add(asset);
        }

        Assert.Equal(assets.Count, seen.Count);
        Assert.Contains(assets[^1], seen);
    }
}
