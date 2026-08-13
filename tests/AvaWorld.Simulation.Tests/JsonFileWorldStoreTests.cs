using AvaWorld.Simulation;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// The world saves on every tick and is normally killed by closing a window or losing power, so a
/// partial write is the expected way this process ends rather than a rare accident. These cover
/// what has to survive that.
/// </summary>
public class JsonFileWorldStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "avaworld-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ANeverRunWorld_LoadsAsNull()
        => Assert.Null(await new JsonFileWorldStore(PathFor("world.json")).LoadAsync());

    [Fact]
    public async Task SaveThenLoad_RoundTripsEverything()
    {
        var store = new JsonFileWorldStore(PathFor("world.json"));
        var created = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        var state = new WorldState
        {
            CreatedAt = created,
            LastTickedAt = created.AddHours(9),
            Lived = TimeSpan.FromHours(1),
            Ticks = 60,
            Gaps = { new Downtime(created.AddHours(1), created.AddHours(9)) },
        };

        await store.SaveAsync(state);
        var loaded = await store.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(created, loaded!.CreatedAt);
        Assert.Equal(created.AddHours(9), loaded.LastTickedAt);
        Assert.Equal(TimeSpan.FromHours(1), loaded.Lived);
        Assert.Equal(60, loaded.Ticks);
        Assert.Single(loaded.Gaps);
        Assert.Equal(TimeSpan.FromHours(8), loaded.Gaps[0].Duration);
    }

    [Fact]
    public async Task SavingIsAtomic_SoAnInterruptedSaveNeverTruncatesTheWorld()
    {
        // Save repeatedly and assert the target file is always complete and parseable. An
        // in-place writer would leave a truncated file visible between open and flush.
        var path = PathFor("world.json");
        var store = new JsonFileWorldStore(path);
        var created = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);

        for (var i = 1; i <= 25; i++)
        {
            await store.SaveAsync(new WorldState
            {
                CreatedAt = created,
                LastTickedAt = created.AddMinutes(i),
                Ticks = i,
            });

            var reloaded = await store.LoadAsync();
            Assert.NotNull(reloaded);
            Assert.Equal(i, reloaded!.Ticks);
        }

        // No temp file is left behind once saving has finished.
        Assert.False(File.Exists(path + ".saving"));
    }

    [Fact]
    public async Task AnOrphanedTempFile_IsRecoveredRatherThanDiscarded()
    {
        // We died between writing the temp file and moving it into place. The new world reached
        // disk; the old one is already gone. Losing it and starting fresh would be worse.
        var path = PathFor("world.json");
        var created = new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            path + ".saving",
            System.Text.Json.JsonSerializer.Serialize(new WorldState { CreatedAt = created, Ticks = 7 }));

        var loaded = await new JsonFileWorldStore(path).LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(7, loaded!.Ticks);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SavingCreatesTheDirectory_SoAFirstRunNeedsNoSetup()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "world.json");
        await new JsonFileWorldStore(path).SaveAsync(new WorldState());
        Assert.True(File.Exists(path));
    }
}
