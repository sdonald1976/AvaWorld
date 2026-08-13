using Microsoft.Extensions.Logging;

namespace AvaWorld.Simulation;

/// <summary>
/// The world, as a pure simulation. It has no reference to Godot, no scene tree, and no idea
/// whether anything is rendering it — which is the point: the world must produce identical history
/// with no client ever connected, and the only way to keep that true is to make the dependency
/// impossible rather than merely discouraged.
///
/// Time is wall-clock time, 1:1. There is no separate world calendar to drift out of step with the
/// user's day, so "this morning" means the same morning to both of them.
/// </summary>
public sealed class World
{
    /// <summary>
    /// How far <see cref="WorldState.LastTickedAt"/> may fall behind before the distance is treated
    /// as the world having been *stopped* rather than merely between ticks. Comfortably larger than
    /// any normal tick interval, so a slow frame or a paused debugger is not mistaken for downtime.
    /// </summary>
    public static readonly TimeSpan DowntimeThreshold = TimeSpan.FromMinutes(2);

    private readonly IWorldStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<World> _logger;

    public World(IWorldStore store, TimeProvider clock, ILogger<World> logger)
    {
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The live state. Null until <see cref="StartAsync"/> has run.</summary>
    public WorldState? State { get; private set; }

    /// <summary>
    /// Loads the world and brings it up to the present. If the saved state is older than
    /// <see cref="DowntimeThreshold"/>, the intervening time is recorded as a gap — and pointedly
    /// NOT simulated. Synthesising a plausible eight hours would be easy, would make the world seem
    /// more alive, and would be indistinguishable from real history once written. A gap stays a gap.
    /// </summary>
    public async Task<StartResult> StartAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);

        if (loaded is null)
        {
            State = new WorldState { CreatedAt = now, LastTickedAt = now };
            await _store.SaveAsync(State, ct).ConfigureAwait(false);
            _logger.LogInformation("World created at {CreatedAt:u}.", now);
            return new StartResult(Created: true, Gap: null);
        }

        State = loaded;

        // Clock went backwards (a corrected system clock, a restored save). Don't invent a negative
        // gap or rewind lived time; just resynchronise and carry on.
        if (now < State.LastTickedAt)
        {
            _logger.LogWarning(
                "Saved world is {Ahead} ahead of the current clock; resynchronising to now.",
                State.LastTickedAt - now);
            State.LastTickedAt = now;
            await _store.SaveAsync(State, ct).ConfigureAwait(false);
            return new StartResult(Created: false, Gap: null);
        }

        var away = now - State.LastTickedAt;
        if (away < DowntimeThreshold)
        {
            _logger.LogInformation("World resumed; it was only away {Away}.", away);
            State.LastTickedAt = now;
            await _store.SaveAsync(State, ct).ConfigureAwait(false);
            return new StartResult(Created: false, Gap: null);
        }

        var gap = new Downtime(State.LastTickedAt, now);
        State.Gaps.Add(gap);
        State.LastTickedAt = now;
        await _store.SaveAsync(State, ct).ConfigureAwait(false);

        _logger.LogWarning(
            "World was not running for {Duration} ({From:u} → {To:u}). That period has no history and "
            + "none will be generated for it.",
            gap.Duration, gap.From, gap.To);

