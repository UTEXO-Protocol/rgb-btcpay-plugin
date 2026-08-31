using BTCPayServer.Plugins.RgbUtexo.Services;

namespace BTCPayServer.Plugins.RgbUtexo.Tests;

// The single-flight gate bounds how much restore work runs AT ONCE; it said nothing about how often.
// Because it released the moment an attempt ended, a caller holding CanModifyStoreSettings on any one
// store could re-upload immediately after each attempt and keep one child consuming meaningful resources
// continuously. These tests pin the outcome-independent duty cycle; RestoreGateTests pins its placement
// around every native attempt.
[Collection("RestoreSerial")]
public class RestoreCooldownGateTests
{
    static readonly DateTimeOffset T0 = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFreshGateIsNotCoolingDown()
    {
        Assert.False(new RestoreCooldownGate(TimeSpan.FromSeconds(60)).IsCoolingDown(T0));
    }

    [Fact]
    public void AnAttemptBlocksTheNextOne()
    {
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(60));
        gate.RecordAttempt(T0);

        Assert.True(gate.IsCoolingDown(T0));
        Assert.True(gate.IsCoolingDown(T0.AddSeconds(59)));
    }

    [Fact]
    public void TheCooldownExpires()
    {
        // A permanent refusal here would be a fund-loss bug, not a safe failure: the wallet could never
        // be restored again.
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(60));
        gate.RecordAttempt(T0);

        Assert.False(gate.IsCoolingDown(T0.AddSeconds(60)));
        Assert.False(gate.IsCoolingDown(T0.AddSeconds(61)));
    }

    [Fact]
    public void RemainingCountsDownAndFloorsAtZero()
    {
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(60));
        gate.RecordAttempt(T0);

        Assert.Equal(TimeSpan.FromSeconds(60), gate.Remaining(T0));
        Assert.Equal(TimeSpan.FromSeconds(15), gate.Remaining(T0.AddSeconds(45)));
        Assert.Equal(TimeSpan.Zero, gate.Remaining(T0.AddSeconds(120)));
    }

    [Fact]
    public void AnEarlierKillNeverShortensARunningCooldown()
    {
        // Out-of-order stamping must not become a way to shorten the wait.
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(60));
        gate.RecordAttempt(T0.AddSeconds(30));
        gate.RecordAttempt(T0);

        Assert.True(gate.IsCoolingDown(T0.AddSeconds(85)));
    }

    [Fact]
    public void ALaterKillExtendsTheCooldown()
    {
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(60));
        gate.RecordAttempt(T0);
        gate.RecordAttempt(T0.AddSeconds(30));

        Assert.True(gate.IsCoolingDown(T0.AddSeconds(85)));
        Assert.False(gate.IsCoolingDown(T0.AddSeconds(90)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveCooldownDisablesTheGateEntirely(int seconds)
    {
        // An operator must be able to turn this off without it degenerating into "always cooling down".
        var gate = new RestoreCooldownGate(TimeSpan.FromSeconds(seconds));
        gate.RecordAttempt(T0);

        Assert.False(gate.IsCoolingDown(T0));
        Assert.Null(gate.ReadyAt);
    }

    [Fact]
    public async Task ConcurrentFirstUseReturnsOneProcessWideGate()
    {
        var field = typeof(RGBWalletService).GetField("_restoreCooldown",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        field.SetValue(null, null);
        using var rendezvous = new Barrier(2);
        try
        {
            RestoreCooldownGate Create()
            {
                rendezvous.SignalAndWait();
                return new RestoreCooldownGate(TimeSpan.FromSeconds(60));
            }

            var first = Task.Run(() => RGBWalletService.GetOrCreateRestoreCooldown(Create));
            var second = Task.Run(() => RGBWalletService.GetOrCreateRestoreCooldown(Create));
            var gates = await Task.WhenAll(first, second);

            Assert.Same(gates[0], gates[1]);
        }
        finally { field.SetValue(null, null); }
    }
}
