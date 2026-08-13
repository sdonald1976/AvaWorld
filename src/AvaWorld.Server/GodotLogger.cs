using Godot;
using Microsoft.Extensions.Logging;

namespace AvaWorld.Server;

/// <summary>
/// Routes the simulation's <see cref="ILogger"/> output to Godot's console, so running headless
/// with the console executable shows what the world is doing. The simulation logs through the
/// standard abstraction and stays unaware that Godot exists; this is the one adapter that knows.
/// </summary>
public sealed class GodotLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new GodotLogger(Shorten(categoryName));

    public void Dispose() { }

    private static string Shorten(string category)
    {
        var lastDot = category.LastIndexOf('.');
        return lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
    }

    private sealed class GodotLogger : ILogger
    {
        private readonly string _category;

        public GodotLogger(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var stamp = System.DateTimeOffset.Now.ToString("HH:mm:ss");
            var line = $"[{stamp}] {Initial(logLevel)} {_category}: {formatter(state, exception)}";

            if (logLevel >= LogLevel.Error)
                GD.PrintErr(line, exception is null ? "" : "\n" + exception);
            else
                GD.Print(line);
        }

        private static string Initial(LogLevel level) => level switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Information => "inf",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
