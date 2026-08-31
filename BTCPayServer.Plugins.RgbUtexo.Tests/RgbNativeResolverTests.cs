using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbNativeResolverTests
{
    // A regression guard: it passes on the commit that introduces it and exists to fail later if the
    // resolver starts answering for another library. Losing the guard leaves the gate working while
    // every rgb-lib entry point disappears — "the plugin is broken" rather than "the native is
    // missing", and a far worse regression than the one this phase diagnoses.
    [Fact]
    public void Resolver_DoesNotHijackOtherNativeLibraries()
    {
        // Unstaged, the resolver returns Zero whether or not the guard exists, so without this the
        // assertion below would pass vacuously.
        StagedNative.Require();

        var handle = RgbVerifyNative.ResolveNative("rgblibcffi", typeof(RgbVerifyNative).Assembly, null);

        Assert.Equal(IntPtr.Zero, handle);
    }

    // Pins that the P/Invoke path works — not that the resolver is what makes it work. Measured,
    // the DllImport still binds with the resolver forced to Zero, because RgbLib's native assets
    // already put runtimes/<rid>/native/ on the default search path; and a test cannot substitute a
    // resolver anyway (SetDllImportResolver throws once one is set). Whether the resolver is
    // load-bearing under BTCPay's plugin ALC is covered only by the live run in the spec.
    [Fact]
    public void RealDllImport_BindsThroughTheStagedNative()
    {
        StagedNative.Require();

        // Reaching the native at all is the assertion: a malformed invoice comes back as an Err
        // surfaced through the typed exception, whereas an unbound import throws DllNotFound.
        Assert.Throws<RgbIntentVerificationException>(
            () => RgbVerifyNative.DecodeInvoice("not-a-valid-rgb-invoice"));
    }
}
