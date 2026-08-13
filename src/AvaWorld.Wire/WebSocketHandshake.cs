using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AvaWorld.Wire;

/// <summary>
/// The HTTP upgrade that turns a TCP connection into a WebSocket.
///
/// Hand-rolled because the framework's server-side WebSocket wants a stream that has already been
/// upgraded, and the alternative — HttpListener — needs a URL ACL on Windows to bind anywhere but
/// localhost. The world runs on another machine, so "localhost only" is not an option and asking
/// someone to run netsh before their world will start is worse than forty lines of handshake.
///
/// Only the parts of RFC 6455 that matter for a private channel between two programs: parse the
/// request, echo the key, and answer 101. Framing after this point is the framework's problem.
/// </summary>
internal static class WebSocketHandshake
{
    /// <summary>The magic string from RFC 6455 §1.3. Not a secret; it makes the accept hash unambiguous.</summary>
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads the client's upgrade request and answers it. False when this was not a WebSocket
    /// request at all — someone pointing a browser at the port, most likely.
    /// </summary>
    public static async Task<bool> TryAcceptAsync(NetworkStream stream, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);

        var request = await ReadHeadersAsync(stream, deadline.Token);
        if (request is null)
            return false;

        var key = FindHeader(request, "sec-websocket-key");
        if (key is null)
        {
            await RefuseAsync(stream, deadline.Token);
            return false;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.UTF8.GetBytes(key + Guid)));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n"
            + "Upgrade: websocket\r\n"
            + "Connection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, deadline.Token);
        await stream.FlushAsync(deadline.Token);
        return true;
    }

    /// <summary>
    /// Reads until the blank line that ends the headers, one byte at a time so nothing of the
    /// WebSocket stream that follows is swallowed into a buffer the framework will never see.
    /// </summary>
    private static async Task<string?> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        var matched = 0; // how much of "\r\n\r\n" we have seen

        while (text.Length < 8192)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(one, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
                return null;

            var c = (char)one[0];
            text.Append(c);

            var expected = matched switch { 0 => '\r', 1 => '\n', 2 => '\r', _ => '\n' };
            matched = c == expected ? matched + 1 : (c == '\r' ? 1 : 0);

            if (matched == 4)
                return text.ToString();
        }

        return null; // headers implausibly long
    }

    private static string? FindHeader(string request, string name)
    {
        foreach (var line in request.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (line.AsSpan(0, colon).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static async Task RefuseAsync(NetworkStream stream, CancellationToken ct)
    {
        const string body = "This is the AvaWorld wire. It speaks WebSocket.";
        var response =
            "HTTP/1.1 400 Bad Request\r\n"
            + "Content-Type: text/plain\r\n"
            + $"Content-Length: {body.Length}\r\n\r\n"
            + body;

        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
        }
        catch
        {
            // They hung up. Fine.
        }
    }
}
