namespace BTCPayServer.Plugins.RgbUtexo.Services;

internal static class DurableInvoiceScan
{
    internal static string? NextCursor(IReadOnlyList<string> pageIds, int pageSize)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return pageIds.Count == pageSize ? pageIds[^1] : null;
    }

    internal static IReadOnlyList<T> RotatingPage<T>(
        List<T> source, Func<T, string> key, int pageSize, long epoch)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
        source.Sort((left, right) => StringComparer.Ordinal.Compare(key(left), key(right)));
        if (source.Count <= pageSize) return source;
        var pageCount = (source.Count + pageSize - 1) / pageSize;
        var page = (int)((ulong)epoch % (ulong)pageCount);
        return source.GetRange(page * pageSize, Math.Min(pageSize, source.Count - page * pageSize));
    }
}
