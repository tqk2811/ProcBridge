namespace ProcBridge.Relay.Testing.Infrastructure;

/// <summary>A clock tests move by hand (slot lifetimes, rate windows) instead of sleeping. Thread-safe.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_lock)
            _now += by;
    }
}
