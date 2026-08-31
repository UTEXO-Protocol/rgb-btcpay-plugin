using BTCPayServer.Plugins.RgbUtexo.Data;
using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Services.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbReplenishmentNoticeService : IRgbNoticeRaiser
{
    readonly RGBPluginDbContextFactory _db;
    readonly NotificationSender _notifications;
    readonly ILogger<RgbReplenishmentNoticeService> _log;
    readonly RgbNoticeAttemptGate _gate = new();

    public RgbReplenishmentNoticeService(
        RGBPluginDbContextFactory db,
        NotificationSender notifications,
        ILogger<RgbReplenishmentNoticeService> log)
    {
        _db = db;
        _notifications = notifications;
        _log = log;
    }

    internal static DateTimeOffset? MarkerOf(RGBStoreNoticeState row, RgbReplenishmentNoticeCause cause)
        => cause switch
        {
            RgbReplenishmentNoticeCause.NotAuthorized => row.NotAuthorizedNoticeSentAt,
            RgbReplenishmentNoticeCause.CapDisabledDeploymentWide => row.CapDisabledNoticeSentAt,
            RgbReplenishmentNoticeCause.ConfigOutOfBounds => row.ConfigOutOfBoundsNoticeSentAt,
            RgbReplenishmentNoticeCause.PricingCodeHasNoRule => row.PricingCodeHasNoRuleNoticeSentAt,
            _ => DateTimeOffset.MinValue
        };

    internal static void StampMarker(
        RGBStoreNoticeState row, RgbReplenishmentNoticeCause cause, DateTimeOffset at)
    {
        switch (cause)
        {
            case RgbReplenishmentNoticeCause.NotAuthorized:
                row.NotAuthorizedNoticeSentAt = at;
                break;
            case RgbReplenishmentNoticeCause.CapDisabledDeploymentWide:
                row.CapDisabledNoticeSentAt = at;
                break;
            case RgbReplenishmentNoticeCause.ConfigOutOfBounds:
                row.ConfigOutOfBoundsNoticeSentAt = at;
                break;
            case RgbReplenishmentNoticeCause.PricingCodeHasNoRule:
                row.PricingCodeHasNoRuleNoticeSentAt = at;
                break;
        }
    }

    internal virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    internal virtual Task SendBlockedNotificationAsync(
        string storeId, RgbReplenishmentNoticeCause cause) =>
        _notifications.SendNotification(
            new StoreScope(storeId),
            new RgbReplenishmentBlockedNotification { StoreId = storeId, Cause = cause });

    public async Task RaiseOncePerCauseAsync(
        string storeId, RgbReplenishmentNoticeCause cause, CancellationToken ct = default)
    {
        if (cause == RgbReplenishmentNoticeCause.None) return;
        if (string.IsNullOrEmpty(storeId)) return;
        if (!_gate.TryBeginAttempt(storeId, cause, UtcNow, out var lease)) return;

        using (lease)
        {
            var notificationCommitted = false;
            try
            {
                await using var ctx = _db.CreateContext();
                var row = await ctx.RGBStoreNoticeStates.FirstOrDefaultAsync(r => r.StoreId == storeId, ct);
                if (row == null)
                {
                    row = new RGBStoreNoticeState { StoreId = storeId };
                    ctx.RGBStoreNoticeStates.Add(row);
                }
                if (MarkerOf(row, cause) != null)
                {
                    _gate.MarkRaised(storeId, cause);
                    return;
                }

                await SendBlockedNotificationAsync(storeId, cause);
                notificationCommitted = true;

                StampMarker(row, cause, UtcNow);
                await ctx.SaveChangesAsync(ct);
                _gate.MarkRaised(storeId, cause);
            }
            catch (Exception ex)
            {
                if (notificationCommitted)
                {
                    _gate.MarkRaised(storeId, cause);
                    _log.LogWarning(ex,
                        "Raised the RGB blocked notification for store {StoreId}, cause {Cause}, but failed to record it. The merchant has been notified, so this process will not send it again; a restart may send it once more",
                        storeId, cause);
                }
                else
                {
                    _gate.MarkSendFailed(storeId, cause, UtcNow);
                    _log.LogWarning(ex,
                        "Failed to raise the RGB blocked notification for store {StoreId}, cause {Cause}; nothing was sent, so the next attempt is admitted after {RetryMinutes} minute(s)",
                        storeId, cause, RgbNoticeAttemptGate.RetryAfterSendFailure.TotalMinutes);
                }
            }
        }
    }
}