        return new StartResult(Created: false, Gap: gap);
    }

    /// <summary>
    /// Advances the world to the present. Everything the world does over time will hang off this
    /// call; today it only accounts for the passage of time, which is exactly enough to prove the
    /// server keeps running when nothing is watching it.
    ///
    /// Safe to call at any interval: elapsed time is measured, never assumed, so a missed tick
    /// slows nothing down and a fast one does no extra work.
    /// </summary>
    public async Task<TimeSpan> TickAsync(CancellationToken ct = default)
    {
        var state = State ?? throw new InvalidOperationException("StartAsync must run before TickAsync.");

        var now = _clock.GetUtcNow();
        var elapsed = now - state.LastTickedAt;
        if (elapsed <= TimeSpan.Zero)
            return TimeSpan.Zero; // clock hasn't moved (or went backwards) — nothing to account for

        // A tick this far apart means the process was stopped, not slow. Record it rather than
        // silently folding hours of absence into "lived" time.
        if (elapsed >= DowntimeThreshold)
        {
            var gap = new Downtime(state.LastTickedAt, now);
            state.Gaps.Add(gap);
            state.LastTickedAt = now;
            state.Ticks++;
            await _store.SaveAsync(state, ct).ConfigureAwait(false);
            _logger.LogWarning("Tick gap of {Duration} recorded as downtime, not lived time.", gap.Duration);
            return TimeSpan.Zero;
        }

        state.LastTickedAt = now;
        state.Lived += elapsed;
        state.Ticks++;
        await _store.SaveAsync(state, ct).ConfigureAwait(false);
        return elapsed;
    }

    // ---- places and occupancy ----

    /// <summary>
    /// How many events are kept. Generous enough to cover a long session, small enough that
    /// rewriting the save file on every tick stays cheap.
    /// </summary>
    public const int MaxRetainedEvents = 500;

    /// <summary>
    /// The layout. Set once at startup by whoever authored the world; not persisted, because the
    /// layout is authored rather than accumulated and a save that disagreed with it would be
    /// worse than no save.
    /// </summary>
    public PlaceGraph Places { get; private set; } = new();

    /// <summary>
    /// Installs the layout and reconciles the saved occupancy against it. A body recorded in a
    /// place that no longer exists — the layout changed under a running world — is moved to
    /// <paramref name="fallbackPlace"/> rather than left pointing at nothing.
    /// </summary>
    public async Task DefineLayoutAsync(
        PlaceGraph places, string fallbackPlace, CancellationToken ct = default)
    {
        var state = State ?? throw new InvalidOperationException("StartAsync must run before DefineLayoutAsync.");
        if (!places.Contains(fallbackPlace))
            throw new ArgumentException($"Fallback place '{fallbackPlace}' is not in the layout.", nameof(fallbackPlace));

        Places = places;

        var stranded = state.Occupancy
            .Where(pair => !places.Contains(pair.Value))
            .ToList();

        foreach (var (body, gone) in stranded)
        {
            _logger.LogWarning(
                "{Body} was in '{Gone}', which the layout no longer has; moved to '{Fallback}'.",
                body, gone, fallbackPlace);
            state.Occupancy[body] = fallbackPlace;
        }

        if (stranded.Count > 0)
            await _store.SaveAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>Where a body is, or null if it isn't in the world.</summary>
    public string? PlaceOf(string body) =>
        State is not null && State.Occupancy.TryGetValue(body, out var place) ? place : null;

    /// <summary>Everyone currently in a place.</summary>
    public IReadOnlyList<string> Occupants(string placeId) =>
        State is null
            ? Array.Empty<string>()
            : State.Occupancy
                .Where(p => string.Equals(p.Value, placeId, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key)
                .OrderBy(b => b, StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// Records that a body is now in a place. Returns true if this was a change — callers can
    /// drive this from a physics volume firing every frame without writing an event each time.
    /// </summary>
    public async Task<bool> EnterAsync(string body, string placeId, CancellationToken ct = default)
    {
        var state = State ?? throw new InvalidOperationException("StartAsync must run before EnterAsync.");
        if (!Places.Contains(placeId))
            throw new InvalidOperationException($"Unknown place '{placeId}'.");

        var known = state.Occupancy.TryGetValue(body, out var current);
        if (known && string.Equals(current, placeId, StringComparison.OrdinalIgnoreCase))
            return false;

        state.Occupancy[body] = placeId;
        Record(state, new WorldEvent(
            _clock.GetUtcNow(),
            known ? WorldEventKind.Arrived : WorldEventKind.Joined,
            body,
            placeId));

        await _store.SaveAsync(state, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Removes a body from the world (a client disconnected). Ava is never removed.</summary>
    public async Task<bool> LeaveAsync(string body, CancellationToken ct = default)
    {
        var state = State ?? throw new InvalidOperationException("StartAsync must run before LeaveAsync.");
        if (!state.Occupancy.Remove(body))
            return false;

        Record(state, new WorldEvent(_clock.GetUtcNow(), WorldEventKind.Left, body, null));
        await _store.SaveAsync(state, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The most recent events, newest last.</summary>
    public IReadOnlyList<WorldEvent> RecentEvents(int count = 20) =>
        State is null
            ? Array.Empty<WorldEvent>()
            : State.Events.TakeLast(Math.Max(0, count)).ToList();

    private static void Record(WorldState state, WorldEvent e)
    {
        state.Events.Add(e);
        if (state.Events.Count > MaxRetainedEvents)
            state.Events.RemoveRange(0, state.Events.Count - MaxRetainedEvents);
    }

    /// <summary>Total time the world was not running.</summary>
    public TimeSpan TotalDowntime =>
        State is null ? TimeSpan.Zero : State.Gaps.Aggregate(TimeSpan.Zero, (sum, g) => sum + g.Duration);

    /// <summary>
    /// True when the world was running for the whole of the given instant's surroundings — i.e. no
    /// recorded gap covers it. The honest basis for "I have no record of that afternoon".
    /// </summary>
    public bool WasRunningAt(DateTimeOffset instant) =>
        State is not null
        && instant >= State.CreatedAt
        && instant <= State.LastTickedAt
        && !State.Gaps.Any(g => instant > g.From && instant < g.To);
}

/// <summary>What starting the world revealed. <paramref name="Gap"/> is null on a clean resume.</summary>
public readonly record struct StartResult(bool Created, Downtime? Gap);
