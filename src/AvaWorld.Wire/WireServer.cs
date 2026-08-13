using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AvaWorld.Wire;

/// <summary>What a connected brain asked for, and who to answer.</summary>
public sealed record WireRequest(Intention Intention, Func<object, Task> Reply);

/// <summary>
/// The channel the companion talks to the world on.
///
/// Deliberately not Godot's multiplayer. The brain renders nothing and needs no state replication —
/// it needs events in and intentions out — and keeping it on a plain WebSocket means the companion
/// never links a Godot assembly and the traffic stays readable in a log. It also means this whole
/// class is testable without an engine.
///
/// Raw <see cref="TcpListener"/> plus the framework's WebSocket rather than HttpListener, because
/// HttpListener on Windows needs a URL ACL to bind anywhere but localhost, and this world lives on
/// another machine.
/// </summary>
public sealed class WireServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _token;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<WireConnection> _connections = new();
    private readonly object _connectionsLock = new();

    private TcpListener? _listener;
    private Task? _accepting;

    public WireServer(int port, string token, ILogger log)
    {
        _port = port;
        _token = token;
        _log = log;
    }

    /// <summary>Raised for every authenticated intention. Handlers run on a background thread.</summary>
    public event Func<WireRequest, Task>? IntentionReceived;

    /// <summary>How many brains are currently connected and authenticated.</summary>
    public int Connected
    {
        get { lock (_connectionsLock) return _connections.Count(c => c.Authenticated); }
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _accepting = Task.Run(AcceptLoopAsync);
        _log.LogInformation("Wire listening on port {Port}.", _port);
    }

    /// <summary>
    /// Sends to every authenticated connection. Perception is broadcast rather than addressed:
    /// there is one brain today, and a world that behaved differently for a second one would be
    /// keeping secrets from itself.
    /// </summary>
    public async Task BroadcastAsync(object message)
    {
        List<WireConnection> targets;
        lock (_connectionsLock)
            targets = _connections.Where(c => c.Authenticated).ToList();

        foreach (var connection in targets)
            await connection.SendAsync(message);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Wire accept failed; still listening.");
                continue;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        WireConnection? connection = null;

        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            if (!await WebSocketHandshake.TryAcceptAsync(stream, _stopping.Token))
            {
                _log.LogWarning("Wire rejected a non-WebSocket connection from {Remote}.", remote);
                return;
            }

            using var socket = WebSocket.CreateFromStream(
                stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));

            connection = new WireConnection(socket, remote);
            lock (_connectionsLock)
                _connections.Add(connection);

            await ReceiveLoopAsync(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Wire connection from {Remote} ended.", remote);
        }
        finally
        {
            if (connection is not null)
            {
                lock (_connectionsLock)
                    _connections.Remove(connection);
                _log.LogInformation("Wire: {Remote} disconnected.", remote);
            }
            client.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WireConnection connection)
    {
        var buffer = new byte[8192];

        while (connection.Socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            var text = await ReadFrameAsync(connection, buffer);
            if (text is null)
                return;

            var intention = Intention.Parse(text);
            if (intention is null)
            {
                await connection.SendAsync(new Refusal(RefusalCodes.Malformed, "That was not a message I understand."));
                continue;
            }

            // Nothing but authentication is entertained until authentication succeeds.
            if (!connection.Authenticated)
            {
                if (!string.Equals(intention.Type, "auth", StringComparison.OrdinalIgnoreCase))
                {
                    await connection.SendAsync(new Refusal(
                        RefusalCodes.Unauthenticated, "Send an auth message first."));
                    continue;
                }

                if (intention.Token is null || !FixedTimeEquals(_token, intention.Token))
                {
                    _log.LogWarning("Wire: {Remote} presented the wrong token.", connection.Remote);
                    await connection.SendAsync(new Refusal(RefusalCodes.BadToken, "That token is not right."));
                    await connection.CloseAsync();
                    return;
                }

                connection.Authenticated = true;
                _log.LogInformation("Wire: {Remote} authenticated.", connection.Remote);
                await OnIntention(new WireRequest(intention, connection.SendAsync));
                continue;
            }

            await OnIntention(new WireRequest(intention, connection.SendAsync));
        }
    }

    private async Task<string?> ReadFrameAsync(WireConnection connection, byte[] buffer)
    {
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stopping.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (message.Length + result.Count > Protocol.MaxFrameBytes)
            {
                _log.LogWarning("Wire: {Remote} sent an oversized frame; closing.", connection.Remote);
                await connection.CloseAsync();
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    private async Task OnIntention(WireRequest request)
    {
        if (IntentionReceived is null)
            return;

        try
        {
            await IntentionReceived(request);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handling a wire intention failed; the world keeps running.");
        }
    }

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented));

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener?.Stop();

        List<WireConnection> open;
        lock (_connectionsLock)
            open = _connections.ToList();
        foreach (var connection in open)
            await connection.CloseAsync();

        if (_accepting is not null)
        {
            try { await _accepting; } catch { /* shutting down */ }
        }

        _stopping.Dispose();
    }
}

/// <summary>One connected brain.</summary>
internal sealed class WireConnection
{
    private readonly SemaphoreSlim _sending = new(1, 1);

    public WireConnection(WebSocket socket, string remote)
    {
        Socket = socket;
        Remote = remote;
    }

    public WebSocket Socket { get; }
    public string Remote { get; }
    public bool Authenticated { get; set; }

    /// <summary>Sends one message. Serialised, because a WebSocket permits one send at a time.</summary>
    public async Task SendAsync(object message)
    {
        var bytes = Encoding.UTF8.GetBytes(Protocol.Serialize(message));

        await _sending.WaitAsync();
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                await Socket.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // The brain went away mid-send. Not an error worth propagating into the world.
        }
        finally
        {
            _sending.Release();
        }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
        }
        catch
        {
            // Already gone.
        }
    }
}
