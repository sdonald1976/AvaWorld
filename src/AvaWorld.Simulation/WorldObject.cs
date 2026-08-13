namespace AvaWorld.Simulation;

/// <summary>How a thing that needs looking after is doing.</summary>
public enum Condition
{
    /// <summary>Recently tended. Nothing to do.</summary>
    Fine,

    /// <summary>Beginning to need attention. The first thing worth mentioning.</summary>
    Dry,

    /// <summary>Visibly suffering. Still recoverable.</summary>
    Wilting,

    /// <summary>Too late. Tending it now does nothing.</summary>
    Dead,
}

/// <summary>
/// How a particular thing says it is doing.
///
/// Because "the stove is looking dry" is wrong, and a world whose things all decline in the
/// vocabulary of a houseplant reads as one object wearing different names. The states are shared;
/// the words for them belong to the thing.
/// </summary>
public sealed record ConditionWords(string Fine, string Dry, string Wilting, string Dead)
{
    public static ConditionWords Plant { get; } =
        new("is doing well", "is looking dry", "is wilting", "has died");

    public static ConditionWords Fire { get; } =
        new("is well banked", "is burning low", "is nearly out", "has gone out");

    public string For(Condition condition) => condition switch
    {
        Condition.Fine => Fine,
        Condition.Dry => Dry,
        Condition.Wilting => Wilting,
        _ => Dead,
    };
}

/// <summary>
/// A thing in a place that changes over time.
///
/// This is what separates a world from a map. Rooms you can walk between are a diagram; a plant
/// that dries out over days, that she can water, that dies if nobody does, is somewhere with
/// consequences — and consequence is the whole argument for the world existing. The design puts it
/// plainly: what makes a world feel inhabited is the basil you saw wilting yesterday being dead
/// today, not how many rooms there are.
///
/// Its condition is a pure function of how long since it was tended, so it advances correctly
/// whether the world was watched, unwatched, or asleep — the same property the clock has.
/// </summary>
public sealed class WorldObject
{
    public required string Id { get; init; }

    /// <summary>Which place it is in. Objects do not move.</summary>
    public required string PlaceId { get; set; }

    /// <summary>What it is called, in her words: "the basil".</summary>
    public required string Name { get; set; }

    /// <summary>What tending it is called: "watered".</summary>
    public required string TendedVerb { get; set; }

    /// <summary>How it describes its own decline. Authored, not accumulated.</summary>
    public ConditionWords Words { get; set; } = ConditionWords.Plant;

    /// <summary>When it was last looked after. Null means never — it starts its life fine.</summary>
    public DateTimeOffset? TendedAt { get; set; }

    /// <summary>When it came into existence, so an untended object still ages.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Once dead it stays dead. Recorded so tending cannot quietly revive it.</summary>
    public bool Died { get; set; }

    /// <summary>How long it can be left before it starts to suffer.</summary>
    public required TimeSpan Patience { get; set; }

    /// <summary>The condition last reported, so only changes are news.</summary>
    public Condition LastReported { get; set; } = Condition.Fine;

    /// <summary>
    /// How it is doing now. Pure: derived from elapsed time, never stored, so an unwatched world
    /// and a replayed one agree.
    ///
    /// The thresholds are multiples of <see cref="Patience"/> rather than absolute times, so a
    /// thing that wants daily attention and one that wants weekly attention behave the same way at
    /// their own pace.
    /// </summary>
    public Condition ConditionAt(DateTimeOffset now)
    {
        if (Died)
            return Condition.Dead;

        var since = now - (TendedAt ?? CreatedAt);
        if (since < Patience)
            return Condition.Fine;
        if (since < Patience * 2)
            return Condition.Dry;
        return since < Patience * 3 ? Condition.Wilting : Condition.Dead;
    }

    /// <summary>
    /// Looks after it. Returns false when there was no point — it was already fine, or it is dead
    /// and tending it now is a kindness to nobody.
    /// </summary>
    public bool Tend(DateTimeOffset now)
    {
        if (Died || ConditionAt(now) == Condition.Dead)
        {
            Died = true;
            return false;
        }

        if (ConditionAt(now) == Condition.Fine)
            return false;

        TendedAt = now;
        LastReported = Condition.Fine;
        return true;
    }

    /// <summary>A sentence about how it is, for the log and for her perception.</summary>
    public string Describe(DateTimeOffset now) => $"{Name} {Words.For(ConditionAt(now))}";
}
