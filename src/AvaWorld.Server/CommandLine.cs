namespace AvaWorld.Server;

/// <summary>
/// The handful of switches the world takes. Godot consumes its own arguments, so anything it does
/// not recognise arrives here.
/// </summary>
/// <param name="Walk">
/// Smoke test: instead of taking input, tour the world automatically. Lets the whole loop —
/// connect, move, resolve a room, record it — be verified headlessly, which is otherwise the one
/// part of step two that needs a human at a screen.
/// </param>
public sealed record CommandLine(bool IsClient, string Host, int Port, bool Walk = false)
{
    public static CommandLine Parse(string[] args)
    {
        var isClient = false;
        var walk = false;
        var host = "127.0.0.1";
        var port = Main.DefaultPort;

        foreach (var arg in args)
        {
            if (arg is "--client")
            {
                isClient = true;
            }
            else if (arg is "--walk")
            {
                isClient = true; // walking implies being a client
                walk = true;
            }
            else if (arg.StartsWith("--host=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--host=".Length..].Trim();
                if (value.Length > 0)
                    host = value;
            }
            else if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--port=".Length..], out var parsed)
                     && parsed is > 0 and < 65536)
            {
                port = parsed;
            }
        }

        return new CommandLine(isClient, host, port, walk);
    }
}
