using System.Net;
using SpiceForgeServer;
using SpiceForgeServer.Audio;
using SpiceForgeServer.Protocol;

// SpiceForgeServer: the WebSocket server a local web front end edits and simulates schematics through
// (DesignDocs/WebSocketApi.md): the catalogue, symbol layouts, value checks, files and circuit checks; simulations, on an
// ASIO device or the internal clock. It also serves the front end (SpiceForgeWebUI's build) from its web root, so the
// page and the API share an origin.

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

if (options.ListAudio)
    return await ListAudio(options);

// Loopback only: this API reads and writes files, so it must never be reachable from another machine.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

using var context = new ServerContext(options);
var api = new Api(context);

WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

if (options.WebRoot != null)
{
    var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(options.WebRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = files,
        // The front end is rebuilt often while it's developed; never serve a stale copy.
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
    });
}
else
{
    app.MapGet("/", () => Results.Text("SpiceForgeServer. No front end found: build SpiceForgeWebUI, or pass --webRoot.\n" +
        "Connect a WebSocket to /api?token=<token>.\n"));
}

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

string page = $"http://127.0.0.1:{options.Port}/?token={options.Token}";
// Announced only once the port is bound: if another server holds it, this one must fail, not claim to be listening.
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"SpiceForgeServer listening on ws://127.0.0.1:{options.Port}/api?token={options.Token}");
    Console.WriteLine(options.WebRoot != null ? $"Front end: {page} (from {options.WebRoot})" : "Front end: none found");
    Console.WriteLine($"Settings: {options.SettingsPath}");
    AudioConfig audio = context.Audio.Config;
    Console.WriteLine(audio.Driver == ClockDriver.Name ? "Audio: none (the internal clock)"
        : $"Audio: {audio.Driver}, {(audio.Device != null ? $"'{audio.Device}'" : "the first device")}, opened when a simulation starts");
    if (options.OpenBrowser && options.WebRoot != null)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine($"Couldn't open a browser: {ex.Message}"); }
    }
});
try
{
    await app.RunAsync();
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Couldn't listen on 127.0.0.1:{options.Port}: {ex.Message} (Is another server running? Use --port.)");
    return 1;
}
return 0;

// --listAudio: every driver's devices, with their channels, rates and buffer sizes.
static async Task<int> ListAudio(ServerOptions options)
{
    var settings = new SettingsStore(options.SettingsPath);
    using var audio = new AudioService(new Hub(), new LogHub(new Hub()), settings, AudioConfig.FromJson(settings.Audio));
    foreach (AudioDriver driver in audio.Drivers)
    {
        Console.WriteLine($"{driver.DriverName}:");
        foreach (string device in await driver.Devices())
        {
            try
            {
                AudioDeviceInfo info = await driver.Info(device);
                Console.WriteLine($"  {device}");
                Console.WriteLine($"    inputs:       {(info.Inputs.Count > 0 ? string.Join(", ", info.Inputs) : "(none)")}");
                Console.WriteLine($"    outputs:      {(info.Outputs.Count > 0 ? string.Join(", ", info.Outputs) : "(none)")}");
                Console.WriteLine($"    sample rates: {string.Join(", ", info.SampleRates)} (now {info.DefaultSampleRate})");
                Console.WriteLine($"    buffer sizes: {string.Join(", ", info.BufferSizes)} (preferred {info.DefaultBufferSize})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {device}: {ex.Message}");
            }
        }
    }
    return 0;
}

static bool CryptographicEquals(string a, string b) =>
    System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
