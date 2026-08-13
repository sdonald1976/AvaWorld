using AvaWorld.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// The whole point of step one: a world that keeps existing when nothing is watching it, survives
/// being killed, and is honest about the time it was not running.
///
/// None of this needs Godot, a display, or a render loop — which is the structural claim the
/// project is making. If a test here ever needs one, the simulation has grown a dependency on
/// presentation and the design has broken.
/// </summary>
public class WorldTests
{
    private static readonly DateTimeOffset Morning = new(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now;
        public Clock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void Set(DateTimeOffset to) => _now = to;
    }

    private sealed class MemoryStore : IWorldStore
    {
        public WorldState? Saved { get; private set; }
        public int Saves { get; private set; }

        public Task<WorldState?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);

        public Task SaveAsync(WorldState state, CancellationToken ct = default)
        {
            // Round-trip through JSON so a test can't pass on a shared object reference that a
            // real store would never give back.
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            Saved = System.Text.Json.JsonSerializer.Deserialize<WorldState>(json);
            Saves++;
            return Task.CompletedTask;
        }
    }

    private static World Build(IWorldStore store, TimeProvider clock) =>
        new(store, clock, NullLogger<World>.Instance);

    [Fact]
    public async Task AFreshWorld_IsCreatedAndSaved()
    {
        var store = new MemoryStore();
        var world = Build(store, new Clock(Morning));

        var result = await world.StartAsync();

        Assert.True(result.Created);
        Assert.Null(result.Gap);
        Assert.Equal(Morning, world.State!.CreatedAt);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task TimePasses_WhileNothingIsWatching()
    {
        // The claim step one exists to prove: leave it running, come back, and the world agrees
        // that time went by.
        var clock = new Clock(Morning);
        var store = new MemoryStore();
        var world = Build(store, clock);
        await world.StartAsync();

        for (var i = 0; i < 8 * 60; i++) // eight hours, one tick a minute
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await world.TickAsync();
        }

        Assert.Equal(TimeSpan.FromHours(8), world.State!.Lived);
        Assert.Equal(Morning.AddHours(8), world.State.LastTickedAt);
        Assert.Empty(world.State.Gaps);
    }

    [Fact]
    public async Task TickRate_DoesNotChangeHowMuchTimePassed()
    {
        // Elapsed time is measured, never assumed, so a slow tick and a fast one must agree. This
        // is what makes the simulation safe to drive from a frame loop later.
        async Task<TimeSpan> LivedWith(TimeSpan interval, int ticks)
        {
            var clock = new Clock(Morning);
            var world = Build(new MemoryStore(), clock);
            await world.StartAsync();
            for (var i = 0; i < ticks; i++)
            {
                clock.Advance(interval);
                await world.TickAsync();
            }
            return world.State!.Lived;
        }

        var fast = await LivedWith(TimeSpan.FromSeconds(1), 3600);
        var slow = await LivedWith(TimeSpan.FromSeconds(60), 60);

        Assert.Equal(TimeSpan.FromHours(1), fast);
        Assert.Equal(TimeSpan.FromHours(1), slow);
    }

    [Fact]
    public async Task ARestart_RecordsTheGap_AndInventsNothingForIt()
    {
        var clock = new Clock(Morning);
        var store = new MemoryStore();

        var first = Build(store, clock);
        await first.StartAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await first.TickAsync();
        var livedBefore = first.State!.Lived;

        // The machine is off for eight hours.
        clock.Advance(TimeSpan.FromHours(8));

        var second = Build(store, clock);
        var result = await second.StartAsync();

        Assert.False(result.Created);
        Assert.NotNull(result.Gap);
        Assert.Equal(TimeSpan.FromHours(8), result.Gap!.Value.Duration);

        // The gap is recorded, and it is emphatically not lived: no history was generated for it.
        Assert.Equal(livedBefore, second.State!.Lived);
        Assert.Single(second.State.Gaps);
        Assert.Equal(TimeSpan.FromHours(8), second.TotalDowntime);
    }

    [Fact]
    public async Task ABriefRestart_IsNotTreatedAsDowntime()
    {
        // Relaunching the server takes seconds. That is not a gap in the world, and recording it as
        // one would fill the log with noise that means nothing.
        var clock = new Clock(Morning);
        var store = new MemoryStore();

        await Build(store, clock).StartAsync();
        clock.Advance(TimeSpan.FromSeconds(20));

        var result = await Build(store, clock).StartAsync();

        Assert.Null(result.Gap);
    }

    [Fact]
    public async Task WasRunningAt_DistinguishesQuietFromAbsent()
    {
        // "Nothing happened then" and "I have no record of then" are different claims, and only the
        // second one is true across a gap.
        var clock = new Clock(Morning);
        var store = new MemoryStore();

        var first = Build(store, clock);
        await first.StartAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await first.TickAsync();

        clock.Advance(TimeSpan.FromHours(8));
        var second = Build(store, clock);
        await second.StartAsync();

        // The gap runs from the last tick before shutdown (08:01) to the first after it (16:01),
        // so "after the gap" means the resumed present, not eight hours after the start.
        var gap = second.State!.Gaps.Single();
        Assert.True(second.WasRunningAt(Morning.AddSeconds(30)));    // before it
        Assert.False(second.WasRunningAt(Morning.AddHours(4)));      // inside it
        Assert.False(second.WasRunningAt(gap.To.AddSeconds(-1)));    // still inside, right at the end
        Assert.True(second.WasRunningAt(second.State.LastTickedAt)); // the resumed present
    }

    [Fact]
    public async Task AClockThatWentBackwards_DoesNotRewindTheWorld()
    {
        // A corrected system clock or a restored save must not produce negative time or a nonsense
        // gap. Resynchronise and carry on.
        var clock = new Clock(Morning);
        var store = new MemoryStore();
        var first = Build(store, clock);
        await first.StartAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await first.TickAsync();
        var lived = first.State!.Lived;

        clock.Set(Morning.AddHours(-3));
        var second = Build(store, clock);
        var result = await second.StartAsync();

        Assert.Null(result.Gap);
        Assert.Empty(second.State!.Gaps);
        Assert.Equal(lived, second.State.Lived);
    }

    [Fact]
    public async Task TickingBeforeStarting_IsAProgrammingError()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(new MemoryStore(), new Clock(Morning)).TickAsync());
}
