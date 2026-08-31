using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Controllers;

public static class RgbOperatorFacingFailure
{
    public const string EscalateToServerLogs = "Check server logs for details.";

    public static bool MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime(Exception ex) =>
        ex is InvalidOperationException or KeyNotFoundException or RgbLibException;

    public static string OperatorFacingLayerMessageOrFallback(Exception ex, string fallback) =>
        MessageComesFromAnOperatorFacingLayerNotTheDotnetRuntime(ex) ? ex.Message : fallback;
}
