using System.Runtime.InteropServices;
using RgbLib;

namespace RgbRestoreHelper;

public static class RgbRestoreNative
{
    public const string UndiagnosedRestoreFailure = "restore_backup failed";

    public const string ErrorDiscriminant = "Err";

    public const ulong RawUtf8StringOpaqueType = 0;

    static readonly Func<string, string, string, (bool ok, string err)> _real = RealInvoke;

    public static Func<string, string, string, (bool ok, string err)> NativeInvoke { get; set; } = RealInvoke;

    public static void ResetNativeInvoke() => NativeInvoke = _real;

    public static int Restore(string backupPath, string stagingDir, string password, out string error)
    {
        var (ok, err) = NativeInvoke(backupPath, password, stagingDir);
        error = err;
        return ok ? 0 : 1;
    }

    static (bool ok, string err) RealInvoke(string backupPath, string password, string targetDir)
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var nativeMethods = assembly.GetType("RgbLib.NativeMethods")!;
        var method = nativeMethods.GetMethod("rgblib_restore_backup")!;
        var result = method.Invoke(null, new object?[] { backupPath, password, targetDir });
        if (result == null) return (false, "restore_backup returned null");

        var t = result.GetType();
        var isSuccessProp = t.GetProperty("IsSuccess");
        if (isSuccessProp == null) return (false, "restore_backup: cannot read result type");
        var isSuccess = (bool)(isSuccessProp.GetValue(result) ?? false);
        if (isSuccess) return (true, "");

        var msg = UndiagnosedRestoreFailure;
        try { msg = ReadCResultErrorStringFromOpaqueInnerPointer(result, RgbNativeSend.FreeNativeString); }
        catch { }
        return (false, msg);
    }

    public static string ReadCResultErrorStringFromOpaqueInnerPointer(
        object result, Action<IntPtr> freeNativeString)
    {
        var type = result.GetType();
        if (type.GetField("result")?.GetValue(result)?.ToString() != ErrorDiscriminant)
            return UndiagnosedRestoreFailure;
        var inner = type.GetField("inner")?.GetValue(result);
        if (inner == null) return UndiagnosedRestoreFailure;
        var innerType = inner.GetType();
        if (innerType.GetField("ty")?.GetValue(inner) is not ulong opaqueType
            || opaqueType != RawUtf8StringOpaqueType)
            return UndiagnosedRestoreFailure;
        if (innerType.GetField("ptr")?.GetValue(inner) is not IntPtr pointer
            || pointer == IntPtr.Zero)
            return UndiagnosedRestoreFailure;

        var text = Marshal.PtrToStringUTF8(pointer);
        try { freeNativeString(pointer); } catch { }
        return string.IsNullOrWhiteSpace(text) ? UndiagnosedRestoreFailure : text!;
    }
}
