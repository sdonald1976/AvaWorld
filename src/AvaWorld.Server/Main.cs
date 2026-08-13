using AvaWorld.Simulation;
using Godot;
using Microsoft.Extensions.Logging;

namespace AvaWorld.Server;

/// <summary>
/// The Godot end of the world server. Deliberately thin.
///
/// Everything this class does is scheduling and I/O: bring the simulation up, advance it on an
/// interval, and shut it down cleanly. All world logic lives in AvaWorld.Simulation, which cannot
/// reference Godot at all. If this file starts making decisions about the world, the split has
/// begun to rot — the rule is that the server produces identical history with no client connected,
/// and logic living in a Node is how that stops being true.
/// </summary>
public partial class Main : Node
{
    /// <summary>
    /// How often the simulation is advanced. Not a frame rate — elapsed time is measured rather
    /// than assumed, so this only decides how promptly the world notices time passing, and how
    /// often it saves. Once a second is generous for a world with no moving parts yet.
    /// </summary>
    private const double TickSeconds = 1.0;

    private World _world = null!;
    private ILogger<Main> _log = null!;
    private double _sinceLastTick;
    private bool _ticking;

    public override void _Ready()
    {
        var factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new GodotLoggerProvider()));
        _log = factory.CreateLogger<Main>();

        var path = WorldFilePath();
        _world = new World(new JsonFileWorldStore(path), TimeProvider.System, factory.CreateLogger<World>());

        _log.LogInformation("World file: {Path}", path);

        // Godot installs a synchronization context on the main thread, so blocking here on a task
        // whose continuation wants that same thread deadlocks the server before it ever starts.
        // Task.Run moves the whole chain onto the thread pool; the brief block is fine at startup,
        // and the world must be up before the first tick regardless.
        var result = Task.Run(() => _world.StartAsync()).GetAwaiter().GetResult();
        if (result.Created)
            _log.LogInformation("A new world begins.");
        else if (result.Gap is { } gap)
            _log.LogInformation("Resumed after {Duration} away. That time has no history.", gap.Duration);
        else
            _log.LogInformation("Resumed. Lived {Lived} so far.", _world.State!.Lived);
    }

    public override void _Process(double delta)
    {
        _sinceLastTick += delta;
        if (_sinceLastTick < TickSeconds || _ticking)
            return;
        _sinceLastTick = 0;

        // The save is async and the frame loop is not. Skip rather than queue if a tick is still
        // in flight: the next one measures the full elapsed time anyway, so nothing is lost.
        _ticking = true;
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            await _world.TickAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tick failed; the world keeps running.");
        }
        finally
        {
            _ticking = false;
        }
    }

    public override void _Notification(int what)
    {
        // Closing the window or SIGINT. One last tick so the saved LastTickedAt is the moment we
        // actually stopped, which keeps the recorded downtime honest instead of counting the last
        // second of runtime as part of the gap.
        if (what is (int)NotificationWMCloseRequest or (int)NotificationPredelete)
        {
            try
            {
                Task.Run(() => _world.TickAsync()).GetAwaiter().GetResult();
                _log.LogInformation("World saved on shutdown. Lived {Lived}.", _world.State?.Lived);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to save on shutdown.");
            }
        }
    }

    /// <summary>
    /// Where the world lives. Beside the project during development (so it is easy to find and
    /// delete), overridable so a deployed server can put it somewhere sensible.
    /// </summary>
    private static string WorldFilePath()
    {
        var configured = System.Environment.GetEnvironmentVariable("AVAWORLD_STATE");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return System.IO.Path.Combine(
            ProjectSettings.GlobalizePath("res://"), "world.json");
    }
}
