using AvaWorld.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// Things that change whether or not anyone is looking.
///
/// This is what separates a world from a map, and the properties that matter are the awkward ones:
/// condition must follow from elapsed time alone (so an unwatched world and a replayed one agree),
/// only *changes* are worth telling her about, and neglect has to be able to cost something — a
/// plant that could always be revived is a chore, not a consequence.
/// </summary>
public class WorldObjectTests
{
    private static readonly DateTimeOffset Morning = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class MemoryStore : IWorldStore
    {
        public WorldState? Saved { get; private set; }
        public Task<WorldState?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);
        public Task SaveAsync(WorldState state, CancellationToken ct = default)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            Saved = System.Text.Json.JsonSerializer.Deserialize<WorldState>(json);
            return Task.CompletedTask;
        }
    }

    private static WorldObject Basil(DateTimeOffset created) => new()
    {
        Id = "basil", PlaceId = Cottage.Greenhouse, Name = "the basil",
        TendedVerb = "watered", Patience = TimeSpan.FromHours(8), CreatedAt = created,
    };

    private static async Task<World> StartedAsync(IWorldStore store, TimeProvider clock)
    {
        var world = new World(store, clock, NullLogger<World>.Instance);
        await world.StartAsync();
        await world.DefineLayoutAsync(Cottage.Graph(), Cottage.Spawn);
        await world.DefineObjectsAsync(Cottage.Things(clock.GetUtcNow()));
        return world;
    }

    // ---- condition follows from time alone ----

    [Theory]
    [InlineData(0, Condition.Fine)]
    [InlineData(7, Condition.Fine)]
    [InlineData(9, Condition.Dry)]
    [InlineData(17, Condition.Wilting)]
    [InlineData(25, Condition.Dead)]
    public void ItDriesOutOnItsOwnSchedule(int hours, Condition expected)
        => Assert.Equal(expected, Basil(Morning).ConditionAt(Morning.AddHours(hours)));

    [Fact]
    public void ConditionIsDerived_NotStored_SoAnUnwatchedWorldAgreesWithAReplayedOne()
    {
        // Nothing has to run for it to have got worse. That is what lets the world stop overnight
        // and still be honest in the morning.
        var basil = Basil(Morning);
        Assert.Equal(Condition.Fine, basil.ConditionAt(Morning));
        Assert.Equal(Condition.Wilting, basil.ConditionAt(Morning.AddHours(18)));
        Assert.Equal(Condition.Fine, basil.ConditionAt(Morning)); // asking again changed nothing
    }

    [Fact]
    public void WateringItStartsTheClockAgain()
    {
        var basil = Basil(Morning);
        var later = Morning.AddHours(10);

        Assert.Equal(Condition.Dry, basil.ConditionAt(later));
        Assert.True(basil.Tend(later));
        Assert.Equal(Condition.Fine, basil.ConditionAt(later));
        Assert.Equal(Condition.Dry, basil.ConditionAt(later.AddHours(9)));
    }

    [Fact]
    public void WateringSomethingThatIsFine_DoesNothing()
        => Assert.False(Basil(Morning).Tend(Morning.AddHours(2)));

    [Fact]
    public void OnceItIsDead_ItStaysDead()
    {
        // Neglect has to be able to cost something, or tending is a chore rather than a stake.
        var basil = Basil(Morning);
        var tooLate = Morning.AddHours(30);

        Assert.False(basil.Tend(tooLate));
        Assert.Equal(Condition.Dead, basil.ConditionAt(tooLate));
        Assert.Equal(Condition.Dead, basil.ConditionAt(tooLate.AddHours(1)));
        Assert.False(basil.Tend(tooLate.AddHours(1)));
    }

    // ---- only changes are news ----

    [Fact]
    public async Task ItIsOnlyWorthMentioningWhenSomethingChanges()
    {
        // A plant that has been dry for two days is not news twice, and a diary that said so every
        // tick would drown everything else she has to think about.
        var clock = new Clock(Morning);
        var world = await StartedAsync(new MemoryStore(), clock);

        Assert.Empty(await world.NoticeAsync());          // everything fine

        clock.Advance(TimeSpan.FromHours(9));
        var first = await world.NoticeAsync();
        Assert.Single(first);
        Assert.Contains("dry", first[0].Detail);

        Assert.Empty(await world.NoticeAsync());          // still dry — not news again

        clock.Advance(TimeSpan.FromHours(8));
        Assert.Single(await world.NoticeAsync());         // now wilting — news
    }

    [Fact]
    public async Task ThingsSurviveARestart_InTheStateTheyWereLeft()
    {
        // A plant that reset itself on restart would be scenery, and nobody would ever have to
        // remember it.
        var store = new MemoryStore();
        var clock = new Clock(Morning);
        var first = await StartedAsync(store, clock);
        Assert.Equal(2, first.Objects.Count);

        clock.Advance(TimeSpan.FromHours(10));
        var second = await StartedAsync(store, clock);

        Assert.Equal(2, second.Objects.Count);            // not duplicated by re-defining
        Assert.Equal(Condition.Dry, second.Objects.First(o => o.Id == "basil").ConditionAt(clock.GetUtcNow()));
    }

    // ---- tending needs you to be there ----

    [Fact]
    public async Task SheHasToBeInTheRoom_ToLookAfterSomething()
    {
        // Watering the greenhouse basil from the study would make places decorative, and the whole
        // point of them is that being somewhere matters.
        var clock = new Clock(Morning);
        var world = await StartedAsync(new MemoryStore(), clock);
        await world.EnterAsync("ava", Cottage.Study);
        clock.Advance(TimeSpan.FromHours(9));

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.TendAsync("ava", "basil"));

        await world.EnterAsync("ava", Cottage.Greenhouse);
        var tended = await world.TendAsync("ava", "basil");

        Assert.NotNull(tended);
        Assert.Equal(WorldEventKind.Tended, tended!.Kind);
        Assert.Contains("watered", tended.Detail);
    }

    [Fact]
    public async Task TendingSomethingThatDoesNotExist_IsRefused()
    {
        var world = await StartedAsync(new MemoryStore(), new Clock(Morning));
        await world.EnterAsync("ava", Cottage.Greenhouse);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.TendAsync("ava", "orchid"));
    }

    [Fact]
    public async Task AnObjectInAPlaceTheLayoutLacks_IsSkippedRatherThanStranded()
    {
        var world = new World(new MemoryStore(), new Clock(Morning), NullLogger<World>.Instance);
        await world.StartAsync();
        await world.DefineLayoutAsync(Cottage.Graph(), Cottage.Spawn);

        await world.DefineObjectsAsync(new[]
        {
            new WorldObject
            {
                Id = "anvil", PlaceId = "forge", Name = "the anvil", TendedVerb = "oiled",
                Patience = TimeSpan.FromHours(8), CreatedAt = Morning,
            },
        });

        Assert.Empty(world.Objects);
    }
}
