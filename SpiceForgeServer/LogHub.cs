using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer;

/// <summary>
/// The server's log: keeps the last entries for <c>log.history</c>, sends each entry as a <c>log</c> event to the
/// sessions whose verbosity includes it, and echoes warnings and errors to the console.
/// </summary>
public sealed class LogHub
{
    public const int HistoryLength = 1000;

    private readonly Hub hub;
    private readonly LinkedList<JsonObject> history = new();
    private readonly object sync = new();

    public LogHub(Hub hub) => this.hub = hub;

    public void Write(MessageType level, string source, string message)
    {
        var entry = new JsonObject
        {
            ["time"] = DateTime.UtcNow.ToString("O"),
            ["level"] = LevelName(level),
            ["source"] = source,
            ["message"] = message,
        };
        lock (sync)
        {
            history.AddLast(entry);
            if (history.Count > HistoryLength)
                history.RemoveFirst();
        }
        if (level <= MessageType.Warning)
            Console.Error.WriteLine($"[{LevelName(level)}] {source}: {message}");
        hub.Broadcast("log", entry, s => level <= s.Verbosity);
    }

    public JsonArray History(int limit)
    {
        lock (sync)
            return Json.Array(history.Skip(Math.Max(0, history.Count - limit)).Select(e => (JsonNode?)e.DeepClone()));
    }

    /// <summary>A LiveSPICE log that writes here, under <paramref name="source"/>, and also keeps what it was given for
    /// the caller (a file load's messages, a build's problems).</summary>
    public CapturingLog For(string source) => new(this, source);

    public static string LevelName(MessageType level) => level switch
    {
        MessageType.Error => "error",
        MessageType.Warning => "warning",
        MessageType.Info => "info",
        _ => "verbose",
    };

    public static MessageType ParseLevel(string name) => name switch
    {
        "error" => MessageType.Error,
        "warning" => MessageType.Warning,
        "info" => MessageType.Info,
        "verbose" => MessageType.Verbose,
        _ => throw new ApiException(ErrorCodes.InvalidValue, $"'{name}' isn't a log level: use error, warning, info or verbose."),
    };
}

public sealed class CapturingLog : ILog
{
    private readonly LogHub hub;
    private readonly string source;

    public List<(MessageType Level, string Message)> Messages { get; } = new();

    public CapturingLog(LogHub hub, string source)
    {
        this.hub = hub;
        this.source = source;
    }

    public void WriteLine(MessageType type, string text, params object[] format)
    {
        string message = format.Length > 0 ? string.Format(text, format) : text;
        Messages.Add((type, message));
        hub.Write(type, source, message);
    }

    public void WriteLines(MessageType type, IEnumerable<string> lines)
    {
        foreach (string line in lines)
            WriteLine(type, line);
    }
}
