namespace AvaWorld.Simulation;

/// <summary>
/// Everything the world remembers between runs. Deliberately small: this is the seed the whole
/// simulation grows from, and every field added here has to survive a restart forever after.
/// </summary>
public sealed class WorldState
{
    /// <summary>When this world first came into existence. Never changes.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The last moment the simulation is known to have been running. This is the load-bearing
    /// field: everything before it is accounted for, and any distance between it and "now" on
    /// startup is time the world was not running.
    /// </summary>
    public DateTimeOffset LastTickedAt { get; set; }

    /// <summary>
    /// Periods when the world was not running, in order. Recorded so the log can be honest about
    /// being incomplete — "nothing happened then" and "we have no record of then" are different
    /// claims, and only one of them is true after a restart.
    ///
    /// These are never turned into events, and nothing ever fills them in. See DESIGN.md.
    /// </summary>
    public List<Downtime> Gaps { get; set; } = new();

    /// <summary>How many times the simulation has advanced. Diagnostic only.</summary>
    public long Ticks { get; set; }

    /// <summary>Total time the world has actually been running, excluding gaps.</summary>
    public TimeSpan Lived { get; set; }

    /// <summary>
    /// Where each body is, by place id. Persisted, so Ava is where she was when the world stopped
    /// rather than teleporting to a spawn point on every restart — she lives here, and the room
    /// she was in is part of what continuing means.
    /// </summary>
    public Dictionary<string, string> Occupancy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What has happened, oldest first, capped at <see cref="World.MaxRetainedEvents"/>.
    ///
    /// A cap rather than unbounded growth because this file is rewritten on every tick, and an
    /// ever-growing array would make saving slower for as long as the world exists. Retention
    /// policy will need revisiting once events feed reflection — but silently unbounded is not a
    /// policy, it is the absence of one.
    /// </summary>
    public List<WorldEvent> Events { get; set; } = new();
}

/// <summary>A period the world was not running. Both ends inclusive of the observed boundary.</summary>
/// <param name="From">The last tick before the world stopped.</param>
/// <param name="To">The first tick after it started again.</param>
public readonly record struct Downtime(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Duration => To - From;
}
