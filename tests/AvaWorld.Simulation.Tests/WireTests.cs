using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AvaWorld.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AvaWorld.Simulation.Tests;

/// <summary>
/// The channel the companion will speak on. Worth real tests rather than manual poking: it is the
/// one part of the world reachable from the network, and the failure modes that matter — an
/// unauthenticated caller being obeyed, a bad token being accepted — are silent when they happen.
/// </summary>
public class WireTests : IAsyncLifetime
{
    private const string Token = "a-known-token";

    private WireServer _server = null!;
    private int _port;

    public Task InitializeAsync()
    {
        _port = FreePort();
        _server = new WireServer(_port, Token, NullLogger.Instance);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>Ask the OS for a port nobody is using, so tests never collide with a real world.</summary>
    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/"), CancellationToken.None);
        return socket;
    }

    private static async Task SendAsync(ClientWebSocket socket, object message)
    {
        var bytes = Encoding.UTF8.GetBytes(Protocol.Serialize(message));
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, int timeoutMs = 3000)
    {
        var buffer = new byte[16 * 1024];
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return result.MessageType == WebSocketMessageType.Close
                ? null
                : Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- the handshake ----

    [Fact]
    public async Task ItSpeaksWebSocket()
    {
        using var socket = await ConnectAsync();
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task APlainHttpRequest_IsTurnedAwayPolitely()
    {
        // Someone pointing a browser at the port should get an explanation, not a hang.
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));

        var buffer = new byte[512];
        var read = await stream.ReadAsync(buffer);
        var response = Encoding.ASCII.GetString(buffer, 0, read);

        Assert.Contains("400", response);
        Assert.Contains("WebSocket", response);
    }

    // ---- authentication ----

    [Fact]
    public async Task NothingIsObeyed_BeforeAuthentication()
    {
        var obeyed = false;
        _server.IntentionReceived += r =>
        {
            if (r.Intention.Type == "goto") obeyed = true;
            return Task.CompletedTask;
        };

        using var socket = await ConnectAsync();
        await SendAsync(socket, new { type = "goto", place = "garden" });

        var reply = await ReceiveAsync(socket);

        Assert.Contains(RefusalCodes.Unauthenticated, reply);
        Assert.False(obeyed);
    }

    [Fact]
    public async Task AWrongToken_IsRefusedAndTheDoorIsClosed()
    {
        using var socket = await ConnectAsync();
        await SendAsync(socket, new { type = "auth", token = "not-it" });

        var reply = await ReceiveAsync(socket);
        Assert.Contains(RefusalCodes.BadToken, reply);

        // And the connection is not left open to try again.
        Assert.Null(await ReceiveAsync(socket, 2000));
    }

    [Fact]
    public async Task TheRightToken_Authenticates()
    {
        string? seen = null;
        _server.IntentionReceived += r =>
        {
            seen ??= r.Intention.Type;
            return r.Reply(new Refusal("acknowledged", "in"));
        };

        using var socket = await ConnectAsync();
        await SendAsync(socket, new { type = "auth", token = Token });

        var reply = await ReceiveAsync(socket);

        Assert.Equal("auth", seen);
        Assert.Contains("acknowledged", reply);
        Assert.Equal(1, _server.Connected);
    }

    // ---- messages ----

    [Fact]
    public async Task Rubbish_IsRefusedWithoutClosing()
    {
        using var socket = await ConnectAsync();
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("this is not json"), WebSocketMessageType.Text, true, CancellationToken.None);

        var reply = await ReceiveAsync(socket);

        Assert.Contains(RefusalCodes.Malformed, reply);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task AnAuthenticatedIntention_ReachesTheWorld()
    {
        string? place = null;
        _server.IntentionReceived += r =>
        {
            if (r.Intention.Type == "goto")
                place = r.Intention.Place;
            return Task.CompletedTask;
        };

        using var socket = await ConnectAsync();
        await SendAsync(socket, new { type = "auth", token = Token });
        await Task.Delay(200);
        await SendAsync(socket, new { type = "goto", place = "greenhouse" });
        await Task.Delay(500);

        Assert.Equal("greenhouse", place);
    }

    [Fact]
    public async Task Perception_ReachesEveryAuthenticatedBrain_AndNoOneElse()
    {
        using var authenticated = await ConnectAsync();
        await SendAsync(authenticated, new { type = "auth", token = Token });
        await Task.Delay(300);

        using var lurker = await ConnectAsync(); // connected, never authenticated
        await Task.Delay(200);

        await _server.BroadcastAsync(new Arrived("ava", "garden", DateTimeOffset.UnixEpoch));

        var heard = await ReceiveAsync(authenticated);
        Assert.Contains("garden", heard);

        // The lurker gets nothing but the refusal it has not yet asked for.
        Assert.Null(await ReceiveAsync(lurker, 1000));
    }

    // ---- the protocol itself ----

    [Fact]
    public void TheHelloCarriesTheMenu_SoTheCompanionNeedNotRememberTheLayout()
    {
        var hello = new Hello(
            "ava", "hall",
            new[] { new PlaceInfo("hall", "the hall", "plain", new[] { "kitchen" }) },
            new[] { "goto" });

        var json = Protocol.Serialize(hello);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal("hello", parsed.RootElement.GetProperty("type").GetString());
        Assert.Equal("hall", parsed.RootElement.GetProperty("place").GetString());
        Assert.Equal("kitchen",
            parsed.RootElement.GetProperty("places")[0].GetProperty("adjoins")[0].GetString());
    }

    [Fact]
    public void MalformedIntentions_ParseToNothing_RatherThanADefault()
    {
        // A message with no type must not become a valid instruction by accident.
        Assert.Null(Intention.Parse("not json"));
        Assert.Null(Intention.Parse("{}"));
        Assert.Null(Intention.Parse("""{"place":"garden"}"""));
        Assert.NotNull(Intention.Parse("""{"type":"goto","place":"garden"}"""));
    }
}
