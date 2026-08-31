using System.Security.Cryptography;

namespace BTCPayServer.Plugins.RgbUtexo.PaymentHandler;

public static class RgbPricingCode
{
    const string CurrentPrefix = "RGB2";
    const string LegacyPrefix = "RGB";
    const int CurrentHexChars = 64;
    const int LegacyHexChars = 16;

    public static string For(string assetId)
    {
        var canonicalAssetId = Convert.FromHexString(CanonicalizeAssetId(assetId));
        var digest = SHA256.HashData(canonicalAssetId);
        return string.Create(CurrentPrefix.Length + CurrentHexChars, digest, static (span, bytes) =>
        {
            CurrentPrefix.AsSpan().CopyTo(span);
            for (var i = 0; i < CurrentHexChars / 2; i++)
            {
                var b = bytes[i];
                span[CurrentPrefix.Length + i * 2] = HexDigit(b >> 4);
                span[CurrentPrefix.Length + i * 2 + 1] = HexDigit(b & 0xF);
            }
        });
    }

    // Canonical identity is the 32-byte ContractId payload, not any one BAID64 presentation.
    public static string CanonicalizeAssetId(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("Asset id is required to derive a pricing code", nameof(assetId));

        var value = assetId.Trim();
        if (value.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
            value = value[4..];

        var mnemonic = value.IndexOf('#');
        if (mnemonic >= 0)
            value = value[..mnemonic];
        value = value.Replace("-", "", StringComparison.Ordinal);
        if (value.Length == 0 || value.Contains(':'))
            throw new ArgumentException("Asset id is not a supported RGB contract id", nameof(assetId));

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '~'))
                throw new ArgumentException("Asset id is not a supported RGB contract id", nameof(assetId));
        }

        var standardBase64 = value.Replace('_', '+').Replace('~', '/');
        standardBase64 += new string('=', (4 - standardBase64.Length % 4) % 4);

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(standardBase64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Asset id is not a supported RGB contract id", nameof(assetId), ex);
        }

        if (decoded.Length is not (32 or 36))
            throw new ArgumentException("Asset id is not a 32-byte RGB contract id", nameof(assetId));

        var payload = decoded.AsSpan(0, 32);
        if (decoded.Length == 36)
        {
            var hriKey = SHA256.HashData("rgb"u8);
            Span<byte> checksumInput = stackalloc byte[64];
            hriKey.CopyTo(checksumInput);
            payload.CopyTo(checksumInput[32..]);
            var digest = SHA256.HashData(checksumInput);
            Span<byte> expected = stackalloc byte[] { digest[0], digest[1], digest[1], digest[2] };
            if (!CryptographicOperations.FixedTimeEquals(expected, decoded.AsSpan(32, 4)))
                throw new ArgumentException("Asset id has an invalid embedded checksum", nameof(assetId));
        }

        return Convert.ToHexString(payload);
    }

    public static bool MatchesCurrent(string? pricingCode, string assetId) =>
        IsCurrentPricingCode(pricingCode)
        && string.Equals(pricingCode, For(assetId), StringComparison.OrdinalIgnoreCase);

    public static bool IsPricingCode(string? value) =>
        IsCurrentPricingCode(value) || IsLegacyPricingCode(value);

    public static bool IsCurrentPricingCode(string? value) =>
        HasShape(value, CurrentPrefix, CurrentHexChars);

    public static bool IsLegacyPricingCode(string? value) =>
        HasShape(value, LegacyPrefix, LegacyHexChars);

    static bool HasShape(string? value, string prefix, int hexChars)
    {
        if (value is null || value.Length != prefix.Length + hexChars) return false;
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = prefix.Length; i < value.Length; i++)
        {
            var c = char.ToUpperInvariant(value[i]);
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
        }
        return true;
    }

    static char HexDigit(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
}
