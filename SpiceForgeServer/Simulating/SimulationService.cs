using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Simulating;

/// <summary>
/// The <c>sim.*</c> operations (WebSocketApi.md, Simulation). At most one simulation runs at a time, as on an audio
/// device; there's no device yet, so it runs on its own real-time clock (see <see cref="Simulation"/>).
/// </summary>
public sealed class SimulationService : IDisposable
{
    private readonly ServerContext context;
    private readonly object sync = new();
    private Simulation? current;
    private int next = 1;

    public SimulationService(ServerContext context) => this.context = context;

    public async Task<JsonNode?> Start(Session session, Args args)
    {
        JsonObject document = args.Object("document");
        string title = args.OptionalString("title") ?? "Untitled";
        bool replace = args.Bool("replace", false);
        JsonObject settings = args.OptionalObject("settings") ?? context.Settings.SimulationDefaults;
        JsonObject check = await Engine.Run(() => Simulation.Check(document));

        Simulation simulation;
        lock (sync)
        {
            if (current != null && !current.IsStopped && !replace)
                throw new ApiException(ErrorCodes.AudioBusy, $"Simulation {current.Id} is running; stop it, or start with replace.");
            current?.Stop();
            simulation = new Simulation(context, next++, title, (JsonObject)document.DeepClone(), check, (JsonObject)settings.DeepClone());
            current = simulation;
        }
        // Scope streams belonged to the old simulation.
        foreach (Session s in context.Hub.Sessions)
            s.ScopeSimulation = null;
        simulation.Start();
        return simulation.ToJson();
    }

    private Simulation Get(Args args)
    {
        string id = args.String("sim");
        lock (sync)
            return current != null && current.Id == id && !current.IsStopped ? current
                : throw new ApiException(ErrorCodes.NotFound, $"There's no running simulation '{id}'.");
    }

    public async Task<JsonNode?> Update(Session session, Args args)
    {
        Simulation simulation = Get(args);
        JsonObject document = args.Object("document");
        JsonObject check = await Engine.Run(() => Simulation.Check(document));
        simulation.Update((JsonObject)document.DeepClone(), check);
        return null;
    }

    public Task<JsonNode?> Configure(Session session, Args args)
    {
        Get(args).Configure((JsonObject)args.Object("settings").DeepClone());
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> Restart(Session session, Args args)
    {
        Get(args).Restart();
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> GetJson(Session session, Args args) => Task.FromResult<JsonNode?>(Get(args).ToJson());

    public Task<JsonNode?> Stop(Session session, Args args)
    {
        Simulation simulation = Get(args);
        simulation.Stop();
        foreach (Session s in context.Hub.Sessions)
            if (s.ScopeSimulation == simulation.Id)
                s.ScopeSimulation = null;
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> Set(Session session, Args args)
    {
        Get(args).Set(args.OptionalObject("pots"), args.OptionalObject("switches"));
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> Click(Session session, Args args) =>
        Task.FromResult<JsonNode?>(new JsonObject { ["position"] = Get(args).Click(args.String("switch")) });

    public async Task<JsonNode?> SetInput(Session session, Args args)
    {
        await Get(args).SetInput(args.String("element"), args.Object("source"));
        return null;
    }

    public Task<JsonNode?> SetGain(Session session, Args args)
    {
        static double? Db(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? d : null;
        Get(args).SetGain(Db(args["inputDb"]), Db(args["outputDb"]));
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> Probe(Session session, Args args) =>
        Task.FromResult<JsonNode?>(Get(args).AddProbe(args.Object("target"), args.OptionalString("color")));

    public Task<JsonNode?> RemoveProbes(Session session, Args args)
    {
        Get(args).RemoveProbes(args.Strings("probes"));
        return Task.FromResult<JsonNode?>(null);
    }

    public Task<JsonNode?> Scope(Session session, Args args)
    {
        Simulation simulation = Get(args);
        session.ScopeSimulation = args.Bool("enabled", true) ? simulation.Id : null;
        return Task.FromResult<JsonNode?>(null);
    }

    public void Dispose()
    {
        lock (sync)
            current?.Stop();
    }
}
