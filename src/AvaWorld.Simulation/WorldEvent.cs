namespace AvaWorld.Simulation;

/// <summary>Kinds of thing the world records. Deliberately few — this list should grow slowly.</summary>
public enum WorldEventKind
{
    /// <summary>A body entered a place.</summary>
    Arrived,

    /// <summary>A body left the world entirely (disconnected).</summary>
    Left,

    /// <summary>A body entered the world.</summary>
    Joined,

    /// <summary>Something in a place changed condition — the first thing worth telling her about.</summary>
    Noticed,

    /// <summary>Somebody looked after something.</summary>
    Tended,
}

/// <summary>
/// Something that happened, where, and when.
///
/// This is the spine of the whole design: it is what the companion's reflection eventually reads,
/// and the reason she can answer "what have you been up to?" with something true instead of
/// something plausible. Which is also why nothing may ever append to it speculatively — an event
/// exists because the world did it, never because it would be nice if it had.
/// </summary>
/// <param name="At">When, in wall-clock time.</param>
/// <param name="Kind">What sort of thing happened.</param>
/// <param name="Body">Who it happened to.</param>
/// <param name="Place">Where, if the event has a location.</param>
/// <param name="Detail">
/// What happened, when the event is about a thing rather than a body — "the basil is looking dry".
/// </param>
public sealed record WorldEvent(
    DateTimeOffset At, WorldEventKind Kind, string Body, string? Place, string? Detail = null)
{
    /// <summary>A plain-language rendering. Not for the user — for logs and, later, for perception.</summary>
    public string Describe() => Kind switch
    {
        WorldEventKind.Arrived => $"{Body} arrived in the {Place}",
        WorldEventKind.Joined => Place is null ? $"{Body} appeared" : $"{Body} appeared in the {Place}",
        WorldEventKind.Left => $"{Body} left",
        WorldEventKind.Noticed => $"In the {Place}, {Detail}",
        WorldEventKind.Tended => $"{Body} {Detail} in the {Place}",
        _ => $"{Body}: {Kind}",
    };
}
