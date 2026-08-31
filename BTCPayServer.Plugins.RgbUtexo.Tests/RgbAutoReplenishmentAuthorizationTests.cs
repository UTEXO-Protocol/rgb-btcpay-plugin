using BTCPayServer.Plugins.RgbUtexo.Data.Entities;
using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

public class RgbAutoReplenishmentAuthorizationTests
{
    const string Store = "store-1";
    const string Wallet = "wallet-1";

    static RGBStoreAutoReplenishment Row(
        RgbAutoReplenishmentDecision decision,
        string? decidedForWalletId,
        DateTimeOffset? decidedAt = null,
        string? decidedBy = null) =>
        new()
        {
            StoreId = Store,
            Decision = decision,
            DecidedForWalletId = decidedForWalletId,
            DecidedAt = decidedAt,
            DecidedBy = decidedBy
        };

    [Fact]
    public void MissingRow_IsNotAuthorized()
        => Assert.False(RgbAutoReplenishmentAuthorizationStore.IsGranted(null, Wallet),
            "absence must mean 'not authorized' unconditionally and permanently. Nothing automatic ever "
            + "writes this row, so a missing row is the state of every store that has never decided — "
            + "including every store created after an upgrade. Defaulting absence to granted is the "
            + "round-3 blocker: it hands standing unattended-signing authority to stores that never "
            + "decided.");

    [Theory]
    [InlineData(RgbAutoReplenishmentDecision.Undecided)]
    [InlineData(RgbAutoReplenishmentDecision.Revoked)]
    public void OnlyGrantedAuthorizes(RgbAutoReplenishmentDecision decision)
        => Assert.False(
            RgbAutoReplenishmentAuthorizationStore.IsGranted(Row(decision, Wallet), Wallet),
            $"only Decision = Granted authorizes unattended signing; {decision} must not. A revoked "
            + "decision that came back as authorized would be a false-ACCEPT that survives a restart.");

    [Fact]
    public void GrantedForTheConfiguredWallet_IsAuthorized()
        => Assert.True(
            RgbAutoReplenishmentAuthorizationStore.IsGranted(
                Row(RgbAutoReplenishmentDecision.Granted, Wallet,
                    decidedAt: new DateTimeOffset(2026, 8, 22, 9, 30, 0, TimeSpan.FromHours(2)),
                    decidedBy: "user-7"),
                Wallet),
            "a grant recorded for exactly this wallet must authorize, or granting is a no-op and the "
            + "operator can never turn automatic replenishment on — a permanent refusal on the "
            + "operator's own recovery path.");

    [Fact]
    public void GrantedWithEveryOptionalFieldAtItsClrDefault_IsStillAuthorized()
        => Assert.True(
            RgbAutoReplenishmentAuthorizationStore.IsGranted(
                Row(RgbAutoReplenishmentDecision.Granted, Wallet), Wallet),
            "DecidedAt and DecidedBy are audit fields that nothing reads for a decision, so a row with "
            + "both null must still authorize. A predicate keyed on either would pass every test that "
            + "sets them and refuse rows that do not.");

    [Fact]
    public void GrantedForADifferentWallet_IsNotAuthorized()
        => Assert.False(
            RgbAutoReplenishmentAuthorizationStore.IsGranted(
                Row(RgbAutoReplenishmentDecision.Granted, "wallet-2"), Wallet),
            "the grant is wallet-bound: a wallet replacement leaves the decision effectively revoked and "
            + "requires an explicit re-grant. Honouring it for any wallet would let a restored or "
            + "recreated wallet inherit unattended-signing authority the operator never gave it.");

    [Fact]
    public void GrantedWithNoWalletRecorded_IsNotAuthorized()
        => Assert.False(
            RgbAutoReplenishmentAuthorizationStore.IsGranted(
                Row(RgbAutoReplenishmentDecision.Granted, null), Wallet),
            "a null DecidedForWalletId must not match a configured wallet id. Null-matches-anything is "
            + "the false-ACCEPT direction, and DecidedForWalletId is nullable precisely so a revoke can "
            + "clear it.");
}
