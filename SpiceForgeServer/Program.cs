using System.Net;
using SpiceForgeServer;
using SpiceForgeServer.Protocol;

// SpiceForgeServer: the WebSocket server a local web front end edits schematics through (DesignDocs/WebSocketApi.md).
// So far it does the editing side: the catalogue, symbol layouts, value checks, files and circuit checks. Audio and
// simulation answer `unsupported`.

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddEnvironmentVariables("SPICEFORGE_");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

ServerOptions options;
try
{
    options = ServerOptions.From(builder.Configuration);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

// Loopback only: this API reads and writes files, so it must never be reachable from another machine.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

using var context = new ServerContext(options);
var api = new Api(context);

WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/", () => Results.Text("SpiceForgeServer. Connect a WebSocket to /api?token=<token>.\n"));

app.Map("/api", async (HttpContext http) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsync("This endpoint takes WebSocket connections.");
        return;
    }
    // Any web page can try to open a socket to localhost, so a connection must carry the token, and a browser's must
    // come from an allowed origin.
    if (!CryptographicEquals(http.Request.Query["token"].ToString(), options.Token))
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!options.OriginAllowed(http.Request.Headers.Origin.ToString()))
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    using System.Net.WebSockets.WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
    await Connection.Run(socket, context, api);
});

Console.WriteLine($"SpiceForgeServer listening on ws://127.0.0.1:{options.Port}/api?token={options.Token}");
Console.WriteLine($"Settings: {options.SettingsPath}");
await app.RunAsync();
return 0;

static bool CryptographicEquals(string a, string b) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
