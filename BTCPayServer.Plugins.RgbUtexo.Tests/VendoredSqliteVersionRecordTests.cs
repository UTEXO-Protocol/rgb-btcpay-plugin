namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class VendoredSqliteVersionRecordTests
{
    const string ShippedRgbLibNativeRid = "linux-x64";

    const string ShippedRgbLibNativeFileName = "librgblibcffi.so";

    const string RecordedSqliteVersionVendoredInTheShippedRgbLibNative = "3.46.0";

    const string RecordedSqliteSourceIdVendoredInTheShippedRgbLibNative =
        "2024-05-23 13:25:27 96c92aba00c8375bc32fafcdf12429c58bd8aabfcadab6683e35bbb9cdebf19e";

    const string WhyThisIsARecordAndNotAVerdictOnCve20256965FixedInSqlite3502 =
        "This is a record of the SQLite build that was assessed, not a verdict on it. The recorded build "
        + "predates the CVE-2025-6965 fix, which shipped in SQLite 3.50.2, and it was measured compiled "
        + "into the prebuilt RgbLib native with no exported sqlite3_ symbols, so it cannot be replaced or "
        + "patched from this repository. The only remediation route is upstream: bump libsqlite3-sys in "
        + "UTEXO-Protocol/rgb-lib, republish RgbLib, raise the RgbLib version in "
        + "BTCPayServer.Plugins.RgbUtexo.csproj and packages.lock.json, then re-read this advisory against "
        + "the new engine and update the recorded values above in the same change.";

    [Fact]
    public void ShippedRgbLibNativeStillVendorsTheRecordedSqliteBuild()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", ShippedRgbLibNativeRid, "native",
            ShippedRgbLibNativeFileName);

        Assert.True(File.Exists(path),
            $"unverified: the {ShippedRgbLibNativeRid} RgbLib native is not staged at {path}, so nothing here "
            + "is checked. Restore the RgbLib package and rebuild the test project. "
            + WhyThisIsARecordAndNotAVerdictOnCve20256965FixedInSqlite3502);

        var image = File.ReadAllBytes(path);

        var sourceIds = NulDelimitedAsciiTokens(image, LooksLikeSqliteSourceId);
        Assert.True(
            sourceIds.Count == 1
            && sourceIds.Contains(RecordedSqliteSourceIdVendoredInTheShippedRgbLibNative),
            $"the SQLite source ids found in {ShippedRgbLibNativeRid}/{ShippedRgbLibNativeFileName} are "
            + $"[{string.Join("] [", sourceIds)}]; the recorded one is "
            + $"[{RecordedSqliteSourceIdVendoredInTheShippedRgbLibNative}]. "
            + WhyThisIsARecordAndNotAVerdictOnCve20256965FixedInSqlite3502);

        var recordedVersionIsInTheImage = NulDelimitedAsciiTokens(image,
            (bytes, start, length) => TokenEquals(bytes, start, length,
                RecordedSqliteVersionVendoredInTheShippedRgbLibNative)).Count == 1;
        Assert.True(recordedVersionIsInTheImage,
            $"{ShippedRgbLibNativeRid}/{ShippedRgbLibNativeFileName} no longer carries the standalone string "
            + $"'{RecordedSqliteVersionVendoredInTheShippedRgbLibNative}', which is the SQLite version this "
            + "record names and the value every write-up of this finding quotes. "
            + WhyThisIsARecordAndNotAVerdictOnCve20256965FixedInSqlite3502);
    }

    static SortedSet<string> NulDelimitedAsciiTokens(byte[] image, Func<byte[], int, int, bool> matches)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= image.Length; i++)
        {
            if (i < image.Length && image[i] >= 0x20 && image[i] <= 0x7E)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0 && matches(image, start, i - start))
                found.Add(System.Text.Encoding.ASCII.GetString(image, start, i - start));
            start = -1;
        }

        return found;
    }

    static bool LooksLikeSqliteSourceId(byte[] image, int start, int length)
    {
        if (length < 60 || length > 96)
            return false;
        if (image[start + 4] != (byte)'-' || image[start + 7] != (byte)'-' || image[start + 10] != (byte)' ')
            return false;
        if (image[start + 13] != (byte)':' || image[start + 16] != (byte)':' || image[start + 19] != (byte)' ')
            return false;

        for (var i = 0; i < 19; i++)
        {
            if (i is 4 or 7 or 10 or 13 or 16)
                continue;
            if (image[start + i] < (byte)'0' || image[start + i] > (byte)'9')
                return false;
        }

        for (var i = start + 20; i < start + length; i++)
        {
            var b = image[i];
            var isLowerHex = (b >= (byte)'0' && b <= (byte)'9') || (b >= (byte)'a' && b <= (byte)'f');
            if (!isLowerHex)
                return false;
        }

        return true;
    }

    static bool TokenEquals(byte[] image, int start, int length, string token)
    {
        if (length != token.Length)
            return false;
        for (var i = 0; i < length; i++)
        {
            if (image[start + i] != (byte)token[i])
                return false;
        }

        return true;
    }
}
