using System.Net.WebSockets;
using System.Text;
using AvaWorld.Wire;

// A stand-in for the companion: connects to the world's wire, authenticates, prints everything it
// is told, and sends the intentions you type.
//
// Its whole purpose is to prove the protocol before the brain depends on it. If driving Ava around
// from here feels awkward, the protocol is wrong, and that is much cheaper to discover now than
// after the companion is wired to it.
//
//   dotnet run --project src/AvaWorld.Poke -- [--host=127.0.0.1] [--port=8738] [--token=...]
//
// Then type:  greenhouse       (go there)
//             places           (what exists)
//             where            (where is she)
//             quit

var host = Arg("--host") ?? "127.0.0.1";
var port = int.TryParse(Arg("--port"), out var p) ? p : 8738;
var token = Arg("--token")
            ?? Environment.GetEnvironmentVariable("AVAWORLD_TOKEN")
            ?? ReadTokenBesideWorld();

if (token is null)
{
    Console.Error.WriteLine("No token. Pass --token=..., set AVAWORLD_TOKEN, or run beside the world.");
    return 1;
}

using var socket = new ClientWebSocket();
var uri = new Uri($"ws://{host}:{port}/");

Console.WriteLine($"connecting to {uri}");
try
{
    await socket.ConnectAsync(uri, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"could not reach the world: {ex.Message}");
    return 1;
}

var closing = new CancellationTokenSource();
var listening = Task.Run(() => ListenAsync(socket, closing.Token));

await SendAsync(socket, new { type = "auth", token });

// One-shot mode: say one thing, listen for a moment, leave. This is what makes the wire testable
// without a person at a keyboard, the same way --walk did for the rendering client.
var say = Arg("--say");
if (say is not null)
{
    await Task.Delay(400); // let the hello land first, so the transcript reads in order
    await SendAsync(socket, Compose(say));
    await Task.Delay(int.TryParse(Arg("--listen"), out var ms) ? ms : 3000);
}
else
{
    Console.WriteLine("type a place name to send her there, 'places', 'where', or 'quit'");

    while (!closing.IsCancellationRequested)
    {
        var line = Console.ReadLine()?.Trim();
        if (line is null or "quit" or "exit")
            break;
        if (line.Length == 0)
            continue;

        await SendAsync(socket, Compose(line));
    }
}

await closing.CancelAsync();
try
{
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
}
catch
{
    // Already gone.
}

await listening;
return 0;

/// <summary>Turns a typed word into an intention. Anything unrecognised is taken as a place name.</summary>
static object Compose(string line) => line switch
{
    "places" => new { type = "places" },
    "where" => new { type = "where" },
    "stop" => new { type = "stop" },
    _ => new { type = "goto", place = line },
};

static async Task SendAsync(ClientWebSocket socket, object message)
{
    var bytes = Encoding.UTF8.GetBytes(Protocol.Serialize(message));
    await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
}

static async Task ListenAsync(ClientWebSocket socket, CancellationToken ct)
{
    var buffer = new byte[16 * 1024];

    while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        try
        {
            result = await socket.ReceiveAsync(buffer, ct);
        }
        catch (Exception)
        {
            return;
        }

        if (result.MessageType == WebSocketMessageType.Close)
            return;

        Console.WriteLine("  < " + Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}

static string? Arg(string name) => Environment.GetCommandLineArgs()
    .FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
    ?[(name.Length + 1)..];

/// <summary>Convenience for running on the same machine as the world: read its token file.</summary>
static string? ReadTokenBesideWorld()
{
    var candidate = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "AvaWorld.Server", ".avaworld-token");
    candidate = Path.GetFullPath(candidate);
    return File.Exists(candidate) ? File.ReadAllText(candidate).Trim() : null;
}
