namespace AvaWorld.Simulation;

/// <summary>
/// The starter world: four rooms off a central hall, plus the corridors between them.
///
/// Small on purpose. What makes a world feel inhabited is consequence and persistence — the basil
/// you saw wilting yesterday being dead today — not extent. Four rooms is enough to be somewhere,
/// enough to have a reason to move, and few enough that every one of them can be given a reason to
/// exist before another is added.
///
/// Both halves come from here: <see cref="Graph"/> is what the world believes, <see cref="Map"/>
/// is where those places physically are. The engine builds floors from the second and the server
/// resolves rooms from it, so the two cannot drift apart.
/// </summary>
public static class Cottage
{
    public const string Hall = "hall";
    public const string Kitchen = "kitchen";
    public const string Study = "study";
    public const string Greenhouse = "greenhouse";
    public const string Garden = "garden";

    /// <summary>Where a body goes when it has nowhere else to be.</summary>
    public const string Spawn = Hall;

    public static PlaceGraph Graph() =>
        new PlaceGraph()
            .Add(new Place(Hall, "the hall",
                "A plain landing with doors off it. Everything connects through here."))
            .Add(new Place(Kitchen, "the kitchen",
                "Warm, and the first place to end up in without deciding to."))
            .Add(new Place(Study, "the study",
                "Quiet, with a desk. Where work happens and thinking gets done."))
            .Add(new Place(Greenhouse, "the greenhouse",
                "Glass and damp earth. Things grow here, and need attending to."))
            .Add(new Place(Garden, "the garden",
                "Outside, past the greenhouse. Open sky."))
            .Connect(Hall, Kitchen)
            .Connect(Hall, Study)
            .Connect(Kitchen, Greenhouse)
            .Connect(Greenhouse, Garden);

    /// <summary>
    /// Footprints in metres. Rooms are separated by gaps that the corridors span, so a body
    /// between rooms is briefly nowhere — which the world treats as "still in the last room"
    /// rather than as having left the world.
    /// </summary>
    public static WorldMap Map() =>
        new WorldMap()
            .Add(new PlaceBounds(Hall, 0f, 0f, 10f, 10f))
            .Add(new PlaceBounds(Kitchen, -16f, 0f, 12f, 10f))
            .Add(new PlaceBounds(Study, 16f, 0f, 12f, 10f))
            .Add(new PlaceBounds(Greenhouse, -16f, -16f, 12f, 12f))
            .Add(new PlaceBounds(Garden, -16f, -34f, 16f, 16f));

    /// <summary>
    /// The corridors, as floor strips joining room edges. Purely physical — the world has no
    /// concept of a corridor, only of rooms that adjoin.
    ///
    /// Each strip deliberately <em>overlaps</em> the two rooms it joins by a metre rather than
    /// meeting them exactly. Abutting floors that share an edge coordinate leave a hairline crack
    /// once floats are involved, and a body can fall through it; an overlap cannot.
    ///
    /// They are not in <see cref="Map"/>, so standing in one resolves to no place at all. That is
    /// intended — a body between rooms keeps the room it came from.
    /// </summary>
    public static IReadOnlyList<PlaceBounds> Corridors() => new[]
    {
        new PlaceBounds("corridor:hall-kitchen", -7.5f, 0f, 7f, 4f),
        new PlaceBounds("corridor:hall-study", 7.5f, 0f, 7f, 4f),
        new PlaceBounds("corridor:kitchen-greenhouse", -16f, -7.5f, 4f, 7f),
        new PlaceBounds("corridor:greenhouse-garden", -16f, -24f, 4f, 6f),
    };
}
