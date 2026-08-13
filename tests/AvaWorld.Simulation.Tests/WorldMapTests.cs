using AvaWorld.Simulation;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// Place resolution — deciding which room a body is standing in — is what the world believes about
/// where everyone is. It lives in the simulation rather than the engine so it can be checked
/// without a display, and so the geometry and the beliefs come from one source.
/// </summary>
public class WorldMapTests
{
    [Fact]
    public void APointInsideARoom_ResolvesToIt()
    {
        var map = Cottage.Map();
        Assert.Equal(Cottage.Hall, map.PlaceAt(0f, 0f));
        Assert.Equal(Cottage.Kitchen, map.PlaceAt(-16f, 2f));
        Assert.Equal(Cottage.Garden, map.PlaceAt(-16f, -34f));
    }

    [Fact]
    public void APointBetweenRooms_IsNowhereInParticular()
    {
        // Null is a real answer, not a failure: a body in a corridor should keep the room it came
        // from rather than have one invented for it.
        Assert.Null(Cottage.Map().PlaceAt(-8f, 0f));
    }

    [Fact]
    public void RoomEdges_Count_AsInside()
    {
        // A body standing exactly on the threshold is in the room, not nowhere. Otherwise walking
        // slowly through a doorway flickers.
        var hall = Cottage.Map().For(Cottage.Hall)!.Value;
        Assert.Equal(Cottage.Hall, Cottage.Map().PlaceAt(hall.MaxX, 0f));
        Assert.Equal(Cottage.Hall, Cottage.Map().PlaceAt(hall.MinX, hall.MinZ));
    }

    [Fact]
    public void TheStarterLayout_IsInternallyConsistent()
    {
        // Every place can be entered, every footprint names a real place, and no two rooms overlap.
        // Overlap would make place resolution depend on declaration order, which is the kind of
        // bug that presents as a haunting.
        Assert.Empty(Cottage.Map().Reconcile(Cottage.Graph()));
    }

    [Fact]
    public void TheStarterLayout_HasNoStrandedRooms()
        => Assert.Empty(Cottage.Graph().Unreachable(Cottage.Spawn));

    [Fact]
    public void EveryRoom_IsWalkableFromSpawn_ViaAdjoiningRooms()
    {
        var graph = Cottage.Graph();
        foreach (var place in graph.All)
            Assert.NotEmpty(graph.Route(Cottage.Spawn, place.Id));
    }

    [Fact]
    public void AMissingFootprint_IsReported()
    {
        var graph = Cottage.Graph().Add(new Place("cellar", "the cellar"));
        graph.Connect(Cottage.Hall, "cellar");

        var problems = Cottage.Map().Reconcile(graph);

        Assert.Contains(problems, p => p.Contains("cellar") && p.Contains("no bounds"));
    }

    [Fact]
    public void OverlappingRooms_AreReported()
    {
        var map = new WorldMap()
            .Add(new PlaceBounds("a", 0f, 0f, 10f, 10f))
            .Add(new PlaceBounds("b", 4f, 0f, 10f, 10f));

        Assert.Single(map.Overlaps());
    }

    [Fact]
    public void EveryDoorway_ReachesBothRoomsItClaimsToJoin()
    {
        // A doorway that only nearly meets a room is a hole in the floor between them, and no
        // amount of graph correctness would catch it.
        var problems = new Navigator(Cottage.Graph(), Cottage.Map(), Cottage.Doorways()).Reconcile();
        Assert.Empty(problems);
    }

    [Fact]
    public void ZeroSizedOrDuplicateBounds_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new WorldMap().Add(new PlaceBounds("a", 0, 0, 0, 5)));
        Assert.Throws<InvalidOperationException>(() => new WorldMap()
            .Add(new PlaceBounds("a", 0, 0, 5, 5))
            .Add(new PlaceBounds("A", 20, 20, 5, 5)));
    }
}
