namespace AvaWorld.Simulation;

/// <summary>
/// A place's footprint on the ground: an axis-aligned rectangle, centred on
/// (<paramref name="CentreX"/>, <paramref name="CentreZ"/>). Y is ignored — the world is currently
/// one storey, and pretending otherwise would be modelling a problem nobody has yet.
///
/// Metres, matching the engine's units, with Z as the second ground axis (Godot is Y-up).
/// </summary>
public readonly record struct PlaceBounds(string PlaceId, float CentreX, float CentreZ, float Width, float Depth)
{
    public float MinX => CentreX - Width / 2f;
    public float MaxX => CentreX + Width / 2f;
    public float MinZ => CentreZ - Depth / 2f;
    public float MaxZ => CentreZ + Depth / 2f;

    public bool Contains(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    public bool Overlaps(PlaceBounds other) =>
        MinX < other.MaxX && MaxX > other.MinX && MinZ < other.MaxZ && MaxZ > other.MinZ;
}

/// <summary>
/// Where the places are, as opposed to how they connect. One source of truth for two consumers:
/// the engine builds floors and walls from it, and the server resolves "which room is this body
/// standing in" from it.
///
/// This lives in the simulation rather than the engine deliberately. Point-in-rectangle is not a
/// rendering concern, and having it here means place resolution — the thing that decides what the
/// world believes about where everyone is — is testable without a display.
/// </summary>
public sealed class WorldMap
{
    private readonly List<PlaceBounds> _bounds = new();

    public IReadOnlyList<PlaceBounds> Bounds => _bounds;

    public WorldMap Add(PlaceBounds bounds)
    {
        if (string.IsNullOrWhiteSpace(bounds.PlaceId))
            throw new ArgumentException("Bounds need a place id.", nameof(bounds));
        if (bounds.Width <= 0 || bounds.Depth <= 0)
            throw new ArgumentException($"Place '{bounds.PlaceId}' has no area.", nameof(bounds));
        if (_bounds.Any(b => string.Equals(b.PlaceId, bounds.PlaceId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Place '{bounds.PlaceId}' already has bounds.");

        _bounds.Add(bounds);
        return this;
    }

    public PlaceBounds? For(string placeId) =>
        _bounds.Any(b => string.Equals(b.PlaceId, placeId, StringComparison.OrdinalIgnoreCase))
            ? _bounds.First(b => string.Equals(b.PlaceId, placeId, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>
    /// Which place a point is in, or null if it is between rooms. Null is a real answer — a body
    /// in a corridor is nowhere in particular, and the caller should keep its previous place
    /// rather than invent one.
    /// </summary>
    public string? PlaceAt(float x, float z)
    {
        foreach (var b in _bounds)
        {
            if (b.Contains(x, z))
                return b.PlaceId;
        }
        return null;
    }

    /// <summary>
    /// Rooms whose footprints overlap. An authoring mistake that makes place resolution depend on
    /// declaration order, which is exactly the kind of bug that looks like a haunting.
    /// </summary>
    public IReadOnlyList<(string A, string B)> Overlaps()
    {
        var clashes = new List<(string, string)>();
        for (var i = 0; i < _bounds.Count; i++)
        for (var j = i + 1; j < _bounds.Count; j++)
        {
            if (_bounds[i].Overlaps(_bounds[j]))
                clashes.Add((_bounds[i].PlaceId, _bounds[j].PlaceId));
        }
        return clashes;
    }

    /// <summary>
    /// Every place in the graph has a footprint, and every footprint names a real place. Called at
    /// startup so a mismatch is a loud failure rather than a room you can walk into that the world
    /// does not believe exists.
    /// </summary>
    public IReadOnlyList<string> Reconcile(PlaceGraph graph)
    {
        var problems = new List<string>();

        foreach (var place in graph.All)
        {
            if (For(place.Id) is null)
                problems.Add($"Place '{place.Id}' has no bounds — it cannot be entered.");
        }

        foreach (var b in _bounds)
        {
            if (!graph.Contains(b.PlaceId))
                problems.Add($"Bounds name '{b.PlaceId}', which is not a place in the layout.");
        }

        foreach (var (a, b) in Overlaps())
            problems.Add($"Places '{a}' and '{b}' overlap; which one a body is in would depend on ordering.");

        return problems;
    }
}
