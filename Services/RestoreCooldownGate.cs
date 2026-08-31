namespace BTCPayServer.Plugins.RgbUtexo.Services;

// Thrown when a restore was STOPPED by the supervisor (wall-clock timeout, or a disk / RAM / staging
// entry cap). Derived from InvalidOperationException so every existing caller and test that catches
// that keeps working.
public sealed class RestoreAbortedException : InvalidOperationException
{
    public RestoreAbortedException(string message) : base(message) { }
}

// WHY this exists: the single-flight gate refuses CONCURRENT restores, which bounds how much work can
// run at once but says nothing about how often. Because the gate released the moment an attempt ended,
// a caller holding `CanModifyStoreSettings` on any one store — not a server admin — could re-upload
// immediately after each attempt and keep one child consuming meaningful resources continuously. Exit
// status cannot distinguish that from a typo because rgb-lib pays scrypt before validating the password,
// so every native attempt starts the duty-cycle wait.
//
// Deliberately process-wide rather than per-store: a per-store key is defeated by creating another
// store, which the same permission already allows. The cost of that choice is that one abusive tenant
// can delay another tenant's legitimate restore, bounded by RestoreKillCooldownSeconds, after any
// attempt reached the native restore process.
public sealed class RestoreCooldownGate
{
    readonly TimeSpan _cooldown;
    long _readyAtTicks;

    public RestoreCooldownGate(TimeSpan cooldown)
    {
        _cooldown = cooldown > TimeSpan.Zero ? cooldown : TimeSpan.Zero;
    }

    public DateTimeOffset? ReadyAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _readyAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public bool IsCoolingDown(DateTimeOffset now) => ReadyAt is { } readyAt && now < readyAt;

    // Monotonic in the only direction that matters: a later deadline never gets shortened by an
    // earlier kill whose cooldown is still running.
    public void RecordAttempt(DateTimeOffset now)
    {
        if (_cooldown == TimeSpan.Zero) return;
        var candidate = now.UtcDateTime.Ticks + _cooldown.Ticks;
        while (true)
        {
            var current = Interlocked.Read(ref _readyAtTicks);
            if (current >= candidate) return;
            if (Interlocked.CompareExchange(ref _readyAtTicks, candidate, current) == current) return;
        }
    }

    public TimeSpan Remaining(DateTimeOffset now) =>
        ReadyAt is { } readyAt && now < readyAt ? readyAt - now : TimeSpan.Zero;
}
