using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BTCPayServer.Plugins.RgbUtexo.Services;
using RgbLib;

namespace RgbRestoreHelper;

public sealed record RgbNativeSendRequest(
    string DataDir,
    string BitcoinNetwork,
    string ElectrumUrl,
    string XpubVanilla,
    string XpubColored,
    string MasterFingerprint,
    string LeaseWalletDir,
    string LeaseToken,
    int MaxAllocationsPerUtxo,
    string? RecipientMapJson,
    float FeeRate,
    int MinConfirmations,
    string? SignedPsbt);

public static class RgbNativeSend
{
    public static string Invoke(string operation, string requestJson)
    {
        var request = JsonSerializer.Deserialize<RgbNativeSendRequest>(requestJson)
            ?? throw new InvalidDataException("send request is missing");
        if (string.IsNullOrWhiteSpace(request.DataDir) || string.IsNullOrWhiteSpace(request.MasterFingerprint))
            throw new InvalidDataException("send request has incomplete wallet identity");
        if (string.IsNullOrWhiteSpace(request.LeaseWalletDir)
            || string.IsNullOrWhiteSpace(request.LeaseToken))
            throw new InvalidDataException("send request has no helper lease");

        // This must precede wallet construction. A replacement parent that wins recovery exclusion
        // makes this open fail, so an orphaned helper can never touch a wallet already being recovered.
        using var lease = RgbNativeSendLease.AcquireWorker(
            request.LeaseWalletDir, request.LeaseToken);
        using var walletAccess = RgbNativeSendLease.AcquireWalletAccess(
            request.LeaseWalletDir, allowMarked: true);

        var walletConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["data_dir"] = request.DataDir,
            ["bitcoin_network"] = request.BitcoinNetwork,
            ["database_type"] = "Sqlite",
            ["max_allocations_per_utxo"] = request.MaxAllocationsPerUtxo,
            ["supported_schemas"] = RgbAssetSchemaSupport.TheOnlySchemasThisPluginCanEnumerateAndSpend
        });
        var keysConfig = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["account_xpub_vanilla"] = request.XpubVanilla,
            ["account_xpub_colored"] = request.XpubColored,
            ["master_fingerprint"] = request.MasterFingerprint,
            ["vanilla_keychain"] = (int?)null,
            ["mnemonic"] = (string?)null
        });

        var wallet = new RgbLibWallet(walletConfig, keysConfig);
        BringOnlineFreeingTheNativeOnlinePayload(wallet, request.ElectrumUrl, true);
        return operation switch
        {
            "send-begin" => InvokeNative(wallet, "rgblib_send_begin",
                request.RecipientMapJson ?? throw new InvalidDataException("recipient map is missing"),
                request.FeeRate, request.MinConfirmations),
            "send-end" => InvokeNative(wallet, "rgblib_send_end",
                request.SignedPsbt ?? throw new InvalidDataException("signed PSBT is missing"),
                request.FeeRate, request.MinConfirmations),
            _ => throw new InvalidDataException($"unknown native send operation '{operation}'")
        };
    }

    static void BringOnlineFreeingTheNativeOnlinePayload(RgbLibWallet wallet, string electrumUrl,
        bool skipConsistencyCheck)
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var native = assembly.GetType("RgbLib.NativeMethods")
            ?? throw new MissingMemberException("RgbLib.NativeMethods");
        var walletField = typeof(RgbLibWallet).GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("RgbLibWallet._wallet");
        var onlineField = typeof(RgbLibWallet).GetField("_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("RgbLibWallet._onlineJson");
        var method = native.GetMethod("rgblib_go_online")
            ?? throw new MissingMethodException("rgblib_go_online");

        var onlineOptionsJson = JsonSerializer.Serialize(new
        {
            indexer_url = electrumUrl,
            skip_consistency_check = skipConsistencyCheck,
            vanilla_sync_lookback = 100u
        });

        object?[] args = [walletField.GetValue(wallet)!, onlineOptionsJson];
        var result = method.Invoke(null, args);
        walletField.SetValue(wallet, args[0]);

        var onlineJson = ReadResult(result, "rgblib_go_online");
        if (onlineJson.Length == 0)
            throw new InvalidOperationException("go_online returned an empty online JSON");
        onlineField.SetValue(wallet, onlineJson);
    }

    static string InvokeNative(RgbLibWallet wallet, string methodName, string payload, float feeRate,
        int minConfirmations)
    {
        var assembly = typeof(RgbLibWallet).Assembly;
        var native = assembly.GetType("RgbLib.NativeMethods")
            ?? throw new MissingMemberException("RgbLib.NativeMethods");
        var walletField = typeof(RgbLibWallet).GetField("_wallet", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("RgbLibWallet._wallet");
        var onlineField = typeof(RgbLibWallet).GetField("_onlineJson", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException("RgbLibWallet._onlineJson");
        var method = native.GetMethod(methodName)
            ?? throw new MissingMethodException(methodName);
        var walletStruct = walletField.GetValue(wallet)!;
        var online = (string)(onlineField.GetValue(wallet)
            ?? throw new InvalidOperationException("wallet is offline"));
        object?[] args = methodName == "rgblib_send_begin"
            ? [walletStruct, online, payload, false, ((int)Math.Round(feeRate)).ToString(),
                minConfirmations.ToString(), null, false]
            : [walletStruct, online, payload.Trim('"')];
        var result = method.Invoke(null, args);
        walletField.SetValue(wallet, args[0]);
        return ReadResult(result, methodName);
    }

    [DllImport("rgblibcffi", CallingConvention = CallingConvention.Cdecl)]
    static extern void rgblib_string_free(IntPtr ptr);

    public static void FreeNativeString(IntPtr ptr) => rgblib_string_free(ptr);

    public static string ReadResult(object? result, string methodName)
    {
        if (result == null)
            throw new InvalidOperationException($"{methodName} returned null");
        var type = result.GetType();
        var status = type.GetField("result")?.GetValue(result)?.ToString();
        var innerField = type.GetField("inner")
            ?? throw new MissingFieldException(type.FullName, "inner");
        var pointer = (IntPtr)(innerField.GetValue(result) ?? IntPtr.Zero);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException($"{methodName} returned no detail");
        innerField.SetValue(result, IntPtr.Zero);
        try
        {
            var text = Marshal.PtrToStringUTF8(pointer)
                ?? throw new InvalidOperationException($"{methodName} returned invalid UTF-8");
            if (status != "Ok")
                throw new InvalidOperationException(text);
            return text;
        }
        finally
        {
            rgblib_string_free(pointer);
        }
    }
}
