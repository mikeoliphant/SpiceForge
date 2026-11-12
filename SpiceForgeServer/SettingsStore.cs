using System.Text.Json;
using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer;

/// <summary>
/// The server's persistent settings (WebSocketApi.md, Session and settings): recent files, log verbosity, simulation
/// defaults, the user's component folders, and the client's own opaque state. Saved as JSON after every change.
/// </summary>
public sealed class SettingsStore
{
    public const int MaxRecentFiles = 20;
    public const int MaxClientBytes = 64 * 1024;

    private readonly string path;
    private readonly object sync = new();
    private JsonObject data;

    public SettingsStore(string path)
    {
        this.path = path;
        data = Load(path);
    }

    public string Path => path;

    public JsonObject Snapshot()
    {
        lock (sync)
            return new JsonObject
            {
                ["recentFiles"] = Json.Strings(RecentFiles),
                ["audio"] = data["audio"]?.DeepClone() ?? new JsonObject(),
                ["logVerbosity"] = LogVerbosity,
                ["simulationDefaults"] = SimulationDefaults,
                ["componentFolders"] = Json.Strings(ComponentFolders),
                ["client"] = data["client"]?.DeepClone(),
            };
    }

    public IReadOnlyList<string> RecentFiles
    {
        get
        {
            lock (sync)
                return (data["recentFiles"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        }
    }

    /// <summary>Folders of component libraries the user added, loaded after the server's own.</summary>
    public IReadOnlyList<string> ComponentFolders
    {
        get
        {
            lock (sync)
                return (data["componentFolders"] as JsonArray)?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? new List<string>();
        }
    }

    public string LogVerbosity
    {
        get { lock (sync) return data["logVerbosity"]?.GetValue<string>() ?? "info"; }
    }

    /// <summary>SpiceForge's plugin defaults (see WebSocketApi.md, Simulation), unless the user changed them.</summary>
    public JsonObject SimulationDefaults
    {
        get
        {
            lock (sync)
                return data["simulationDefaults"]?.DeepClone() as JsonObject ?? new JsonObject
                {
                    ["oversample"] = 4,
                    ["iterationCap"] = 32,
                    ["resampling"] = "sinc",
                    ["potSmoothingMs"] = 5,
                    ["switchSmoothingMs"] = 5,
                };
        }
    }

    /// <summary>The saved audio configuration (see <c>audio.setConfig</c>), or null if there's none.</summary>
    public JsonObject? Audio
    {
        get { lock (sync) return data["audio"]?.DeepClone() as JsonObject; }
    }

    public void SetAudio(JsonObject audio)
    {
        lock (sync)
        {
            data["audio"] = audio.DeepClone();
            Save();
        }
    }

    /// <summary>Moves a file to the front of the recent files.</summary>
    public void Used(string file) => UpdateRecent(list =>
    {
        list.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, file);
        if (list.Count > MaxRecentFiles)
            list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);
    });

    public void Forget(string file) => UpdateRecent(list => list.RemoveAll(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase)));

    private void UpdateRecent(Action<List<string>> change)
    {
        lock (sync)
        {
            List<string> list = RecentFiles.ToList();
            change(list);
            data["recentFiles"] = Json.Strings(list);
            Save();
        }
    }

    /// <summary>Applies <c>settings.set</c>: any of logVerbosity, simulationDefaults, componentFolders and client.</summary>
    public void Set(Args args)
    {
        // Checked before anything changes, so a bad folder leaves the settings as they were.
        List<string>? folders = null;
        if (args.Has("componentFolders"))
        {
            folders = new List<string>();
            foreach (string folder in args.Strings("componentFolders"))
            {
                if (!System.IO.Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                    throw new ApiException(ErrorCodes.InvalidValue, $"There's no folder '{folder}'.");
                string full = System.IO.Path.GetFullPath(folder);
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase))
                    folders.Add(full);
            }
        }
        lock (sync)
        {
            if (folders != null)
                data["componentFolders"] = Json.Strings(folders);
            if (args.OptionalString("logVerbosity") is string level)
            {
                LogHub.ParseLevel(level);
                data["logVerbosity"] = level;
            }
            if (args.OptionalObject("simulationDefaults") is JsonObject defaults)
                data["simulationDefaults"] = defaults.DeepClone();
            if (args.Has("client"))
            {
                JsonNode? client = args["client"];
                if (client != null && client.ToJsonString().Length > MaxClientBytes)
                    throw new ApiException(ErrorCodes.InvalidValue, $"The client state must be under {MaxClientBytes / 1024} KB.");
                data["client"] = client?.DeepClone();
            }
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string temp = path + ".temp";
            File.WriteAllText(temp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Couldn't save the settings to '{path}': {ex.Message}");
        }
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject loaded)
                return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Console.Error.WriteLine($"Couldn't read the settings from '{path}', starting afresh: {ex.Message}");
        }
        return new JsonObject();
    }
}
