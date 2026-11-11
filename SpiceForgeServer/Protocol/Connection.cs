using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Util;

namespace SpiceForgeServer.Protocol;

/// <summary>
/// Runs one WebSocket connection: reads requests, handles them in order, and sends their responses (WebSocketApi.md,
/// Messages). A request without an id gets no response; its failure comes back as an <c>error</c> event instead.
/// </summary>
public static class Connection
{
    /// <summary>Documents and catalogues can be large; anything past this is refused.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    public static async Task Run(WebSocket socket, ServerContext context, Api api)
    {
        var session = new Session(socket);
        context.Hub.Add(session);
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult received;
                try
                {
                    received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    break;
                }
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
                message.Write(buffer, 0, received.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large.", CancellationToken.None);
                    break;
                }
                if (!received.EndOfMessage)
                    continue;

                byte[] bytes = message.ToArray();
                message.SetLength(0);
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    await session.SendEventAsync("error", ErrorJson(ErrorCodes.BadRequest, "Requests are text frames.", null));
                    continue;
                }
                bool keepOpen = await HandleMessage(session, Encoding.UTF8.GetString(bytes), context, api);
                if (!keepOpen)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The first message must be 'hello'.", CancellationToken.None);
                    break;
                }
            }
        }
        finally
        {
            context.Hub.Remove(session);
            context.Files.Forget(session);
        }
    }

    // Returns false when the connection should close: a first message that isn't hello.
    private static async Task<bool> HandleMessage(Session session, string text, ServerContext context, Api api)
    {
        JsonNode? id = null;
        string op = "";
        try
        {
            if (JsonNode.Parse(text) is not JsonObject request)
                throw new ApiException(ErrorCodes.BadRequest, "A request must be a JSON object.");
            id = request["id"]?.DeepClone();
            op = request["op"] is JsonValue v && v.TryGetValue(out string? name) ? name
                : throw new ApiException(ErrorCodes.BadRequest, "A request needs an 'op'.");
            if (request["args"] is not (null or JsonObject))
                throw new ApiException(ErrorCodes.BadRequest, "'args' must be an object.");

            bool wasGreeted = session.Greeted;
            JsonNode? result = await api.Handle(session, op, new Args(request["args"] as JsonObject));
            if (id != null)
                await session.SendAsync(new JsonObject { ["re"] = id, ["result"] = result });
            if (!wasGreeted && session.Greeted)
                context.Log.Write(MessageType.Info, "server", $"Client {session.Id} connected: '{session.ClientName}'.");
            return true;
        }
        catch (Exception ex)
        {
            (string code, string message, JsonNode? details) = ex switch
            {
                ApiException api2 => (api2.Code, api2.Message, api2.Details),
                JsonException json => (ErrorCodes.BadRequest, $"That isn't JSON: {json.Message}", null),
                _ => (ErrorCodes.Internal, ex.ToString(), null),
            };
            if (code == ErrorCodes.Internal)
                context.Log.Write(MessageType.Error, "server", $"'{op}' failed: {ex}");
            JsonObject error = ErrorJson(code, message, details);
            if (id != null)
                await session.SendAsync(new JsonObject { ["re"] = id, ["error"] = error });
            else
            {
                error["op"] = op;
                await session.SendEventAsync("error", error);
            }
            return session.Greeted;
        }
    }

    private static JsonObject ErrorJson(string code, string message, JsonNode? details)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (details != null)
            error["details"] = details;
        return error;
    }
}
