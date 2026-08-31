using System.Runtime.InteropServices;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// Stand-in for RgbLib.CResultString. Field NAMES must match — the production constructor resolves
// them by name — but the type is ours, so no native library is involved.
public struct FakeCResult
{
    public string result;
    public IntPtr inner;
}

public class RgbNativeResultTests
{
    // Real unmanaged memory with a matched allocator pair. A fabricated pointer would make
    // PtrToStringUTF8 undefined behaviour — a memory-safety test must not create a memory-safety bug.
    static (object Boxed, IntPtr Ptr) Result(string status, string payload)
    {
        var ptr = Marshal.StringToCoTaskMemUTF8(payload);
        return (new FakeCResult { result = status, inner = ptr }, ptr);
    }

    static (RgbLibService Svc, List<IntPtr> Freed) Build(Func<IntPtr, string?>? marshal = null)
    {
        var freed = new List<IntPtr>();
        var svc = RgbLibServiceTestFactory.Create(
            typeof(FakeCResult),
            p => { freed.Add(p); Marshal.FreeCoTaskMem(p); },
            marshal ?? Marshal.PtrToStringUTF8);
        return (svc, freed);
    }

    [Fact] // G1-T1
    public void OkResult_IsReadAndFreedExactlyOnce()
    {
        var (svc, freed) = Build();
        var (boxed, ptr) = Result("Ok", "{\"a\":1}");

        var r = svc.ReadNativeResult(boxed);

        Assert.True(r.IsOk);
        Assert.Equal("{\"a\":1}", r.Payload);
        Assert.Equal(new[] { ptr }, freed);
    }

    [Fact] // G1-T2 — the double-free this whole change exists to prevent
    public void ReadingTheSameResultTwice_FreesOnlyOnce()
    {
        var (svc, freed) = Build();
        var (boxed, ptr) = Result("Ok", "payload");

        var first = svc.ReadNativeResult(boxed);
        var second = svc.ReadNativeResult(boxed);

        Assert.True(first.IsOk);
        Assert.False(second.IsOk);
        Assert.Equal(new[] { ptr }, freed);
    }

    [Fact] // G1-T3 — property holds whether by the type guard or by GetValue throwing
    public void AForeignResultType_IsNeverFreed()
    {
        var (svc, freed) = Build();

        try { svc.ReadNativeResult(new { result = "Ok", inner = new IntPtr(1) }); }
        catch { }

        Assert.Empty(freed);
    }

    [Fact] // G1-T4
    public void ErrResult_ReturnsErrorNotPayload_AndFreesOnce()
    {
        var (svc, freed) = Build();
        var (boxed, ptr) = Result("Err", "AlreadyAvailable");

        var r = svc.ReadNativeResult(boxed);

        Assert.False(r.IsOk);
        Assert.Null(r.Payload);
        Assert.Equal("AlreadyAvailable", r.Error);
        Assert.Equal(new[] { ptr }, freed);
    }

    [Fact] // G1-T5
    public void OkWithNullPointer_IsAFailureAndFreesNothing()
    {
        var (svc, freed) = Build();

        var r = svc.ReadNativeResult(new FakeCResult { result = "Ok", inner = IntPtr.Zero });

        Assert.False(r.IsOk);
        Assert.Empty(freed);
    }

    [Fact] // G1-T6 — marshaller injected, never provoked with a bad pointer
    public void WhenMarshallingThrows_ThePointerIsStillFreedExactlyOnce()
    {
        var (svc, freed) = Build(_ => throw new InvalidOperationException("boom"));
        var (boxed, ptr) = Result("Ok", "payload");

        Assert.Throws<InvalidOperationException>(() => svc.ReadNativeResult(boxed));

        Assert.Equal(new[] { ptr }, freed);
    }

    [Fact] // G1-T12 — the typed reader, over a real CResultString
    public void ReadRgbLibString_FreesExactlyOnce_OnBothArms()
    {
        var freed = new List<IntPtr>();
        var svc = RgbLibServiceTestFactory.Create(
            typeof(RgbLib.CResultString),
            p => { freed.Add(p); Marshal.FreeCoTaskMem(p); },
            Marshal.PtrToStringUTF8);

        var okPtr = Marshal.StringToCoTaskMemUTF8("{\"recipient_id\":\"x\"}");
        var ok = new RgbLib.CResultString { result = RgbLib.CResultValue.Ok, inner = okPtr };
        Assert.Equal("{\"recipient_id\":\"x\"}", svc.ReadRgbLibString(ok, "invoice_data"));
        Assert.Equal(new[] { okPtr }, freed);

        freed.Clear();
        var errPtr = Marshal.StringToCoTaskMemUTF8("InvalidInvoice");
        var err = new RgbLib.CResultString { result = RgbLib.CResultValue.Err, inner = errPtr };
        var ex = Assert.Throws<RgbLibException>(() => svc.ReadRgbLibString(err, "invoice_data"));
        Assert.Contains("InvalidInvoice", ex.Message);
        Assert.Equal(new[] { errPtr }, freed);
    }

    [Fact] // G1-T7 — binding probe: the write-back depends on this
    public void RgbLibCResultString_InnerField_IsWritable()
    {
        var t = typeof(RgbLib.RgbLibWallet).Assembly.GetType("RgbLib.CResultString")!;
        var inner = t.GetField("inner")!;

        Assert.False(inner.IsInitOnly, "CResultString.inner became readonly — the write-back breaks");

        var box = Activator.CreateInstance(t)!;
        inner.SetValue(box, new IntPtr(1234));
        Assert.Equal(new IntPtr(1234), inner.GetValue(box));
    }

    static RgbLib.CResult Opaque(RgbLib.CResultValue result, ulong opaqueType, IntPtr ptr) =>
        new() { result = result, inner = new RgbLib.COpaqueStruct { ty = opaqueType, ptr = ptr } };

    [Fact]
    public void TheErrArmOfACResult_FreesItsOwnedErrorStringExactlyOnce()
    {
        var (svc, freed) = Build();
        var ptr = Marshal.StringToCoTaskMemUTF8("InvalidInvoice");

        svc.FreeCResultErrorString(
            Opaque(RgbLib.CResultValue.Err, RgbLibService.RawUtf8StringOpaqueType, ptr));

        Assert.Equal(new[] { ptr }, freed);
    }

    [Fact]
    public void TheOkArmOfACResult_IsNeverFreedAsAString_ItsInnerIsABoxedObject()
    {
        var (svc, freed) = Build();
        var ptr = Marshal.StringToCoTaskMemUTF8("not an error string");

        svc.FreeCResultErrorString(
            Opaque(RgbLib.CResultValue.Ok, RgbLibService.RawUtf8StringOpaqueType, ptr));

        Assert.Empty(freed);
        Marshal.FreeCoTaskMem(ptr);
    }

    [Fact]
    public void ACResultErrWhoseOpaqueTypeIsNotTheRawStringTag_IsNeverFreedAsAString()
    {
        var (svc, freed) = Build();
        var ptr = Marshal.StringToCoTaskMemUTF8("typed payload, not a CString");

        svc.FreeCResultErrorString(Opaque(RgbLib.CResultValue.Err, 1, ptr));

        Assert.Empty(freed);
        Marshal.FreeCoTaskMem(ptr);
    }

    [Fact]
    public void ACResultErrWithANullInnerPointer_IsNotHandedToTheDeallocator()
    {
        var (svc, freed) = Build();

        svc.FreeCResultErrorString(
            Opaque(RgbLib.CResultValue.Err, RgbLibService.RawUtf8StringOpaqueType, IntPtr.Zero));

        Assert.Empty(freed);
    }
}
