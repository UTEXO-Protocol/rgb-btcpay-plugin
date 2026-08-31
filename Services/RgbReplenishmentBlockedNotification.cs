using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.RgbUtexo.Services;

public class RgbReplenishmentBlockedNotification : BaseNotification
{
    const string TYPE = "rgb-replenishment-blocked";

    public string StoreId { get; set; } = "";
    public RgbReplenishmentNoticeCause Cause { get; set; }
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;

    public class Handler : NotificationHandler<RgbReplenishmentBlockedNotification>
    {
        readonly LinkGenerator _linkGenerator;
        readonly BTCPayServerOptions _options;

        public Handler(LinkGenerator linkGenerator, BTCPayServerOptions options)
        {
            _linkGenerator = linkGenerator;
            _options = options;
        }

        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta =>
            [(TYPE, "RGB payments are blocked")];

        protected override void FillViewModel(
            RgbReplenishmentBlockedNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = RgbReplenishmentNotice.MessageFor(notification.Cause);
            vm.ActionLink = _linkGenerator.GetPathByAction(
                action: "Settings",
                controller: "RGB",
                values: new { storeId = notification.StoreId },
                pathBase: _options.RootPath);
        }
    }
}
