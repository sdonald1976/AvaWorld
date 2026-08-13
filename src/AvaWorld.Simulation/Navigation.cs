namespace AvaWorld.Simulation;

/// <summary>The way between two adjoining places, and the ground it covers.</summary>
public readonly record struct Doorway(string A, string B, PlaceBounds Bounds)
{
    public bool Joins(string x, string y) =>
        (string.Equals(A, x, StringComparison.OrdinalIgnoreCase) && string.Equals(B, y, StringComparison.OrdinalIgnoreCase))
        || (string.Equals(A, y, StringComparison.OrdinalIgnoreCase) && string.Equals(B, x, StringComparison.OrdinalIgnoreCase));

    public (float X, float Z) Middle => (Bounds.CentreX, Bounds.CentreZ);
}

/// <summary>A point on the ground to walk to, and what it is for.</summary>
/// <param name="X">Metres.</param>
/// <param name="Z">Metres.</param>
/// <param name="Place">The place this step arrives in, or null for a doorway on the way.</param>
public readonly record struct Waypoint(float X, float Z, string? Place);

/// <summary>
/// Works out how to get from one place to another: which rooms to pass through, and the points on
/// the ground to aim at on the way.
///
/// This is the world's half of the bargain in the design. The companion says "go to the
/// greenhouse" and never says how; the route from wherever she is to wherever that is belongs
/// here, and — crucially — it belongs in the simulation rather than the engine, so it can be
/// tested without a display.
///
/// It routes between rooms, not around obstacles. That is the right granularity for a world whose
/// rooms are empty; the moment there is furniture to walk around, a navmesh belongs *underneath*
/// this, steering between these waypoints rather than replacing them.
/// </summary>
public sealed class Navigator
{
    private readonly PlaceGraph _graph;
    private readonly WorldMap _map;
    private readonly IReadOnlyList<Doorway> _doorways;

    public Navigator(PlaceGraph graph, WorldMap map, IReadOnlyList<Doorway> doorways)
    {
        _graph = graph;
        _map = map;
        _doorways = doorways;
    }

    /// <summary>
    /// The points to walk through to get from <paramref name="from"/> to <paramref name="to"/>,
    /// ending at the centre of the destination. Empty when there is no way — which callers must
    /// treat as "she cannot get there" rather than walking in a straight line through a wall.
    /// </summary>
    public IReadOnlyList<Waypoint> RouteTo(string from, string to)
    {
        var rooms = _graph.Route(from, to);
        if (rooms.Count == 0)
            return Array.Empty<Waypoint>();

        var waypoints = new List<Waypoint>();

        for (var i = 1; i < rooms.Count; i++)
        {
            var previous = rooms[i - 1];
            var next = rooms[i];

            // Aim for the doorway first, so she goes through the gap rather than cutting the
            // corner and walking into the wall beside it.
            var doorway = _doorways.FirstOrDefault(d => d.Joins(previous, next));
            if (doorway != default)
            {
                var (x, z) = doorway.Middle;
                waypoints.Add(new Waypoint(x, z, null));
            }

            if (_map.For(next) is { } bounds)
                waypoints.Add(new Waypoint(bounds.CentreX, bounds.CentreZ, next));
        }

        // Already there: give a single waypoint so callers always have somewhere to stand.
        if (waypoints.Count == 0 && _map.For(to) is { } here)
            waypoints.Add(new Waypoint(here.CentreX, here.CentreZ, to));

        return waypoints;
    }

    /// <summary>
    /// Every adjoining pair in the graph has a doorway, and every doorway physically overlaps both
    /// rooms it claims to join. Without this a route can be computed that has no floor under it.
    /// </summary>
    public IReadOnlyList<string> Reconcile()
    {
        var problems = new List<string>();

        foreach (var place in _graph.All)
        foreach (var neighbour in _graph.Neighbours(place.Id))
        {
            // Each pair is seen twice; check one direction only.
            if (string.CompareOrdinal(place.Id, neighbour) > 0)
                continue;

            var doorway = _doorways.FirstOrDefault(d => d.Joins(place.Id, neighbour));
            if (doorway == default)
            {
                problems.Add($"'{place.Id}' and '{neighbour}' adjoin but have no doorway between them.");
                continue;
            }

            var a = _map.For(place.Id);
            var b = _map.For(neighbour);
            if (a is null || b is null)
                continue; // already reported by WorldMap.Reconcile

            if (!doorway.Bounds.Overlaps(a.Value) || !doorway.Bounds.Overlaps(b.Value))
                problems.Add(
                    $"The way between '{place.Id}' and '{neighbour}' does not reach both rooms — "
                    + "there is a hole in the floor.");
        }

        foreach (var doorway in _doorways)
        {
            if (!_graph.Adjoins(doorway.A, doorway.B))
                problems.Add($"There is a way between '{doorway.A}' and '{doorway.B}', but they do not adjoin.");
        }

        return problems;
    }
}
