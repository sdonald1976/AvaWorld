using AvaWorld.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// The world's half of the bargain: the companion says where, the world works out how. Routing
/// lives in the simulation so it can be checked without a display — which matters, because "she
/// walked into a wall" is otherwise only discoverable by watching.
/// </summary>
public class NavigationTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static Navigator Nav() => new(Cottage.Graph(), Cottage.Map(), Cottage.Doorways());

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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

    private static async Task<World> StartedAsync(IWorldStore store)
    {
        var world = new World(store, new Clock(Noon), NullLogger<World>.Instance);
        await world.StartAsync();
        await world.DefineLayoutAsync(Cottage.Graph(), Cottage.Spawn);
        return world;
    }

    // ---- routing ----

    [Fact]
    public void ARouteEndsAtItsDestination()
    {
        var route = Nav().RouteTo(Cottage.Hall, Cottage.Garden);

        Assert.NotEmpty(route);
        Assert.Equal(Cottage.Garden, route[^1].Place);
    }

    [Fact]
    public void ARoutePassesThroughTheDoorway_BeforeEnteringTheRoom()
    {
        // Aiming straight at the room centre cuts the corner and walks into the wall beside the
        // opening. The doorway has to come first.
        var route = Nav().RouteTo(Cottage.Hall, Cottage.Kitchen);
        var doorway = Cottage.Doorways().First(d => d.Joins(Cottage.Hall, Cottage.Kitchen));

        Assert.Equal(2, route.Count);
        Assert.Null(route[0].Place);
        Assert.Equal(doorway.Middle.X, route[0].X, 3);
        Assert.Equal(doorway.Middle.Z, route[0].Z, 3);
        Assert.Equal(Cottage.Kitchen, route[1].Place);
    }

    [Fact]
    public void EveryStepOfARoute_HasFloorUnderIt()
    {
        // Each waypoint must be inside either a room or the doorway leading to it. A waypoint over
        // nothing is a body walking into the void.
        var map = Cottage.Map();
        var doorways = Cottage.Doorways();
        var graph = Cottage.Graph();

        foreach (var from in graph.All)
        foreach (var to in graph.All)
        {
            foreach (var step in Nav().RouteTo(from.Id, to.Id))
            {
                var onFloor = map.PlaceAt(step.X, step.Z) is not null
                              || doorways.Any(d => d.Bounds.Contains(step.X, step.Z));
                Assert.True(onFloor, $"Step ({step.X}, {step.Z}) on the way from {from.Id} to {to.Id} has no floor.");
            }
        }
    }

    [Fact]
    public void TheLongWayRound_VisitsEveryRoomInOrder()
    {
        var route = Nav().RouteTo(Cottage.Study, Cottage.Garden);
        var rooms = route.Where(w => w.Place is not null).Select(w => w.Place).ToList();

        Assert.Equal(
            new[] { Cottage.Hall, Cottage.Kitchen, Cottage.Greenhouse, Cottage.Garden },
            rooms);
    }

    [Fact]
    public void RoutingToWhereSheStands_GivesSomewhereToStand()
    {
        var route = Nav().RouteTo(Cottage.Kitchen, Cottage.Kitchen);
        Assert.Single(route);
        Assert.Equal(Cottage.Kitchen, route[0].Place);
    }

    [Fact]
    public void AnUnreachableDestination_GivesNoRouteRatherThanAStraightLine()
    {
        // Walking hopefully in a straight line through a wall is the failure mode this prevents.
        var graph = Cottage.Graph().Add(new Place("cellar", "the cellar"));
        var map = Cottage.Map().Add(new PlaceBounds("cellar", 60f, 60f, 8f, 8f));

        Assert.Empty(new Navigator(graph, map, Cottage.Doorways()).RouteTo(Cottage.Hall, "cellar"));
    }

    [Fact]
    public void ADoorwayBetweenRoomsThatDoNotAdjoin_IsReported()
    {
        var doorways = Cottage.Doorways()
            .Append(new Doorway(Cottage.Hall, Cottage.Garden, new PlaceBounds("way:bogus", 0f, -17f, 4f, 4f)))
            .ToList();

        var problems = new Navigator(Cottage.Graph(), Cottage.Map(), doorways).Reconcile();

        Assert.Contains(problems, p => p.Contains("do not adjoin"));
    }

    // ---- destinations ----

    [Fact]
    public async Task SettingADestination_IsRememberedUntilSheArrives()
    {
        var world = await StartedAsync(new MemoryStore());
        await world.EnterAsync("ava", Cottage.Hall);

        Assert.True(await world.SetDestinationAsync("ava", Cottage.Garden));
        Assert.Equal(Cottage.Garden, world.DestinationOf("ava"));

        await world.EnterAsync("ava", Cottage.Kitchen);      // passing through
        Assert.Equal(Cottage.Garden, world.DestinationOf("ava"));

        await world.EnterAsync("ava", Cottage.Garden);       // arrived
        Assert.Null(world.DestinationOf("ava"));
    }

    [Fact]
    public async Task AskingForTheRoomSheIsAlreadyIn_IsNotAJourney()
    {
        var world = await StartedAsync(new MemoryStore());
        await world.EnterAsync("ava", Cottage.Study);

        Assert.False(await world.SetDestinationAsync("ava", Cottage.Study));
        Assert.Null(world.DestinationOf("ava"));
    }

    [Fact]
    public async Task SheIsStillOnHerWay_AfterARestart()
    {
        // An intention is part of what continuing means. Forgetting where she was going because
        // the process stopped would make the world a level again.
        var store = new MemoryStore();
        var first = await StartedAsync(store);
        await first.EnterAsync("ava", Cottage.Hall);
        await first.SetDestinationAsync("ava", Cottage.Greenhouse);

        var second = await StartedAsync(store);

        Assert.Equal(Cottage.Greenhouse, second.DestinationOf("ava"));
    }

    [Fact]
    public async Task AnUnknownDestination_IsRefused()
    {
        var world = await StartedAsync(new MemoryStore());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.SetDestinationAsync("ava", "the moon"));
    }
}
