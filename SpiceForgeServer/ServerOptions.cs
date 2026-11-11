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

    /// <summary>Where the catalogue's libraries come from: LiveSPICE's, copied next to the server, then the user's.</summary>
    public IReadOnlyList<string> ComponentFolders { get; init; } = Array.Empty<string>();

    /// <summary>The folder the front end is served from: <c>--webRoot</c>, else <c>wwwroot</c> next to the server, else
    /// (while developing) SpiceForgeWebUI/dist in the repository. Null if there's none, and only the API is served.</summary>
    public string? WebRoot { get; init; }

    /// <summary>Whether to open the front end in the default browser once the server starts (<c>--open true</c>).</summary>
    public bool OpenBrowser { get; init; }

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
            OpenBrowser = bool.TryParse(configuration["open"], out bool open) && open,
            ComponentFolders = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Components"),
                Path.Combine(userFolder, "Components"),
            },
        };
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
