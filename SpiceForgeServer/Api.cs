using System.Reflection;
using System.Text.Json.Nodes;
using SpiceForgeServer.Catalogue;
using SpiceForgeServer.Documents;
using SpiceForgeServer.Protocol;
using SpiceForgeServer.Simulating;
using Util;

namespace SpiceForgeServer;

/// <summary>Everything the handlers share: the settings, the log, the catalogue, files, and the sessions.</summary>
public sealed class ServerContext : IDisposable
{
    public ServerOptions Options { get; }
    public Hub Hub { get; } = new();
    public LogHub Log { get; }
    public SettingsStore Settings { get; }
    public FileService Files { get; }
    public SimulationService Simulations { get; }
    private Catalog? catalog;

    public ServerContext(ServerOptions options)
    {
        Options = options;
        Log = new LogHub(Hub);
        Settings = new SettingsStore(options.SettingsPath);
        Files = new FileService(Settings, Log, Hub);
        Simulations = new SimulationService(this);
    }

    /// <summary>The catalogue, loaded on first use (it takes a moment) and again on <c>catalog.refresh</c>.</summary>
    public Catalog Catalog(bool reload = false)
    {
        if (catalog == null || reload)
            catalog = new Catalog(ComponentFolders, Log.For("catalog"));
        return catalog;
    }

    /// <summary>The folders the catalogue loads: the server's own, then the user's.</summary>
    public IReadOnlyList<string> ComponentFolders =>
        Options.ComponentFolders.Concat(Settings.ComponentFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Dispose()
    {
        Simulations.Dispose();
        Files.Dispose();
    }
}

/// <summary>The operations of WebSocketApi.md, by name.</summary>
public sealed class Api
{
    public const int Protocol = 1;

    private delegate Task<JsonNode?> Handler(Session session, Args args);

    private readonly ServerContext context;
    private readonly Dictionary<string, Handler> handlers;

    // Operations the spec defines that this server doesn't do yet: they answer `unsupported` rather than `unknown_op`.
    private static readonly string[] NotYet =
    {
        "audio.devices", "audio.getConfig", "audio.setConfig", "audio.test", "sim.setOutput",
    };

    public Api(ServerContext context)
    {
        this.context = context;
        handlers = new Dictionary<string, Handler>
        {
            ["hello"] = Hello,
            ["settings.get"] = (_, _) => Done(context.Settings.Snapshot()),
            ["settings.set"] = SettingsSet,
            ["fs.list"] = (_, a) => Done(context.Files.List(a.String("path"), a.Has("extensions") ? a.Strings("extensions") : null)),
            ["fs.recent"] = (_, _) => Done(new JsonObject { ["files"] = Json.Strings(context.Settings.RecentFiles) }),
            ["file.read"] = (_, a) => Engine.Run<JsonNode?>(() => context.Files.Read(a.String("path"))),
            ["file.write"] = (_, a) => Engine.Run<JsonNode?>(() =>
                context.Files.Write(a.String("path"), a.Object("document"), a.Bool("overwrite", true), a.OptionalString("expectModified"))),
            ["file.watch"] = FileWatch,
            ["schx.toXml"] = (_, a) => Engine.Run<JsonNode?>(() => new JsonObject { ["xml"] = SchxConverter.ToClipboardXml(a.Array("elements")) }),
            ["schx.fromXml"] = (_, a) => Engine.Run<JsonNode?>(() => FromXml(a.String("xml"))),
            ["catalog.get"] = (_, _) => Engine.Run<JsonNode?>(() => context.Catalog().ToJson()),
            ["catalog.refresh"] = CatalogRefresh,
            ["catalog.layout"] = (_, a) => Engine.Run<JsonNode?>(() => Layouts(a.Array("components"))),
            ["values.check"] = (_, a) => Engine.Run<JsonNode?>(() => CheckValues(a.Array("items"))),
            ["circuit.check"] = (_, a) => Engine.Run<JsonNode?>(() => CircuitChecker.Check(a.Object("document"))),
            ["log.history"] = (_, a) => Done(new JsonObject { ["entries"] = context.Log.History(Math.Clamp(a.Int("limit", 200), 0, LogHub.HistoryLength)) }),
            ["log.setVerbosity"] = LogSetVerbosity,
            ["sim.start"] = context.Simulations.Start,
            ["sim.update"] = context.Simulations.Update,
            ["sim.configure"] = context.Simulations.Configure,
            ["sim.restart"] = context.Simulations.Restart,
            ["sim.get"] = context.Simulations.GetJson,
            ["sim.stop"] = context.Simulations.Stop,
            ["sim.set"] = context.Simulations.Set,
            ["sim.click"] = context.Simulations.Click,
            ["sim.setInput"] = context.Simulations.SetInput,
            ["sim.setGain"] = context.Simulations.SetGain,
            ["sim.probe"] = context.Simulations.Probe,
            ["sim.removeProbes"] = context.Simulations.RemoveProbes,
            ["sim.scope"] = context.Simulations.Scope,
        };
    }

