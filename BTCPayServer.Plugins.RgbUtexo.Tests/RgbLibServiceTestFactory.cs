using BTCPayServer.Plugins.RgbUtexo.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The production constructor resolves RgbLib.CResultString and binds the real rgblib_string_free /
// Marshal.PtrToStringUTF8, which is exactly what these tests replace.
internal static class RgbLibServiceTestFactory
{
    internal static RgbLibService Create(Type cResultStringType, Action<IntPtr> stringFree,
        Func<IntPtr, string?> marshal) =>
        new(new RGBConfiguration(), null!, NullLogger<RgbLibService>.Instance,
            cResultStringType, stringFree, marshal);
}
