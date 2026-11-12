using System.Text.Json.Nodes;
using SpiceForgeServer.Audio;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Simulating;

/// <summary>
/// The <c>sim.*</c> operations (WebSocketApi.md, Simulation). At most one simulation runs at a time, on the audio
/// device (see <see cref="AudioService"/>): starting one attaches it to the device, and stopping it detaches it.
/// </summary>
public sealed class SimulationService : IDisposable
{
    private readonly ServerContext context;
    private readonly object sync = new();
    private readonly SemaphoreSlim starting = new(1, 1);
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
        await starting.WaitAsync();
        try
        {
            Simulation? old;
            lock (sync)
            {
                if (current != null && !current.IsStopped && !replace)
                    throw new ApiException(ErrorCodes.AudioBusy, $"Simulation {current.Id} is running; stop it, or start with replace.");
                if (context.Audio.Client is AudioTest && !replace)
                    throw new ApiException(ErrorCodes.AudioBusy, "The audio test is running; stop it, or start with replace.");
                old = current;
                old?.Stop();
                simulation = new Simulation(context, next++, title, (JsonObject)document.DeepClone(), check, (JsonObject)settings.DeepClone());
                current = simulation;
            }
            if (old != null)
                await context.Audio.Detach(old);
            await context.Audio.StopTest();
            // Scope streams belonged to the old simulation.
            foreach (Session s in context.Hub.Sessions)
                s.ScopeSimulation = null;
            // Attached first, so it builds for the device's rate.
            await context.Audio.Attach(simulation);
            simulation.Start();
        }
        finally
        {
            starting.Release();
        }
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

    public async Task<JsonNode?> Stop(Session session, Args args)
    {
        Simulation simulation = Get(args);
        simulation.Stop();
        foreach (Session s in context.Hub.Sessions)
            if (s.ScopeSimulation == simulation.Id)
                s.ScopeSimulation = null;
        await context.Audio.Detach(simulation);
        return null;
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

    public Task<JsonNode?> SetOutput(Session session, Args args)
    {
        Get(args).SetOutput(args.String("channel"), args.Object("signal"));
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
        Simulation? simulation;
        lock (sync)
            simulation = current;
        simulation?.Stop();
        if (simulation != null)
            context.Audio.Detach(simulation).Wait(TimeSpan.FromSeconds(5));
    }
}