    public bool Knows(string op) => handlers.ContainsKey(op) || NotYet.Contains(op);

    public Task<JsonNode?> Handle(Session session, string op, Args args)
    {
        if (!session.Greeted && op != "hello")
            throw new ApiException(ErrorCodes.BadRequest, "The first message must be 'hello'.");
        if (handlers.TryGetValue(op, out Handler? handler))
            return handler(session, args);
        if (NotYet.Contains(op))
            throw new ApiException(ErrorCodes.Unsupported, $"'{op}' isn't implemented yet: this server has no audio device so far.");
        throw new ApiException(ErrorCodes.UnknownOp, $"There's no operation '{op}'.");
    }

    private static Task<JsonNode?> Done(JsonNode? result) => Task.FromResult(result);

    private Task<JsonNode?> Hello(Session session, Args args)
    {
        int protocol = args.Int("protocol", Protocol);
        if (protocol != Protocol)
            throw new ApiException(ErrorCodes.BadRequest, $"This server speaks protocol {Protocol}, not {protocol}.");
        session.Greeted = true;
        session.ClientName = args.OptionalString("client") ?? "";
        session.Verbosity = LogHub.ParseLevel(context.Settings.LogVerbosity);
        string version = typeof(Api).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        return Done(new JsonObject
        {
            ["protocol"] = Protocol,
            ["server"] = new JsonObject { ["name"] = "SpiceForgeServer", ["version"] = version },
            ["capabilities"] = new JsonObject
            {
                ["simulation"] = true,
                ["audio"] = false,
                ["maxInputs"] = 1,
                ["resampling"] = Json.Strings(new[] { "linear", "sinc" }),
                ["oversample"] = new JsonObject { ["min"] = 1, ["max"] = 64 },
                ["iterationCap"] = new JsonObject { ["min"] = 1, ["max"] = 127 },
                ["expressions"] = true,
            },
            ["simulationDefaults"] = context.Settings.SimulationDefaults,
            ["folders"] = new JsonObject
            {
                ["user"] = context.Options.UserFolder,
                ["components"] = Json.Strings(context.ComponentFolders),
            },
        });
    }

    private async Task<JsonNode?> SettingsSet(Session session, Args args)
    {
        context.Settings.Set(args);
        JsonObject snapshot = context.Settings.Snapshot();
        context.Hub.Broadcast("settings.changed", snapshot);
        // New component folders: the catalogue reloads with them, as catalog.refresh does.
        if (args.Has("componentFolders"))
            context.Hub.Broadcast("catalog.changed", await Engine.Run(() => context.Catalog(reload: true).ToJson()));
        return snapshot;
    }

    private Task<JsonNode?> FileWatch(Session session, Args args)
    {
        context.Files.Watch(session, args.Strings("paths"));
        return Done(null);
    }

    private async Task<JsonNode?> CatalogRefresh(Session session, Args args)
    {
        JsonObject catalog = await Engine.Run(() => context.Catalog(reload: true).ToJson());
        context.Hub.Broadcast("catalog.changed", catalog);
        return catalog;
    }

    private Task<JsonNode?> LogSetVerbosity(Session session, Args args)
    {
        string level = args.String("level");
        session.Verbosity = LogHub.ParseLevel(level);
        context.Settings.Set(new Args(new JsonObject { ["logVerbosity"] = level }));
        return Done(null);
    }

    private static JsonObject FromXml(string xml)
    {
        System.Xml.Linq.XElement root;
        try
        {
            root = System.Xml.Linq.XElement.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ApiException(ErrorCodes.InvalidValue, $"That isn't schematic XML: {ex.Message}");
        }
        try
        {
            return new JsonObject { ["elements"] = SchxConverter.ToElements(root.Elements("Element"), SchxConverter.ResolveComponent) };
        }
        catch (FormatException ex)
        {
            throw new ApiException(ErrorCodes.InvalidValue, $"That isn't schematic XML: {ex.Message}");
        }
    }

    private static JsonObject Layouts(JsonArray components)
    {
        var layouts = new JsonArray();
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is not JsonObject component)
                throw new ApiException(ErrorCodes.BadRequest, $"components[{i}] isn't an object.");
            layouts.Add(ComponentServices.Layout(component, $"components[{i}]"));
        }
        return new JsonObject { ["layouts"] = layouts };
    }

    private static JsonObject CheckValues(JsonArray items)
    {
        var results = new JsonArray();
        for (int i = 0; i < items.Count; i++)
        {
            var item = new Args(items[i] as JsonObject ?? throw new ApiException(ErrorCodes.BadRequest, $"items[{i}] isn't an object."));
            results.Add(ComponentServices.CheckValue(item.String("type"), item.String("property"), item.String("value")));
        }
        return new JsonObject { ["items"] = results };
    }
}
