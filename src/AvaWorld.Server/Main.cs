using AvaWorld.Simulation;
using AvaWorld.Wire;
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

    /// <summary>How often the server tells clients where Ava is, so she is seen walking.</summary>
    private const double AvaBroadcastSeconds = 0.1;

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

    private string? _token;            // server only
    private WireServer? _wire;         // server only — the companion's channel
    private AvaBody? _ava;             // server only
    private Wandering? _wandering;     // server only, placeholder until the companion connects
    private double _sinceAvaBroadcast;
    private Node3D? _avaGhost;         // client only — where the server says she is
    private bool _admitted;            // client only — authentication finished, safe to talk

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

        // Nobody gets in without the token. With an auth callback set, PeerConnected does not fire
        // until authentication succeeds, so everything downstream can assume the peer is allowed.
        _token = WorldToken.ResolveOrCreate(path);
        var scene = (SceneMultiplayer)Multiplayer;
        scene.AuthCallback = Callable.From<long, byte[]>(OnAuthReceived);
        scene.AuthTimeout = 5.0;
        scene.PeerAuthenticationFailed += id =>
            _log.LogWarning("Peer {Id} failed authentication and was refused.", id);

        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        _log.LogInformation("Token: {Path}", WorldToken.PathBeside(path));

        _log.LogInformation("Listening on port {Port}. Ava is in the {Place}.", args.Port, _world.PlaceOf("ava") ?? Cottage.Spawn);

        // She lives here whether or not anyone is watching.
        if (_world.PlaceOf(AvaBody.BodyId) is null)
            Task.Run(() => _world!.EnterAsync(AvaBody.BodyId, Cottage.Spawn)).GetAwaiter().GetResult();

        _ava = new AvaBody(_world, new Navigator(Cottage.Graph(), Cottage.Map(), Cottage.Doorways()), Cottage.Map());
        _wandering = new Wandering(Cottage.Graph(), TimeSpan.FromSeconds(20));

        StartWire(args.Port + 1);
    }

    // ---- the wire (the companion's channel) ----

    /// <summary>
    /// Opens the brain's channel, one port up from the rendering clients'.
    ///
    /// Two transports on purpose: rendering clients want Godot's replication, the brain wants
    /// events and intentions. Keeping them apart is what stops the companion ever linking a Godot
    /// assembly, and it means this channel can be driven by anything that speaks WebSocket —
    /// including a console app, which is how the protocol got proved before the brain existed.
    /// </summary>
    private void StartWire(int port)
    {
        _wire = new WireServer(port, _token!, _loggerFactory.CreateLogger<WireServer>());
        _wire.IntentionReceived += HandleIntentionAsync;

        try
        {
            _wire.Start();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not open the wire on port {Port}; the world runs without a brain.", port);
            _wire = null;
        }
    }

    /// <summary>
    /// Everything the brain may ask for. Note what is missing: there is no way to say where she
    /// should stand, only which place she should be in. The world keeps "how".
    /// </summary>
    private async Task HandleIntentionAsync(WireRequest request)
    {
        if (_world is null)
            return;

        switch (request.Intention.Type.ToLowerInvariant())
        {
            case "auth":
                // Authenticating tells her what exists. She is given the menu every time rather
                // than being expected to remember a layout that may have changed.
                await request.Reply(Greeting());
                break;

            case "places":
                await request.Reply(Greeting());
                break;

            case "where":
                await request.Reply(new Arrived(
                    AvaBody.BodyId, _world.PlaceOf(AvaBody.BodyId) ?? Cottage.Spawn, DateTimeOffset.UtcNow));
                break;

            case "stop":
                _wandering = null; // hand over: something else is deciding now
                await request.Reply(new Refusal("acknowledged", "She will stay where she is."));
                break;

            case "goto":
                await GoToAsync(request);
                break;

            default:
                await request.Reply(new Refusal(
                    RefusalCodes.UnknownIntention, $"I do not know how to '{request.Intention.Type}'."));
                break;
        }
    }

    private async Task GoToAsync(WireRequest request)
    {
        var place = request.Intention.Place;

        if (string.IsNullOrWhiteSpace(place) || !Cottage.Graph().Contains(place))
        {
            await request.Reply(new Refusal(
                RefusalCodes.UnknownPlace, $"There is no '{place}' here."));
            return;
        }

        var from = _world!.PlaceOf(AvaBody.BodyId) ?? Cottage.Spawn;
        if (Cottage.Graph().Route(from, place).Count == 0)
        {
            await request.Reply(new Refusal(
                RefusalCodes.Unreachable, $"She cannot get to the {place} from the {from}."));
            return;
        }

        // A brain that is steering retires the placeholder. Wandering exists only to stop the
        // world being inert before this connection existed.
        _wandering = null;

        await _world.SetDestinationAsync(AvaBody.BodyId, place);
        _log.LogInformation("The wire sends Ava to the {Place}.", place);
    }

    /// <summary>The menu of what exists: the only thing the companion is allowed to choose from.</summary>
    private Hello Greeting()
    {
        var graph = Cottage.Graph();
        var places = graph.All
            .Select(p => new PlaceInfo(p.Id, p.Name, p.Description, graph.Neighbours(p.Id).ToList()))
            .ToList();

        return new Hello(
            AvaBody.BodyId,
            _world?.PlaceOf(AvaBody.BodyId),
            places,
            new[] { "goto", "where", "places", "stop" });
    }

    /// <summary>Tells the brain something happened. Fire and forget — perception must never stall the world.</summary>
    private void Perceive(object message)
    {
        if (_wire is null)
            return;
        _ = Task.Run(() => _wire.BroadcastAsync(message));
    }

    /// <summary>
    /// A peer has presented something. Accept only an exact match; anything else is disconnected
    /// rather than left hanging, so a wrong token fails immediately instead of timing out.
    /// </summary>
    private void OnAuthReceived(long id, byte[] presented)
    {
        var scene = (SceneMultiplayer)Multiplayer;
        var text = System.Text.Encoding.UTF8.GetString(presented).Trim();

        if (_token is not null && WorldToken.Matches(_token, text))
        {
            scene.CompleteAuth((int)id);
            return;
        }

        _log.LogWarning("Peer {Id} presented the wrong token.", id);
        scene.DisconnectPeer((int)id);
    }

    private void OnPeerConnected(long id)
    {
        var body = BodyFor(id);
        _log.LogInformation("{Body} joined.", body);
        _ = RunAsync(async () => await _world!.EnterAsync(body, Cottage.Spawn));
        RpcId(id, nameof(Welcome), Cottage.Spawn);

        // She can tell when you are around. What she does with that is the companion's business,
        // and the design is emphatic that it must not become an excuse to nag.
        Perceive(new Presence(body, "joined", Cottage.Spawn, DateTimeOffset.UtcNow));
    }

    private void OnPeerDisconnected(long id)
    {
        var body = BodyFor(id);
        _log.LogInformation("{Body} left.", body);
        _ = RunAsync(async () => await _world!.LeaveAsync(body));
        Perceive(new Presence(body, "left", null, DateTimeOffset.UtcNow));
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
                Announce(body, place);
                Perceive(new Arrived(body, place, DateTimeOffset.UtcNow));
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

    /// <summary>
    /// Server → everyone: this is where Ava is. Unreliable, because a dropped update is corrected
    /// a tenth of a second later and a stale position is worse than a missed one.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    public void AvaMoved(Vector3 position)
    {
        if (_isServer || _avaGhost is null)
            return;

        // Snap rather than interpolate for now. Ten updates a second at walking pace is smooth
        // enough to read, and interpolation is a rendering nicety that can wait for a real model.
        _avaGhost.Position = position;
    }

    // ---- client ----

    private void StartClient(CommandLine args)
    {
        var token = WorldToken.ResolveForClient(WorldFilePath());
        if (token is null)
        {
            _log.LogError(
                "No token. Set {Var}, or run this beside a world that has written its {File}.",
                WorldToken.EnvironmentVariable, WorldToken.PathBeside(WorldFilePath()));
            GetTree().Quit(1);
            return;
        }

        // Godot's handshake needs both ends to finish, and a peer only enters the authenticating
        // state at all when an auth callback is set. So the client sets one — it asks nothing of
        // the server, so it accepts immediately — and on being asked, sends the token and declares
        // itself satisfied. Without the CompleteAuth here, a correct token still times out, which
        // looks exactly like a wrong one.
        var net = (SceneMultiplayer)Multiplayer;
        net.AuthCallback = Callable.From<long, byte[]>((id, _) => net.CompleteAuth((int)id));
        net.AuthTimeout = 5.0;
        net.PeerAuthenticating += id =>
        {
            net.SendAuth((int)id, System.Text.Encoding.UTF8.GetBytes(token));
            net.CompleteAuth((int)id);
        };
        net.PeerAuthenticationFailed += _ =>
        {
            _log.LogError("The world refused our token.");
            GetTree().Quit(1);
        };

        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(args.Host, args.Port);
        if (error != Error.Ok)
        {
            _log.LogError("Could not reach a world at {Host}:{Port}: {Error}", args.Host, args.Port, error);
            GetTree().Quit(1);
            return;
        }

        Multiplayer.MultiplayerPeer = peer;
        Multiplayer.ConnectedToServer += () =>
        {
            // Only true once authentication has completed. Sending anything before this point is
            // a packet the server rejects as not-an-auth-command, which floods its log.
            _admitted = true;
            _log.LogInformation("Connected to the world at {Host}:{Port}.", args.Host, args.Port);
        };
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

        // A stand-in for her, moved by the server. Not a character — a marker that she is a body
        // in a place rather than a value in a database. The .glb from the companion's avatar work
        // replaces this without changing anything about how she moves.
        _avaGhost = WorldGeometry.BuildAvaStandIn();
        scene.AddChild(_avaGhost);

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

            MoveAva(delta);
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

        if (_admitted)
            RpcId(1, nameof(ReportPosition), _player.Position);
    }

    /// <summary>
    /// Walks Ava, and tells anyone watching where she is.
    ///
    /// Note the division: this decides nothing about where she goes. It advances a body toward a
    /// destination the world already holds, and records arrivals. The placeholder that *chooses*
    /// destinations is the only part here that step five deletes.
    /// </summary>
    private void MoveAva(double delta)
    {
        if (_ava is null || _world is null)
            return;

        // Wandering is optional and goes away the moment something else is steering. Requiring it
        // here is what made her stop dead the first time the wire took over: taking control set it
        // to null, and this guard then skipped the walking too.
        if (_wandering?.Next(delta, _ava.CurrentPlace, _ava.IsWalking) is { } wantsToGo)
        {
            _ = RunAsync(async () =>
            {
                if (await _world.SetDestinationAsync(AvaBody.BodyId, wantsToGo))
                    _log.LogInformation("Ava sets off for the {Place}.", wantsToGo);
            });
        }

        if (_ava.Advance(delta) is { } arrivedAt)
        {
            _ = RunAsync(async () =>
            {
                if (await _world.EnterAsync(AvaBody.BodyId, arrivedAt))
                {
                    _log.LogInformation("Ava is in the {Place}.", arrivedAt);
                    Announce(AvaBody.BodyId, arrivedAt);
                    Perceive(new Arrived(AvaBody.BodyId, arrivedAt, DateTimeOffset.UtcNow));
                }
            });
        }

        // Her position goes out continuously, not only on arrival, so a client can watch her walk
        // rather than see her teleport between rooms.
        _sinceAvaBroadcast += delta;
        if (_sinceAvaBroadcast >= AvaBroadcastSeconds && Multiplayer.GetPeers().Length > 0)
        {
            _sinceAvaBroadcast = 0;
            Rpc(nameof(AvaMoved), _ava.Position);
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
    /// Broadcasts a room change, from whichever thread noticed it.
    ///
    /// World writes happen off the main thread so saving cannot stall the frame loop, but Godot
    /// refuses multiplayer calls from anywhere else — "Multiplayer can only be manipulated from
    /// the main thread". Deferring hops back before sending, so the two rules can both hold.
    /// </summary>
    private void Announce(string body, string place)
        => Callable.From(() => Rpc(nameof(PlaceChanged), body, place)).CallDeferred();

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
