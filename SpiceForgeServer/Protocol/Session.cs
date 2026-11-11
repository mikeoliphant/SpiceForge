using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SpiceForgeServer.Protocol;

/// <summary>One client connection: its socket, and what it has asked for (verbosity, watched files).</summary>
public sealed class Session
{
    private static int next;

    private readonly WebSocket socket;
    private readonly SemaphoreSlim sending = new(1, 1);

    public int Id { get; } = Interlocked.Increment(ref next);

    /// <summary>Whether the connection has said <c>hello</c>.</summary>
    public bool Greeted { get; set; }

    public string ClientName { get; set; } = "";

    /// <summary>The lowest log level this connection receives (see <see cref="LogHub"/>).</summary>
    public Util.MessageType Verbosity { get; set; } = Util.MessageType.Info;

    /// <summary>The files this connection wants watched (<c>file.watch</c>), as full paths.</summary>
    public IReadOnlyCollection<string> WatchedPaths { get; set; } = Array.Empty<string>();

    public Session(WebSocket socket) => this.socket = socket;

    public bool IsOpen => socket.State == WebSocketState.Open;

    /// <summary>Sends one JSON message. Sends from different threads are serialized; a closed socket drops it.</summary>
    public async Task SendAsync(JsonNode message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(Json.Options));
        await sending.WaitAsync();
        try
        {
            if (IsOpen)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The client went away; the receive loop notices and ends the session.
        }
        finally
        {
            sending.Release();
        }
    }

    public Task SendEventAsync(string name, JsonNode? data) => SendAsync(new JsonObject { ["event"] = name, ["data"] = data });
}

/// <summary>Every open session, for broadcasting events.</summary>
public sealed class Hub
{
    private readonly ConcurrentDictionary<int, Session> sessions = new();

    public void Add(Session session) => sessions[session.Id] = session;

    public void Remove(Session session) => sessions.TryRemove(session.Id, out _);

    public IEnumerable<Session> Sessions => sessions.Values.Where(s => s.Greeted);

    /// <summary>Sends an event to every greeted session, or to those <paramref name="to"/> selects. Doesn't wait: a slow
    /// client mustn't hold up the others.</summary>
    public void Broadcast(string name, JsonNode? data, Func<Session, bool>? to = null)
    {
        foreach (Session session in Sessions)
            if (to == null || to(session))
                _ = session.SendEventAsync(name, data?.DeepClone());
    }
}
