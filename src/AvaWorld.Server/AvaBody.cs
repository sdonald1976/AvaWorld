using AvaWorld.Simulation;
using Godot;

namespace AvaWorld.Server;

/// <summary>
/// Ava's body, on the server. She exists whether or not anyone is connected to watch her, which is
/// the whole point of the world being a server — clients render a copy of where she is, they do
/// not own her.
///
/// This node walks; it does not decide. It reads a destination the world already holds, asks the
/// <see cref="Navigator"/> for the way, and follows it. What sets that destination is a placeholder
/// today (see <see cref="Wandering"/>) and becomes the companion at step five. That seam is the
/// design's central boundary: the companion decides where and why, the world decides how.
/// </summary>
public sealed class AvaBody
{
    public const string BodyId = "ava";

    /// <summary>An unhurried walking pace. She is not going anywhere in a hurry.</summary>
    private const float WalkSpeed = 2.6f;

    /// <summary>Close enough to a waypoint to call it reached.</summary>
    private const float ArrivalRadius = 0.6f;

    private readonly World _world;
    private readonly Navigator _navigator;
    private readonly WorldMap _map;

    private Vector3 _position;
    private IReadOnlyList<Waypoint> _route = Array.Empty<Waypoint>();
    private int _step;
    private string? _routedTo;

    public AvaBody(World world, Navigator navigator, WorldMap map)
    {
        _world = world;
        _navigator = navigator;
        _map = map;

        // Start standing in whatever room she was in when the world last stopped.
        var place = world.PlaceOf(BodyId) ?? Cottage.Spawn;
        var bounds = map.For(place);
        _position = bounds is null
            ? Vector3.Zero
            : new Vector3(bounds.Value.CentreX, 0f, bounds.Value.CentreZ);
    }

    public Vector3 Position => _position;

    /// <summary>The room she is standing in right now, or null if she is in a doorway.</summary>
    public string? CurrentPlace => _map.PlaceAt(_position.X, _position.Z);

    public bool IsWalking => _step < _route.Count;

    /// <summary>
    /// Moves her along. Returns the place she has just entered, or null if she has not changed
    /// room this step — the caller records that, because writing to the world is not this class's
    /// job either.
    /// </summary>
    public string? Advance(double delta)
    {
        var destination = _world.DestinationOf(BodyId);

        // A new destination replaces whatever she was doing. She is allowed to change her mind
        // mid-corridor; insisting she finish a journey first would read as stubbornness.
        if (destination is not null && !string.Equals(destination, _routedTo, StringComparison.OrdinalIgnoreCase))
        {
            var from = CurrentPlace ?? _world.PlaceOf(BodyId) ?? Cottage.Spawn;
            _route = _navigator.RouteTo(from, destination);
            _routedTo = destination;
            _step = 0;
        }
        else if (destination is null)
        {
            _routedTo = null;
        }

        if (_step >= _route.Count)
            return null;

        var target = _route[_step];
        var to = new Vector3(target.X, 0f, target.Z) - _position;

        if (to.Length() <= ArrivalRadius)
        {
            _position = new Vector3(target.X, 0f, target.Z);
            _step++;
            return target.Place; // null for a doorway — passing through is not arriving
        }

        _position += to.Normalized() * (float)(WalkSpeed * delta);
        return null;
    }
}

/// <summary>
/// A placeholder for having somewhere to be: every so often, pick a room she is not in and go
/// there.
///
/// Explicitly not the real thing. The design has roaming driven deterministically by her state —
/// spirits, energy by hour, open curiosities — all of which live in the companion, not here. This
/// exists so the world is visibly alive before that connection is built, and it should be deleted
/// rather than extended when step five lands. Wandering with no reason is the thing the design is
/// trying not to end up with.
/// </summary>
public sealed class Wandering
{
    private readonly PlaceGraph _graph;
    private readonly TimeSpan _pause;
    private readonly Random _random;

    private TimeSpan _sinceLastMove = TimeSpan.Zero;

    public Wandering(PlaceGraph graph, TimeSpan pause, int seed = 20260813)
    {
        _graph = graph;
        _pause = pause;
        _random = new Random(seed); // seeded: a reproducible world is easier to reason about
    }

    /// <summary>Where she should head next, or null to stay put a while longer.</summary>
    public string? Next(double delta, string? currentPlace, bool walking)
    {
        if (walking)
        {
            _sinceLastMove = TimeSpan.Zero;
            return null;
        }

        _sinceLastMove += TimeSpan.FromSeconds(delta);
        if (_sinceLastMove < _pause)
            return null;

        _sinceLastMove = TimeSpan.Zero;

        var options = _graph.All
            .Select(p => p.Id)
            .Where(id => !string.Equals(id, currentPlace, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return options.Count == 0 ? null : options[_random.Next(options.Count)];
    }
}
