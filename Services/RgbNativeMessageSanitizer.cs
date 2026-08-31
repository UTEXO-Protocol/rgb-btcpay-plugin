using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public static class RgbNativeMessageSanitizer
{
    public const string RedactionPlaceholder = "[redacted-key-material]";

    static readonly Regex ExtendedKeyPattern = new(
        @"\b[a-zA-Z]{1,4}(pub|prv)[1-9A-HJ-NP-Za-km-z]{60,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex KeyOriginPattern = new(
        @"\[[0-9a-fA-F]{8}(/[0-9]{1,10}['h]?)*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Sanitize(string? nativeMessage)
    {
        if (string.IsNullOrWhiteSpace(nativeMessage))
            return "";

        var sanitized = KeyOriginPattern.Replace(nativeMessage, RedactionPlaceholder);
        return ExtendedKeyPattern.Replace(sanitized, RedactionPlaceholder);
    }
}
