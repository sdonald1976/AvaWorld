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
