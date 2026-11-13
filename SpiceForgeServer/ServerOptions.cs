using System.Security.Cryptography;

namespace SpiceForgeServer;

/// <summary>
/// How the server runs. Each setting comes from the command line (<c>--port 9000</c>), an environment variable
/// (<c>SPICEFORGE_PORT=9000</c>) or <c>appsettings.json</c>, in ASP.NET Core's usual order of precedence.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>8370: 'S' (83) and 'F' (70), for SpiceForge.</summary>
    public const int DefaultPort = 8370;

    public int Port { get; init; } = DefaultPort;

    /// <summary>The token every connection must present. A fresh random one each run unless given.</summary>
    public string Token { get; init; } = NewToken();

    /// <summary>Browser origins allowed to connect, besides the server's own (http://127.0.0.1:port and
    /// http://localhost:port). Clients that send no Origin, which aren't browsers, are allowed.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = Array.Empty<string>();

    public string SettingsPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpiceForge", "server-settings.json");

    /// <summary>LiveSPICE's user folder, Documents/LiveSPICE, where the client's dialogs start.</summary>
    public string UserFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LiveSPICE");

    /// <summary>LiveSPICE's example schematics: the Examples folder a package has beside the server, or, run from the
    /// repository, the LiveSPICE submodule's. Null if neither is there. The client's dialogs offer it as a place.</summary>
    public string? ExamplesFolder { get; init; } = FindExamples();

    /// <summary>Where the catalogue's libraries come from: LiveSPICE's, copied next to the server, then the user's.</summary>
    public IReadOnlyList<string> ComponentFolders { get; init; } = Array.Empty<string>();

    /// <summary>The folder the front end is served from: <c>--webRoot</c>, else <c>wwwroot</c> next to the server, else
    /// (while developing) SpiceForgeWebUI/dist in the repository. Null if there's none, and only the API is served.</summary>
    public string? WebRoot { get; init; }

    /// <summary>Where to open the front end once the server starts: <c>--open browser</c> (or <c>true</c>), the default
    /// browser; <c>--open app</c>, a window of its own (a Chromium browser's app mode, else the default browser).</summary>
    public OpenMode Open { get; init; }

    /// <summary><c>--exitWhenIdle seconds</c>: stop the server once no editor has been connected for this long (and, if
    /// none ever connects, after two minutes), so closing the editor's window quits it. 5 s with <c>--open app</c>, and
    /// off otherwise; 0 turns it off.</summary>
    public TimeSpan? ExitWhenIdle { get; init; }

    /// <summary><c>--logFile path</c>: where the console's output goes instead (relative to the settings folder), for a
    /// server without a console. Rewritten each run.</summary>
    public string? LogFile { get; init; }

    // The audio configuration for this run, over the saved one (see AudioService.Startup). Null: the saved value.

    /// <summary><c>--audio</c>: the driver, <c>None</c> or <c>ASIO</c>.</summary>
    public string? AudioDriver { get; init; }

    /// <summary><c>--audioDevice</c>: the device (for ASIO, the driver's name).</summary>
    public string? AudioDevice { get; init; }

    /// <summary><c>--audioInputs</c>, <c>--audioOutputs</c>: channel names, comma-separated; empty for none.</summary>
    public IReadOnlyList<string>? AudioInputs { get; init; }

    public IReadOnlyList<string>? AudioOutputs { get; init; }

    /// <summary><c>--sampleRate</c>, <c>--bufferSize</c>.</summary>
    public int? SampleRate { get; init; }

    public int? BufferSize { get; init; }

    /// <summary><c>--listAudio true</c>: print the audio drivers, devices and channels, and exit.</summary>
    public bool ListAudio { get; init; }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static ServerOptions From(IConfiguration configuration)
    {
        var defaults = new ServerOptions();
        string userFolder = configuration["userFolder"] ?? defaults.UserFolder;
        string? port = configuration["port"];
        int portNumber = DefaultPort;
        if (port != null && (!int.TryParse(port, out portNumber) || portNumber < 1 || portNumber > 65535))
            throw new ArgumentException($"The port must be a number from 1 to 65535, not '{port}'.");
        return new ServerOptions
        {
            Port = portNumber,
            Token = configuration["token"] is { Length: > 0 } token ? token : defaults.Token,
            AllowedOrigins = (configuration["allowedOrigins"] ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            SettingsPath = configuration["settings"] ?? defaults.SettingsPath,
            UserFolder = userFolder,
            WebRoot = FindWebRoot(configuration["webRoot"]),
            Open = ParseOpen(configuration["open"]),
            ExitWhenIdle = ParseIdle(configuration["exitWhenIdle"], ParseOpen(configuration["open"])),
            LogFile = configuration["logFile"] is { Length: > 0 } logFile
                ? Path.Combine(Path.GetDirectoryName(configuration["settings"] ?? defaults.SettingsPath) ?? ".", logFile)
                : null,
            AudioDriver = AudioDriverName(configuration["audio"]),
            AudioDevice = configuration["audioDevice"],
            AudioInputs = Channels(configuration["audioInputs"]),
            AudioOutputs = Channels(configuration["audioOutputs"]),
            SampleRate = PositiveNumber(configuration["sampleRate"], "sample rate"),
            BufferSize = PositiveNumber(configuration["bufferSize"], "buffer size"),
            ListAudio = bool.TryParse(configuration["listAudio"], out bool list) && list,
            ComponentFolders = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Components"),
                Path.Combine(userFolder, "Components"),
            },
        };
    }

    private static OpenMode ParseOpen(string? configured) => configured?.ToLowerInvariant() switch
    {
        null or "" or "false" or "none" => OpenMode.None,
        "true" or "browser" => OpenMode.Browser,
        "app" or "window" => OpenMode.App,
        _ => throw new ArgumentException($"--open is browser, app or none, not '{configured}'."),
    };

    private static TimeSpan? ParseIdle(string? configured, OpenMode open)
    {
        if (configured == null)
            return open == OpenMode.App ? TimeSpan.FromSeconds(5) : null;
        return double.TryParse(configured, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds >= 0
            ? (seconds == 0 ? null : TimeSpan.FromSeconds(seconds))
            : throw new ArgumentException($"--exitWhenIdle is a number of seconds, not '{configured}'.");
    }

    private static string? AudioDriverName(string? configured)
    {
        if (configured == null)
            return null;
        return Audio.AudioService.DriverNames.FirstOrDefault(d => string.Equals(d, configured, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There's no audio driver '{configured}' here. The drivers are: {string.Join(", ", Audio.AudioService.DriverNames)}.");
    }

    private static IReadOnlyList<string>? Channels(string? configured) =>
        configured?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? PositiveNumber(string? configured, string what)
    {
        if (configured == null)
            return null;
        return int.TryParse(configured, out int value) && value > 0 ? value
            : throw new ArgumentException($"The {what} must be a positive whole number, not '{configured}'.");
    }

    private static string? FindWebRoot(string? configured)
    {
        if (configured != null)
            return Directory.Exists(configured) ? Path.GetFullPath(configured) : throw new ArgumentException($"There's no web root folder '{configured}'.");
        string beside = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(beside, "index.html")))
            return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string dist = Path.Combine(dir.FullName, "SpiceForgeWebUI", "dist");
            if (File.Exists(Path.Combine(dist, "index.html")))
                return dist;
        }
        return null;
    }

    private static string? FindExamples()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "Examples");
        if (Directory.Exists(beside))
            return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string examples = Path.Combine(dir.FullName, "Dependencies", "LiveSPICE", "Tests", "Examples");
            if (Directory.Exists(examples))
                return examples;
        }
        return null;
    }

    public bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return true;
        string trimmed = origin.TrimEnd('/');
        return trimmed.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase)
            || AllowedOrigins.Any(o => o.TrimEnd('/').Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Where the front end opens when the server starts.</summary>
public enum OpenMode
{
    None,
    /// <summary>A tab of the default browser.</summary>
    Browser,
    /// <summary>A window of its own: a Chromium browser's app mode (no tabs or address bar), else the default browser.</summary>
    App,
}
