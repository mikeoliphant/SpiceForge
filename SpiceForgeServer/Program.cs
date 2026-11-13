using System.Net;
using SpiceForgeServer;
using SpiceForgeServer.Audio;
using SpiceForgeServer.Protocol;

// SpiceForgeServer: the WebSocket server a local web front end edits and simulates schematics through
// (DesignDocs/WebSocketApi.md): the catalogue, symbol layouts, value checks, files and circuit checks; simulations, on an
// ASIO device or the internal clock. It also serves the front end (SpiceForgeWebUI's build) from its web root, so the
// page and the API share an origin.

// appsettings.json is read from beside the server (a package sets `"open": "app"` there), wherever it's started from.
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
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

// Without a console (the packaged Windows server), what would go to it goes to a log file in the settings folder.
if (options.LogFile is string logFile)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        var log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);
    }
    catch (IOException) { }
}

if (options.ListAudio)
    return await ListAudio(options);

// What the About dialog shows, in brief: the version and commit, copyright, license and source, and where the
// third-party licenses are.
Console.WriteLine(About.Summary());

// Launched again while a server is running: open another editor onto it, instead of failing on the port.
string instancePath = ServerInstance.PathFor(options);
string profileFolder = Path.Combine(Path.GetDirectoryName(options.SettingsPath) ?? ".", "editor-window");
if (options.Open != OpenMode.None && ServerInstance.Read(instancePath) is { } running && await running.IsAlive())
{
    Console.WriteLine($"SpiceForgeServer is already running (process {running.ProcessId}): opening its editor in {EditorWindow.Open(running.Page, options.Open, profileFolder)}.");
    return 0;
}

// Loopback only: this API reads and writes files, so it must never be reachable from another machine.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

using var context = new ServerContext(options);
var api = new Api(context);

WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// The license texts the About dialog shows (About.cs), beside the server.
if (Directory.Exists(About.LicenseFolder))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(About.LicenseFolder),
        RequestPath = "/licenses",
    });
}

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

// For a second launch to tell a running SpiceForgeServer from anything else on the port.
app.MapGet("/health", () => Results.Text("SpiceForgeServer"));

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
    // To the console and the editor's log alike.
    if (UnoptimizedBuild() is string slow)
        context.Log.Write(Util.MessageType.Warning, "server", slow);
    new ServerInstance(options.Port, options.Token, Environment.ProcessId).Save(instancePath);
    if (options.Open != OpenMode.None && options.WebRoot != null)
    {
        try { Console.WriteLine($"Editor: opened in {EditorWindow.Open(page, options.Open, profileFolder)}"); }
        catch (Exception ex) { Console.Error.WriteLine($"Couldn't open the editor: {ex.Message}"); }
    }
    if (options.ExitWhenIdle is TimeSpan grace)
        _ = ExitWhenIdle(app, context, grace);
});
app.Lifetime.ApplicationStopping.Register(() => ServerInstance.Remove(instancePath, Environment.ProcessId));
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

// Stops the server once no editor has been connected for the grace period (closing the editor's window quits it), or,
// if none ever connects, after two minutes. A reload reconnects well within the grace.
static async Task ExitWhenIdle(WebApplication app, ServerContext context, TimeSpan grace)
{
    DateTime started = DateTime.UtcNow;
    DateTime? emptySince = null;
    bool connected = false;
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        await Task.Delay(250);
        if (context.Hub.Connected > 0)
        {
            connected = true;
            emptySince = null;
            continue;
        }
        emptySince ??= DateTime.UtcNow;
        if (connected ? DateTime.UtcNow - emptySince >= grace : DateTime.UtcNow - started >= TimeSpan.FromMinutes(2))
        {
            Console.WriteLine(connected ? "The editor has closed: stopping." : "No editor connected: stopping.");
            app.Lifetime.StopApplication();
            return;
        }
    }
}

// A Debug build of SpiceForge runs without JIT optimizations: simulations run four to five times slower than Release
// (the SD-1 at 25 to 27% of real time, against 6%). Null for an optimized build.
static string? UnoptimizedBuild()
{
    static bool Unoptimized(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>().Any(d => d.IsJITOptimizerDisabled);
    bool engine = Unoptimized(typeof(SpiceForge.SchematicProcessor).Assembly);
    bool server = Unoptimized(typeof(ServerContext).Assembly);
    if (!engine && !server)
        return null;
    return "this is a Debug build, without JIT optimizations, so simulations run several times slower than in Release. " +
        "Build with -c Release (dotnet run -c Release --project SpiceForgeServer).";
}

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
