namespace AvaWorld.Simulation;

/// <summary>
/// A named location in the world. Deliberately abstract: no geometry, no coordinates, no size.
/// Those belong to the presentation layer, which realises a place as actual space.
///
/// The companion only ever deals in these names — it asks to go to "greenhouse", and the world
/// works out what that means in metres. That is the boundary rule from the design doc: intentions
/// are goals, never motion.
/// </summary>
/// <param name="Id">Stable identifier, used on the wire and in saves. Lowercase, no spaces.</param>
/// <param name="Name">What it is called out loud.</param>
/// <param name="Description">A sentence the companion could say about being here.</param>
public sealed record Place(string Id, string Name, string Description = "");

/// <summary>
/// Which places exist and which of them adjoin each other.
///
/// The graph is registered at startup rather than persisted, because it is authored — it comes
/// from the world's layout, and a save file that disagreed with the current layout would be worse
/// than no save at all. What persists is where everyone <em>is</em>, which is checked against the
/// graph on load.
/// </summary>
public sealed class PlaceGraph
{
    private readonly Dictionary<string, Place> _places = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _adjacency = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Place> All => _places.Values;

    public int Count => _places.Count;

    public bool Contains(string placeId) => _places.ContainsKey(placeId);

    public Place? Get(string placeId) => _places.GetValueOrDefault(placeId);

    public PlaceGraph Add(Place place)
    {
        if (string.IsNullOrWhiteSpace(place.Id))
            throw new ArgumentException("A place needs an id.", nameof(place));
        if (_places.ContainsKey(place.Id))
            throw new InvalidOperationException($"Place '{place.Id}' is already defined.");

        _places[place.Id] = place;
        _adjacency[place.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return this;
    }

    /// <summary>Marks two places as adjoining. Symmetric — a door leads both ways.</summary>
    public PlaceGraph Connect(string a, string b)
    {
        if (!_places.ContainsKey(a)) throw new InvalidOperationException($"Unknown place '{a}'.");
        if (!_places.ContainsKey(b)) throw new InvalidOperationException($"Unknown place '{b}'.");
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A place cannot adjoin itself.");

        _adjacency[a].Add(b);
        _adjacency[b].Add(a);
        return this;
    }

    public IReadOnlyCollection<string> Neighbours(string placeId) =>
        _adjacency.TryGetValue(placeId, out var set) ? set : Array.Empty<string>();

    public bool Adjoins(string a, string b) =>
        _adjacency.TryGetValue(a, out var set) && set.Contains(b);

    /// <summary>
    /// Shortest route between two places, inclusive of both ends, or empty if unreachable.
    ///
    /// This is route-finding over rooms, not pathfinding over ground — the engine owns the second
    /// one. It exists so the world can answer "can she get there at all, and roughly how far is
    /// it" without a navmesh, and so a disconnected place is a loud failure rather than a body
    /// that quietly never arrives.
    /// </summary>
    public IReadOnlyList<string> Route(string from, string to)
    {
        if (!Contains(from) || !Contains(to))
            return Array.Empty<string>();
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return new[] { from };

        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in _adjacency[current])
            {
                if (!seen.Add(next))
                    continue;
                previous[next] = current;
                if (string.Equals(next, to, StringComparison.OrdinalIgnoreCase))
                    return Rebuild(previous, from, to);
                queue.Enqueue(next);
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Places that cannot be reached from <paramref name="origin"/>. A layout that strands a room
    /// is an authoring mistake, and one that is invisible until someone is asked to walk there.
    /// </summary>
    public IReadOnlyList<string> Unreachable(string origin) =>
        _places.Keys.Where(id => Route(origin, id).Count == 0).ToList();

    private static IReadOnlyList<string> Rebuild(
        Dictionary<string, string> previous, string from, string to)
    {
        var path = new List<string> { to };
        var cursor = to;
        while (previous.TryGetValue(cursor, out var step))
        {
            path.Add(step);
            cursor = step;
            if (string.Equals(step, from, StringComparison.OrdinalIgnoreCase))
                break;
        }
        path.Reverse();
        return path;
    }
}
