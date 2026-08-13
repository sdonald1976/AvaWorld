using AvaWorld.Simulation;
using Godot;
using Microsoft.Extensions.Logging;

namespace AvaWorld.Server;

/// <summary>
/// The root node, in one of two roles.
///
/// Server and client are the same binary because they share the layout and the wire contract, and
/// keeping them in one project means those cannot drift. They remain separate <em>processes</em>,
/// which is what the design cares about: the server is authoritative and keeps running whether or
/// not anyone is connected.
///
///   --headless            run as the world server (default)
///   --client              connect to a running world and walk around in it
///   --host=&lt;addr&gt;         which world to connect to (client only, default 127.0.0.1)
///   --port=&lt;n&gt;            port (default 8737)
///
/// This class owns scheduling, transport, and nothing else. World logic lives in
/// AvaWorld.Simulation, which cannot reference Godot.
/// </summary>
public partial class Main : Node
{
    public const int DefaultPort = 8737;

    /// <summary>
    /// How often the simulation is advanced. Not a frame rate — elapsed time is measured rather
    /// than assumed, so this decides only how promptly the world notices time passing and how
    /// often it saves.
    /// </summary>
    private const double TickSeconds = 1.0;

    /// <summary>
    /// How often a client tells the server where it is. Ten times a second is far more than place
    /// resolution needs (rooms are metres across) and keeps the traffic trivial.
    /// </summary>
    private const double PositionReportSeconds = 0.1;

    private ILoggerFactory _loggerFactory = null!;
    private ILogger<Main> _log = null!;

    private bool _isServer;
    private World? _world;          // server only
    private Player? _player;        // client only
    private double _sinceTick;
    private double _sinceReport;
    private bool _ticking;
    private bool _walking;
    private List<string> _tour = new();
    private int _tourIndex;

