using AvaWorld.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// Places as the world understands them: names and adjacency, no geometry. The companion only ever
/// deals in these names, so this is the vocabulary the whole system shares — and none of it needs
/// Godot to be true.
/// </summary>
public class PlaceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

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

    /// <summary>The starter layout: a short chain with one branch.</summary>
    private static PlaceGraph Cottage() =>
        new PlaceGraph()
            .Add(new Place("kitchen", "the kitchen"))
            .Add(new Place("hallway", "the hallway"))
            .Add(new Place("study", "the study"))
            .Add(new Place("greenhouse", "the greenhouse"))
            .Connect("kitchen", "hallway")
            .Connect("hallway", "study")
            .Connect("kitchen", "greenhouse");

    private static async Task<World> StartedAsync(IWorldStore store, TimeProvider clock)
    {
        var world = new World(store, clock, NullLogger<World>.Instance);
        await world.StartAsync();
        await world.DefineLayoutAsync(Cottage(), "kitchen");
        return world;
    }

    // ---- the graph ----

    [Fact]
    public void Connections_AreSymmetric()
    {
        var g = Cottage();
        Assert.True(g.Adjoins("kitchen", "hallway"));
        Assert.True(g.Adjoins("hallway", "kitchen"));
    }

    [Fact]
    public void PlaceIds_AreCaseInsensitive()
    {
        // The companion will send these as text. "Greenhouse" and "greenhouse" are the same room.
        var g = Cottage();
        Assert.True(g.Contains("GREENHOUSE"));
        Assert.True(g.Adjoins("Kitchen", "Hallway"));
    }

    [Fact]
    public void DuplicateOrSelfConnected_PlacesAreRejected()
    {
        var g = Cottage();
        Assert.Throws<InvalidOperationException>(() => g.Add(new Place("kitchen", "again")));
        Assert.Throws<InvalidOperationException>(() => g.Connect("kitchen", "kitchen"));
        Assert.Throws<InvalidOperationException>(() => g.Connect("kitchen", "cellar"));
    }

    [Fact]
    public void Route_FindsTheShortestWayThrough()
    {
        var route = Cottage().Route("greenhouse", "study");
        Assert.Equal(new[] { "greenhouse", "kitchen", "hallway", "study" }, route);
    }

    [Fact]
    public void Route_ToWhereYouAlreadyAre_IsJustHere()
        => Assert.Equal(new[] { "kitchen" }, Cottage().Route("kitchen", "kitchen"));

    [Fact]
    public void AStrandedRoom_IsReportedRatherThanSilentlyUnreachable()
    {
        // An authoring mistake that is otherwise invisible until someone is asked to walk there
        // and simply never arrives.
        var g = Cottage().Add(new Place("cellar", "the cellar"));

        Assert.Empty(g.Route("kitchen", "cellar"));
        Assert.Equal(new[] { "cellar" }, g.Unreachable("kitchen"));
        Assert.Empty(Cottage().Unreachable("kitchen"));
    }

    // ---- occupancy ----

    [Fact]
    public async Task EnteringAPlace_IsRecordedOnceNotEveryFrame()
    {
        // A physics volume fires continuously; only the transition is news.
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));

        Assert.True(await world.EnterAsync("ava", "greenhouse"));
        Assert.False(await world.EnterAsync("ava", "greenhouse"));
        Assert.True(await world.EnterAsync("ava", "kitchen"));

        Assert.Equal("kitchen", world.PlaceOf("ava"));
        Assert.Equal(2, world.RecentEvents().Count);
    }

    [Fact]
    public async Task TheFirstAppearance_IsJoining_AndLaterOnesAreArriving()
    {
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));

        await world.EnterAsync("scott", "kitchen");
        await world.EnterAsync("scott", "hallway");

        var events = world.RecentEvents();
        Assert.Equal(WorldEventKind.Joined, events[0].Kind);
        Assert.Equal(WorldEventKind.Arrived, events[1].Kind);
        Assert.Contains("hallway", events[1].Describe());
    }

    [Fact]
    public async Task Occupants_ReportsWhoIsSharingARoom()
    {
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));

        await world.EnterAsync("ava", "study");
        await world.EnterAsync("scott", "study");

        Assert.Equal(new[] { "ava", "scott" }, world.Occupants("study"));
        Assert.Empty(world.Occupants("kitchen"));
    }

    [Fact]
    public async Task LeavingRemovesYou_ButTheHistoryRemains()
    {
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));
        await world.EnterAsync("scott", "kitchen");

        Assert.True(await world.LeaveAsync("scott"));
        Assert.False(await world.LeaveAsync("scott"));

        Assert.Null(world.PlaceOf("scott"));
        Assert.Contains(world.RecentEvents(), e => e.Kind == WorldEventKind.Left);
    }

    [Fact]
    public async Task AnUnknownPlace_IsRefused()
    {
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.EnterAsync("ava", "cellar"));
    }

    // ---- across a restart ----

    [Fact]
    public async Task SheIsWhereSheWas_AfterARestart()
    {
        // She lives here. Teleporting to a spawn point on every launch would make the world a
        // level rather than a place.
        var store = new MemoryStore();
        var clock = new Clock(Noon);

        var first = await StartedAsync(store, clock);
        await first.EnterAsync("ava", "greenhouse");

        clock.Advance(TimeSpan.FromHours(9));
        var second = await StartedAsync(store, clock);

        Assert.Equal("greenhouse", second.PlaceOf("ava"));
    }

    [Fact]
    public async Task ARoomThatVanishedFromTheLayout_DoesNotStrandHer()
    {
        // The layout is authored and can change under a saved world. Leaving her pointing at a
        // room that no longer exists would be a body in nowhere.
        var store = new MemoryStore();
        var clock = new Clock(Noon);
        var first = await StartedAsync(store, clock);
        await first.EnterAsync("ava", "greenhouse");

        var reduced = new PlaceGraph()
            .Add(new Place("kitchen", "the kitchen"))
            .Add(new Place("hallway", "the hallway"))
            .Connect("kitchen", "hallway");

        var second = new World(store, clock, NullLogger<World>.Instance);
        await second.StartAsync();
        await second.DefineLayoutAsync(reduced, "kitchen");

        Assert.Equal("kitchen", second.PlaceOf("ava"));
    }

    [Fact]
    public async Task TheEventLog_IsCapped()
    {
        // The save file is rewritten every tick; an unbounded array would make that slower for as
        // long as the world exists.
        var world = await StartedAsync(new MemoryStore(), new Clock(Noon));

        for (var i = 0; i < World.MaxRetainedEvents + 50; i++)
            await world.EnterAsync("ava", i % 2 == 0 ? "kitchen" : "hallway");

        Assert.Equal(World.MaxRetainedEvents, world.State!.Events.Count);
        // The newest survive; the oldest are dropped.
        Assert.Equal("hallway", world.State.Events[^1].Place);
    }
}
