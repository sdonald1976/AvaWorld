using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaWorld.Wire;

/// <summary>
/// What the world says, and what it will listen to.
///
/// This is the contract the companion speaks, and the design's central boundary runs straight
/// through it: **intentions are goals, never motion**. The companion may say "go to the
/// greenhouse"; it may not say "move 0.1 metres east", and there is deliberately no message shape
/// that would let it. If coordinates ever cross this line, the boundary has already been broken.
///
/// JSON, newline-free, one message per WebSocket frame. Readable in a log at 2am is worth more
/// than compact here — this traffic is measured in messages per minute, not per frame.
/// </summary>
public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Largest frame accepted. A companion sending more than this is malfunctioning.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Json);
}

// ---- what the world says (perception) ----

/// <summary>
/// The opening message. Carries the menu of places, so the companion chooses from what exists
/// rather than remembering a layout that may have changed. It is told this on every connection
/// precisely so it never needs to store it.
/// </summary>
public sealed record Hello(
    string Type,
    string You,
    string? Place,
    IReadOnlyList<PlaceInfo> Places,
    IReadOnlyList<string> Actions)
{
    public Hello(string you, string? place, IReadOnlyList<PlaceInfo> places, IReadOnlyList<string> actions)
        : this("hello", you, place, places, actions) { }
}

/// <param name="Adjoins">Which places you can reach directly from here.</param>
/// <param name="Things">What is in here that needs looking after.</param>
public sealed record PlaceInfo(
    string Id, string Name, string Description, IReadOnlyList<string> Adjoins, IReadOnlyList<ThingInfo> Things);

/// <summary>Something in a place that changes over time, and how it is doing right now.</summary>
/// <param name="Condition">
/// The shared state — "fine", "dry", "wilting", "dead" — for deciding, not for saying. A stove is
/// never "dry".
/// </param>
/// <param name="Text">
/// The thing's own words for that state: "the stove is burning low". The world owns how its things
/// describe themselves, so the brain never has to invent phrasing for something it cannot see.
/// </param>
/// <param name="NeedsAttention">Whether tending it now would actually do something.</param>
public sealed record ThingInfo(string Id, string Name, string Condition, string Text, bool NeedsAttention);

/// <summary>
/// Something in the world changed condition, or somebody looked after it.
///
/// Carries the condition as well as the sentence, because the brain has to be able to *act* on
/// this and not merely read it. Without a machine-readable state, a thing that starts wanting
/// attention while she is already connected never becomes anything she can decide about — the
/// menu she was handed on connecting said it was fine, and nothing would ever say otherwise.
/// </summary>
public sealed record Noticed(
    string Type, string Place, string? Thing, string Text,
    string Condition, bool NeedsAttention, DateTimeOffset At)
{
    public Noticed(string place, string? thing, string text, string condition, bool needsAttention, DateTimeOffset at)
        : this("noticed", place, thing, text, condition, needsAttention, at) { }
}

/// <summary>A body entered a place. The bread and butter of perception.</summary>
public sealed record Arrived(string Type, string Body, string Place, DateTimeOffset At)
{
    public Arrived(string body, string place, DateTimeOffset at) : this("arrived", body, place, at) { }
}

/// <summary>Somebody came into or left the world. How she learns you are around.</summary>
public sealed record Presence(string Type, string Body, string State, string? Place, DateTimeOffset At)
{
    public Presence(string body, string state, string? place, DateTimeOffset at)
        : this("presence", body, state, place, at) { }
}

/// <summary>Something was asked for that the world cannot do. Always says why.</summary>
public sealed record Refusal(string Type, string Code, string Message)
{
    public Refusal(string code, string message) : this("refusal", code, message) { }
}

// ---- what the world listens to (intention) ----

/// <summary>
/// An instruction from the brain. One shape, because there are only a few verbs and a discriminated
/// union in JSON is more trouble than it is worth at this size.
/// </summary>
public sealed class Intention
{
    /// <summary>"auth", "goto", "tend", or "stop".</summary>
    public string Type { get; set; } = "";

    /// <summary>For "auth".</summary>
    public string? Token { get; set; }

    /// <summary>For "goto" — a place id from the menu in <see cref="Hello"/>.</summary>
    public string? Place { get; set; }

    /// <summary>
    /// For "tend" — the id of a thing from the menu. Still a goal, not an action: she says which
    /// thing wants looking after, and the world decides whether she is close enough to do it.
    /// </summary>
    public string? Thing { get; set; }

    public static Intention? Parse(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Intention>(json, Protocol.Json);
            return string.IsNullOrWhiteSpace(parsed?.Type) ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Why a request was refused. Stable strings — the companion may branch on these.</summary>
public static class RefusalCodes
{
    public const string Unauthenticated = "unauthenticated";
    public const string BadToken = "bad_token";
    public const string Malformed = "malformed";
    public const string UnknownPlace = "unknown_place";
    public const string Unreachable = "unreachable";
    public const string UnknownIntention = "unknown_intention";
    public const string UnknownThing = "unknown_thing";
    public const string NotHere = "not_here";
    public const string NothingToDo = "nothing_to_do";
}