    public override void _Ready()
    {
        _loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new GodotLoggerProvider()));
        _log = _loggerFactory.CreateLogger<Main>();

        var args = CommandLine.Parse(OS.GetCmdlineArgs());
        _isServer = !args.IsClient;

        if (_isServer)
            StartServer(args);
        else
            StartClient(args);
    }

    // ---- server ----

    private void StartServer(CommandLine args)
    {
        var path = WorldFilePath();
        _log.LogInformation("World file: {Path}", path);

        _world = new World(
            new JsonFileWorldStore(path), TimeProvider.System, _loggerFactory.CreateLogger<World>());

        // Godot installs a synchronization context on the main thread, so blocking here on a task
        // whose continuation wants that same thread deadlocks before the world ever starts.
        var result = Task.Run(async () =>
        {
            var start = await _world.StartAsync();
            await _world.DefineLayoutAsync(Cottage.Graph(), Cottage.Spawn);
            return start;
        }).GetAwaiter().GetResult();

        // A layout that contradicts itself is worth refusing to run on: a room with no floor, or
        // two rooms claiming the same ground, produces behaviour that looks like a haunting.
        var problems = Cottage.Map().Reconcile(Cottage.Graph());
        foreach (var problem in problems)
            _log.LogError("Layout problem: {Problem}", problem);
        if (problems.Count > 0)
        {
            _log.LogError("Refusing to start with an inconsistent layout.");
            GetTree().Quit(1);
            return;
        }

        if (result.Created)
            _log.LogInformation("A new world begins.");
        else if (result.Gap is { } gap)
            _log.LogInformation("Resumed after {Duration} away. That time has no history.", gap.Duration);
        else
            _log.LogInformation("Resumed. Lived {Lived} so far.", _world.State!.Lived);

        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(args.Port, maxClients: 8);
        if (error != Error.Ok)
        {
            _log.LogError("Could not listen on port {Port}: {Error}. Is a world already running?", args.Port, error);
            GetTree().Quit(1);
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        _log.LogInformation("Listening on port {Port}. Ava is in the {Place}.", args.Port, _world.PlaceOf("ava") ?? Cottage.Spawn);

        // Ava exists whether or not she has a body yet — she lives here even with nobody watching.
        if (_world.PlaceOf("ava") is null)
            Task.Run(() => _world!.EnterAsync("ava", Cottage.Spawn)).GetAwaiter().GetResult();
    }

    private void OnPeerConnected(long id)
    {
        var body = BodyFor(id);
        _log.LogInformation("{Body} joined.", body);
        _ = RunAsync(async () => await _world!.EnterAsync(body, Cottage.Spawn));
        RpcId(id, nameof(Welcome), Cottage.Spawn);
    }

    private void OnPeerDisconnected(long id)
    {
        var body = BodyFor(id);
        _log.LogInformation("{Body} left.", body);
        _ = RunAsync(async () => await _world!.LeaveAsync(body));
    }

    private static string BodyFor(long peerId) => $"guest:{peerId}";

    // ---- the wire ----

    /// <summary>
    /// Client → server: where I am. Unreliable on purpose; a dropped update is corrected by the
    /// next one a tenth of a second later, and ordering does not matter for a position.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void ReportPosition(Vector3 position)
    {
        if (!_isServer || _world is null)
            return;

        var body = BodyFor(Multiplayer.GetRemoteSenderId());
        var place = Cottage.Map().PlaceAt(position.X, position.Z);

        // Null means between rooms. Keep the last place rather than inventing one — a body in a
        // doorway has not left the world.
        if (place is null)
            return;

        _ = RunAsync(async () =>
        {
            if (await _world.EnterAsync(body, place))
            {
                _log.LogInformation("{Body} is now in the {Place}.", body, place);
                Rpc(nameof(PlaceChanged), body, place);
            }
        });
    }

    /// <summary>Server → client: you are in, and this is where.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
    public void Welcome(string place)
    {
        if (_isServer)
            return;
        _log.LogInformation("Joined the world, in the {Place}.", place);
    }

    /// <summary>Server → everyone: somebody moved between rooms.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false)]
    public void PlaceChanged(string body, string place)
    {
        if (_isServer)
            return;
        _log.LogInformation("{Body} is now in the {Place}.", body, place);
    }

    // ---- client ----

    private void StartClient(CommandLine args)
    {
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(args.Host, args.Port);
        if (error != Error.Ok)
        {
            _log.LogError("Could not reach a world at {Host}:{Port}: {Error}", args.Host, args.Port, error);
            GetTree().Quit(1);
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.ConnectedToServer += () => _log.LogInformation("Connected to the world at {Host}:{Port}.", args.Host, args.Port);
        Multiplayer.ConnectionFailed += () =>
        {
            _log.LogError("The world at {Host}:{Port} did not answer. Is the server running?", args.Host, args.Port);
            GetTree().Quit(1);
        };
        Multiplayer.ServerDisconnected += () =>
        {
            _log.LogWarning("The world stopped. Leaving.");
            GetTree().Quit();
        };

        _walking = args.Walk;

        var scene = new Node3D { Name = "World" };
        AddChild(scene);
        WorldGeometry.Build(scene, withVisuals: !_walking);

        _player = new Player { Name = "Player", Position = WorldGeometry.SpawnPoint(), TakesInput = !_walking };
        scene.AddChild(_player);

        if (_walking)
        {
            // The smoke-test tour: every room, in a walkable order.
            _tour = Cottage.Graph().Route(Cottage.Spawn, Cottage.Garden)
                .Concat(new[] { Cottage.Study })
                .ToList();
            _log.LogInformation("Smoke test: touring {Places}.", string.Join(" → ", _tour));
        }
        else
        {
            _log.LogInformation("Walk with WASD, look with the mouse, Escape releases it.");
        }
    }

    // ---- the loop ----

    public override void _Process(double delta)
    {
        if (_isServer)
        {
            _sinceTick += delta;
            if (_sinceTick >= TickSeconds && !_ticking)
            {
                _sinceTick = 0;
                _ticking = true;
                _ = RunAsync(async () => await _world!.TickAsync(), () => _ticking = false);
            }
            return;
        }

        if (_player is null || Multiplayer.MultiplayerPeer is null)
            return;

        if (_walking)
            AdvanceTour(delta);

        _sinceReport += delta;
        if (_sinceReport < PositionReportSeconds)
            return;
        _sinceReport = 0;

        if (Multiplayer.HasMultiplayerPeer() && Multiplayer.MultiplayerPeer.GetConnectionStatus()
            == MultiplayerPeer.ConnectionStatus.Connected)
        {
            RpcId(1, nameof(ReportPosition), _player.Position);
        }
    }

    /// <summary>
    /// Moves the smoke-test player toward the next room on the tour, and stops the client once it
    /// has visited them all. Straight-line and collision-free on purpose: this exercises the wire
    /// and place resolution, not the walking.
    /// </summary>
    private void AdvanceTour(double delta)
    {
        if (_player is null || _tourIndex >= _tour.Count)
            return;

        var target = WorldGeometry.CentreOf(_tour[_tourIndex]);
        if (target is null)
        {
            _tourIndex++;
            return;
        }

        var to = target.Value - _player.Position;
        if (to.Length() < 0.5f)
        {
            _log.LogInformation("Reached the {Place}.", _tour[_tourIndex]);
            _tourIndex++;
            if (_tourIndex >= _tour.Count)
            {
                _log.LogInformation("Tour complete; the whole layout is reachable over the wire.");
                // Give the last position report time to land before dropping the connection.
                GetTree().CreateTimer(1.0).Timeout += () => GetTree().Quit();
            }
            return;
        }

        _player.Position += to.Normalized() * (float)(12.0 * delta);
    }

    /// <summary>
    /// Runs simulation work off the main thread. The frame loop is synchronous and the store is
    /// not; nothing here may block the thread Godot needs to keep drawing.
    /// </summary>
    private async Task RunAsync(Func<Task> work, Action? then = null)
    {
        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "World work failed; the world keeps running.");
        }
        finally
        {
            then?.Invoke();
        }
    }

    public override void _Notification(int what)
    {
        if (what is not ((int)NotificationWMCloseRequest or (int)NotificationPredelete))
            return;
        if (!_isServer || _world is null)
            return;

        // One last tick so the saved LastTickedAt is when we actually stopped, keeping the
        // recorded downtime honest rather than counting the final second of runtime as a gap.
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

    private static string WorldFilePath()
    {
        var configured = System.Environment.GetEnvironmentVariable("AVAWORLD_STATE");
        return !string.IsNullOrWhiteSpace(configured)
            ? configured
            : System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), "world.json");
    }
}
