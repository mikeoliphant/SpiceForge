using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Circuit;
using SpiceForge;
using SpiceForgeServer.Documents;
using SpiceForgeServer.Protocol;
using Util;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForgeServer.Simulating;

/// <summary>
/// One simulation (WebSocketApi.md, Simulation): a <see cref="SchematicProcessor"/> built from a client's document, run
/// in real time on its own thread. There's no audio device yet, so the thread is the clock: it processes a block
/// whenever the wall clock says one is due, driving the Input from a test signal and sending the output only to the
/// meters, while probes stream node voltages to the scope.
/// </summary>
public sealed class Simulation
{
    public const double SampleRate = 48000;
    private const int Block = 256;
    private const int ScopeRate = 60;
    private const int LevelRate = 10;
    private const double LevelDecaySeconds = 0.25;

    private static readonly string[] Colors = { "red", "green", "blue", "yellow", "cyan", "magenta", "orange" };

    private readonly ServerContext context;
    private readonly object sync = new();
    private readonly Thread thread;
    private volatile bool stopping;

    public string Id { get; }
    public int Number { get; }
    public string Title { get; }

    // Everything below is guarded by sync, except where noted.
    private JsonObject document;
    private JsonObject check;
    private JsonObject settings;
    private string state = "building";
    private string? code, message;
    private int buildVersion;
    private volatile SchematicProcessor? processor;
    private Dictionary<string, int> probeIndex = new();
    private readonly Dictionary<string, double> potPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> switchPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Probe> probes = new();
    private volatile Probe[] probeSnapshot = Array.Empty<Probe>();
    private int nextProbe = 1;
    private InputSource? source;
    private volatile Func<double, double>? sourceFunction;
    private volatile float inputGain = 1, outputGain = 1;
    private double inputDb, outputDb;
    private DateTime runningSince;

    // Monitoring, written by the run thread.
    private long dropouts;

    private sealed record InputSource(JsonObject Json, Func<double, double>? Function);

    private sealed class Probe
    {
        public required string Id { get; init; }
        public required int Stream { get; init; }
        public required JsonObject Target { get; set; }
        public required string Node { get; set; }
        public required string Color { get; init; }

        public JsonObject ToJson() => new()
        {
            ["id"] = Id,
            ["stream"] = Stream,
            ["target"] = Target.DeepClone(),
            ["node"] = Node,
            ["color"] = Color,
            ["label"] = $"V[{Node}]",
        };
    }

    public Simulation(ServerContext context, int number, string title, JsonObject document, JsonObject check, JsonObject settings)
    {
        this.context = context;
        Number = number;
        Id = $"s{number}";
        Title = title;
        this.document = document;
        this.check = check;
        this.settings = settings;
        thread = new Thread(RunLoop) { IsBackground = true, Name = $"Simulation {Id}", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Starts building, and the thread that will run the build.</summary>
    public void Start()
    {
        thread.Start();
        _ = BuildAsync();
    }

    public void Stop()
    {
        lock (sync)
        {
            if (stopping)
                return;
            stopping = true;
            buildVersion++;
            processor = null;
        }
        SetState("stopped");
    }

    public bool IsStopped => stopping;

    // ---- Building ----

    /// <summary>Checks a document the way <c>sim.start</c> and <c>sim.update</c> need: it must build, and have at most
    /// one Input. Returns <c>circuit.check</c>'s result.</summary>
    public static JsonObject Check(JsonObject document)
    {
        JsonObject check = CircuitChecker.Check(document);
        if (check["errors"] is JsonArray errors && errors.Count > 0)
        {
            string first = errors[0]?["message"]?.GetValue<string>() ?? "";
            throw new ApiException(ErrorCodes.BuildFailed, $"The circuit doesn't build: {first}", errors.DeepClone());
        }
        int inputs = InputElements(document).Count;
        if (inputs > 1)
            throw new ApiException(ErrorCodes.Unsupported, $"The circuit has {inputs} Inputs; this server drives at most one.");
        return check;
    }

    private static List<string> InputElements(JsonObject document) =>
        (document["elements"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(e => e["kind"]?.GetValue<string>() == "symbol" && IsInput(e["component"]?["type"]?.GetValue<string>()))
            .Select(e => e["id"]?.GetValue<string>() ?? "")
            .ToList();

    private static bool IsInput(string? type) => type != null && type.Split(',')[0].Trim() is "Circuit.Input";

    /// <summary>Replaces the document (<c>sim.update</c>) and rebuilds. Controls keep their positions by name; probes
    /// whose targets remain re-attach.</summary>
    public void Update(JsonObject newDocument, JsonObject newCheck)
    {
        lock (sync)
        {
            document = newDocument;
            check = newCheck;
            ReattachProbes();
        }
        BroadcastProbes();
        _ = BuildAsync();
    }

    public void Configure(JsonObject newSettings)
    {
        lock (sync)
            settings = newSettings;
        _ = BuildAsync();
    }

    public void Restart() => _ = BuildAsync();

    private async Task BuildAsync()
    {
        int version;
        JsonObject doc, options;
        Dictionary<string, double> pots;
        Dictionary<string, int> switches;
        bool first, needSource;
        lock (sync)
        {
            if (stopping)
                return;
            version = ++buildVersion;
            doc = document;
            options = settings;
            pots = new Dictionary<string, double>(potPositions, StringComparer.OrdinalIgnoreCase);
            switches = new Dictionary<string, int>(switchPositions, StringComparer.OrdinalIgnoreCase);
            first = processor == null;
            needSource = source == null;
        }
        SetState(first ? "building" : "rebuilding");

        try
        {
            CapturingLog log = context.Log.For(Id);
            var (built, initialSource) = await Engine.Run(() =>
            {
                SchematicProcessor p = Create(doc, options, pots, switches, log);
                // The first time, the Input plays a 220 Hz sine at half its full scale, until the client sets a source.
                InputSource? initial = null;
                if (needSource && p.HasInput)
                {
                    string text = $"{Format(p.InputFullScale / 2)}*Sin(2*Pi*220*t)";
                    initial = new InputSource(new JsonObject { ["expression"] = text }, CompileSource(text));
                }
                return (p, initial);
            });

            lock (sync)
            {
                if (version != buildVersion || stopping)
                    return;
                if (source == null && initialSource != null)
                    SetSource(initialSource);
                // Positions that changed while it built.
                foreach (PotControl pot in built.Pots)
                    if (potPositions.TryGetValue(pot.Name, out double position))
                        pot.Position = position;
                foreach (SwitchControl control in built.Switches)
                    if (switchPositions.TryGetValue(control.Name, out int position) && position < control.Positions)
                        control.Position = position;
                probeIndex = built.ProbeNodes.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i);
                processor = built;
                runningSince = DateTime.UtcNow;
            }
            SetState("running");
            BroadcastControls();
        }
        catch (Exception ex)
        {
            lock (sync)
                if (version != buildVersion || stopping)
                    return;
            context.Log.Write(MessageType.Error, Id, $"Couldn't build the simulation: {ex.Message}");
            processor = null;
            SetState("error", ErrorCodes.BuildFailed, ex.Message);
        }
    }

    private SchematicProcessor Create(JsonObject doc, JsonObject options, Dictionary<string, double> pots, Dictionary<string, int> switches, ILog log)
    {
        XElement xml = SchxConverter.ToSchematic(doc);
        string title = Title;
        Circuit.Circuit Build(ILog l)
        {
            Circuit.Circuit circuit = Schematic.Deserialize(xml, l).Build(l);
            circuit.Name = title;
            return circuit;
        }
        var args = new Args(options);
        return new SchematicProcessor(Build, SampleRate, new SchematicProcessorOptions
        {
            Oversample = Math.Clamp(args.Int("oversample", 4), 1, 64),
            IterationCap = Math.Clamp(args.Int("iterationCap", 32), 1, 127),
            Resampling = args.OptionalString("resampling") == "linear" ? Resampling.Linear : Resampling.Sinc,
            PotSmoothingMilliseconds = NumberOf(options["potSmoothingMs"], 5),
            SwitchSmoothingMilliseconds = NumberOf(options["switchSmoothingMs"], 5),
            PotPositions = pots,
            SwitchPositions = switches,
            IgnoreUnknownControls = true,
            AllowNoInputOrSpeaker = true,
            Probes = true,
            Log = log,
        });
    }

    private static double NumberOf(JsonNode? node, double fallback) =>
        node is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    private static string Format(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // ---- State ----

    private void SetState(string newState, string? newCode = null, string? newMessage = null)
    {
        lock (sync)
        {
            if (stopping && newState != "stopped")
                return;
            state = newState;
            code = newCode;
            message = newMessage;
        }
        var data = new JsonObject { ["sim"] = Id, ["state"] = newState };
        if (newCode != null)
            data["code"] = newCode;
        if (newMessage != null)
            data["message"] = newMessage;
        context.Hub.Broadcast("sim.state", data);
    }

    public JsonObject ToJson()
    {
        lock (sync)
        {
            SchematicProcessor? p = processor;
            var json = new JsonObject
            {
                ["sim"] = Id,
                ["title"] = Title,
                ["state"] = state,
                ["settings"] = settings.DeepClone(),
                ["sampleRate"] = SampleRate,
                ["latency"] = p?.Latency ?? 0,
            };
            if (code != null)
                json["code"] = code;
            if (message != null)
                json["message"] = message;
            foreach (var (key, value) in ControlsJson())
                json[key] = value?.DeepClone();
            json["probes"] = Json.Array(probes.Select(pr => (JsonNode?)pr.ToJson()));
            return json;
        }
    }

    // The controls, as sim.get and sim.controls give them. Called under sync.
    private JsonObject ControlsJson()
    {
        SchematicProcessor? p = processor;
        JsonArray checkPots = check["controls"]?["pots"] as JsonArray ?? new JsonArray();
        JsonArray checkSwitches = check["controls"]?["switches"] as JsonArray ?? new JsonArray();
        JsonNode? ElementsOf(JsonArray list, string name) =>
            list.OfType<JsonObject>().FirstOrDefault(c => string.Equals(c["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))?["elements"]?.DeepClone()
            ?? new JsonArray();

        var pots = new JsonArray();
        var switches = new JsonArray();
        if (p != null)
        {
            foreach (PotControl pot in p.Pots)
                pots.Add(new JsonObject
                {
                    ["name"] = pot.Name,
                    ["position"] = pot.Position,
                    ["default"] = pot.DefaultPosition,
                    ["taper"] = pot.Taper.ToString(),
                    ["elements"] = ElementsOf(checkPots, pot.Name),
                });
            foreach (SwitchControl control in p.Switches)
                switches.Add(new JsonObject
                {
                    ["name"] = control.Name,
                    ["position"] = control.Position,
                    ["positions"] = control.Positions,
                    ["default"] = control.DefaultPosition,
                    ["elements"] = ElementsOf(checkSwitches, control.Name),
                });
        }
        else
        {
            // Before the first build, the document's own positions.
            foreach (JsonObject pot in checkPots.OfType<JsonObject>())
            {
                var c = (JsonObject)pot.DeepClone();
                c["default"] = c["position"]?.DeepClone();
                pots.Add(c);
            }
            foreach (JsonObject control in checkSwitches.OfType<JsonObject>())
            {
                var c = (JsonObject)control.DeepClone();
                c["default"] = c["position"]?.DeepClone();
                switches.Add(c);
            }
        }

        var inputs = new JsonArray();
        foreach (string element in InputElements(document))
            inputs.Add(new JsonObject
            {
                ["element"] = element,
                ["name"] = (document["elements"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(e => e["id"]?.GetValue<string>() == element)?["component"]?["properties"]?["Name"]?.DeepClone(),
                ["source"] = source?.Json.DeepClone() ?? new JsonObject { ["silence"] = true },
            });

        return new JsonObject
        {
            ["sim"] = Id,
            ["pots"] = pots,
            ["switches"] = switches,
            ["inputs"] = inputs,
            // No audio device: the output only drives the meters.
            ["outputs"] = new JsonArray(),
            ["gains"] = new JsonObject { ["inputDb"] = inputDb, ["outputDb"] = outputDb },
        };
    }

    private void BroadcastControls()
    {
        JsonObject controls;
        lock (sync)
            controls = ControlsJson();
        context.Hub.Broadcast("sim.controls", controls);
    }

    // ---- Controls ----

    public void Set(JsonObject? pots, JsonObject? switches)
    {
        lock (sync)
        {
            SchematicProcessor? p = processor;
            if (pots != null)
                foreach (var (name, value) in pots)
                {
                    double position = value is JsonValue v && v.TryGetValue(out double d) ? d
                        : throw new ApiException(ErrorCodes.InvalidValue, $"The position of pot '{name}' isn't a number.");
                    if (position < 0 || position > 1)
                        throw new ApiException(ErrorCodes.InvalidValue, $"A pot runs from 0 to 1, not {position}.");
                    if (p != null)
                        Find(p.Pots, name, c => c.Name, "pot").Position = position;
                    potPositions[name] = position;
                }
            if (switches != null)
                foreach (var (name, value) in switches)
                {
                    int position = value is JsonValue v && v.TryGetValue(out int i) ? i
                        : throw new ApiException(ErrorCodes.InvalidValue, $"The position of switch '{name}' isn't a whole number.");
                    if (p != null)
                    {
                        SwitchControl control = Find(p.Switches, name, c => c.Name, "switch");
                        if (position < 0 || position >= control.Positions)
                            throw new ApiException(ErrorCodes.InvalidValue, $"Switch '{name}' has positions 0 to {control.Positions - 1}.");
                        control.Position = position;
                    }
                    switchPositions[name] = position;
                }
        }
        BroadcastControls();
    }

    public int Click(string name)
    {
        int position;
        lock (sync)
        {
            SchematicProcessor p = processor ?? throw new ApiException(ErrorCodes.InvalidValue, "The simulation isn't running yet.");
            SwitchControl control = Find(p.Switches, name, c => c.Name, "switch");
            position = (control.Position + 1) % control.Positions;
            control.Position = position;
            switchPositions[control.Name] = position;
        }
        BroadcastControls();
        return position;
    }

    private static T Find<T>(IEnumerable<T> controls, string name, Func<T, string> nameOf, string kind) =>
        controls.FirstOrDefault(c => string.Equals(nameOf(c), name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ApiException(ErrorCodes.NotFound, $"There's no {kind} named '{name}'.");

    public async Task SetInput(string element, JsonObject sourceJson)
    {
        lock (sync)
            if (!InputElements(document).Contains(element))
                throw new ApiException(ErrorCodes.NotFound, $"Element '{element}' isn't an Input of the circuit.");
        InputSource newSource;
        if (sourceJson["silence"] is JsonValue)
            newSource = new InputSource(new JsonObject { ["silence"] = true }, null);
        else if (sourceJson["expression"] is JsonValue e && e.TryGetValue(out string? text))
        {
            Func<double, double> function = await Engine.Run(() => CompileSource(text));
            newSource = new InputSource(new JsonObject { ["expression"] = text }, function);
        }
        else if (sourceJson["channel"] != null)
            throw new ApiException(ErrorCodes.Unsupported, "This server has no audio device yet: use an expression or silence.");
        else
            throw new ApiException(ErrorCodes.InvalidValue, "A source is { \"expression\": \"...\" }, { \"channel\": \"...\" } or { \"silence\": true }.");
        lock (sync)
            SetSource(newSource);
        BroadcastControls();
    }

    private void SetSource(InputSource newSource)
    {
        source = newSource;
        sourceFunction = newSource.Function;
    }

    /// <summary>Compiles a signal in t (seconds), in volts. Uses LiveSPICE's computer algebra: call it on the engine.</summary>
    private static Func<double, double> CompileSource(string text)
    {
        Expression expression;
        ExpressionEvaluator evaluator;
        try
        {
            expression = Expression.Parse(text);
            evaluator = new ExpressionEvaluator(new[] { (Expression)Component.t }, new[] { expression });
        }
        catch (Exception ex)
        {
            throw new ApiException(ErrorCodes.InvalidValue, $"'{text}' isn't a signal in t: {ex.Message}");
        }
        var values = new double[1];
        var results = new double[1];
        double Evaluate(double t)
        {
            values[0] = t;
            evaluator.Evaluate(values, results);
            return results[0];
        }
        // It must give a number: a name other than t doesn't.
        double probe = Evaluate(0.001);
        if (double.IsNaN(probe) || double.IsInfinity(probe))
            throw new ApiException(ErrorCodes.InvalidValue, $"'{text}' doesn't give a number at t = 1 ms.");
        return Evaluate;
    }

    public void SetGain(double? inDb, double? outDb)
    {
        static double Check(double db) => db is >= -40 and <= 40 ? db
            : throw new ApiException(ErrorCodes.InvalidValue, $"Gains run from -40 to +40 dB, not {db}.");
        lock (sync)
        {
            if (inDb is double i)
            {
                inputDb = Check(i);
                inputGain = (float)Math.Pow(10, i / 20);
            }
            if (outDb is double o)
            {
                outputDb = Check(o);
                outputGain = (float)Math.Pow(10, o / 20);
            }
        }
        BroadcastControls();
    }

    // ---- Probes ----

    /// <summary>The node a target names, from <c>circuit.check</c>'s nodes, or null. Called under sync.</summary>
    private string? NodeOf(JsonObject target)
    {
        string? wire = target["wire"]?.GetValue<string>();
        string? element = target["element"]?.GetValue<string>();
        string? terminal = target["terminal"]?.GetValue<string>();
        foreach (JsonObject node in (check["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (wire != null && (node["wires"] as JsonArray)?.Any(w => w?.GetValue<string>() == wire) == true)
                return node["name"]?.GetValue<string>();
            if (element != null && (node["terminals"] as JsonArray)?.OfType<JsonObject>()
                    .Any(t => t["element"]?.GetValue<string>() == element && t["terminal"]?.GetValue<string>() == terminal) == true)
                return node["name"]?.GetValue<string>();
        }
        return null;
    }

    public JsonObject AddProbe(JsonObject target, string? color)
    {
        Probe probe;
        lock (sync)
        {
            string node = NodeOf(target) ?? throw new ApiException(ErrorCodes.NotFound, "That target isn't connected to a node.");
            // A second probe on a node that has one moves that probe.
            Probe? existing = probes.FirstOrDefault(p => p.Node == node);
            if (existing != null)
            {
                existing.Target = (JsonObject)target.DeepClone();
                probe = existing;
            }
            else
            {
                if (color != null && !Colors.Contains(color))
                    throw new ApiException(ErrorCodes.InvalidValue, $"A probe's colour is one of {string.Join(", ", Colors)}.");
                color ??= Colors.OrderBy(c => probes.Count(p => p.Color == c)).ThenBy(c => Array.IndexOf(Colors, c)).First();
                int number = nextProbe++;
                probe = new Probe { Id = $"p{number}", Stream = number, Target = (JsonObject)target.DeepClone(), Node = node, Color = color };
                probes.Add(probe);
            }
            probeSnapshot = probes.ToArray();
        }
        BroadcastProbes();
        return probe.ToJson();
    }

    public void RemoveProbes(IEnumerable<string> ids)
    {
        lock (sync)
        {
            var remove = ids.ToHashSet();
            foreach (string id in remove)
                if (!probes.Any(p => p.Id == id))
                    throw new ApiException(ErrorCodes.NotFound, $"There's no probe '{id}'.");
            probes.RemoveAll(p => remove.Contains(p.Id));
            probeSnapshot = probes.ToArray();
        }
        BroadcastProbes();
    }

    // After an update: each probe follows its target to its new node, or goes. Called under sync.
    private void ReattachProbes()
    {
        foreach (Probe probe in probes.ToArray())
        {
            string? node = NodeOf(probe.Target);
            if (node == null)
                probes.Remove(probe);
            else
                probe.Node = node;
        }
        probeSnapshot = probes.ToArray();
    }

    private void BroadcastProbes()
    {
        JsonObject data;
        lock (sync)
            data = new JsonObject { ["sim"] = Id, ["probes"] = Json.Array(probes.Select(p => (JsonNode?)p.ToJson())) };
        context.Hub.Broadcast("sim.probes", data);
    }

    // ---- Running ----

    private void RunLoop()
    {
        var inputs = new[] { new double[Block] };
        var outputs = new[] { new double[Block] };
        var clock = Stopwatch.StartNew();
        long sample = 0;               // The stream's sample index: the next sample to process.
        double start = 0;              // Wall-clock seconds at which sample 0 was due.
        SchematicProcessor? running = null;
        var scope = new Dictionary<int, List<float>>();
        long scopeStart = 0;
        int scopeEvery = (int)(SampleRate / ScopeRate), levelEvery = (int)(SampleRate / LevelRate);
        long nextScope = 0, nextLevels = 0, nextStats = 0;
        double inputPeak = 0, outputPeak = 0;
        double decay = Math.Exp(-Block / (LevelDecaySeconds * SampleRate));
        double busy = 0, statsSince = 0;

        while (!stopping)
        {
            SchematicProcessor? p = processor;
            if (p == null)
            {
                running = null;
                Thread.Sleep(5);
                continue;
            }
            if (p != running)
            {
                // A new build: carry on from now, without catching up on the time it took.
                running = p;
                start = clock.Elapsed.TotalSeconds - sample / SampleRate;
                statsSince = clock.Elapsed.TotalSeconds;
                busy = 0;
            }

            double now = clock.Elapsed.TotalSeconds;
            double due = (now - start) * SampleRate;
            if (sample + Block > due)
            {
                Thread.Sleep(1);
                continue;
            }
            if (due - sample > SampleRate / 4)
            {
                // More than a quarter second behind: skip ahead rather than spiral, and say so.
                Interlocked.Increment(ref dropouts);
                sample = (long)due - Block;
                scope.Clear();
            }

            // The input: the source in volts, in the Input's full-scale units, after the input gain.
            Func<double, double>? f = sourceFunction;
            double gain = inputGain / p.InputFullScale;
            double[] input = inputs[0];
            double blockIn = 0;
            for (int n = 0; n < Block; n++)
            {
                double x = f == null ? 0 : f((sample + n) / SampleRate) * gain;
                input[n] = x;
                blockIn = Math.Max(blockIn, Math.Abs(x));
            }

            double began = clock.Elapsed.TotalSeconds;
            try
            {
                p.Process(inputs, outputs, Block);
            }
            catch (Exception ex)
            {
                Fail(p, ErrorCodes.Internal, $"The simulation failed: {ex.Message}");
                continue;
            }
            busy += clock.Elapsed.TotalSeconds - began;

            double blockOut = 0;
            float outGain = outputGain;
            double[] output = outputs[0];
            for (int n = 0; n < Block; n++)
                blockOut = Math.Max(blockOut, Math.Abs(output[n] * outGain));
            if (double.IsNaN(blockOut) || double.IsInfinity(blockOut) || !ProbesFinite(p))
            {
                Diverged(p);
                continue;
            }
            inputPeak = Math.Max(inputPeak * decay, blockIn);
            outputPeak = Math.Max(outputPeak * decay, blockOut);

            // Probe samples for the scope, for as long as anyone is watching.
            Probe[] watched = probeSnapshot;
            bool anyScope = context.Hub.Sessions.Any(s => s.ScopeSimulation == Id);
            if (anyScope && watched.Length > 0)
            {
                if (scope.Count == 0)
                    scopeStart = sample;
                Dictionary<string, int> index = probeIndex;
                foreach (Probe probe in watched)
                {
                    if (!scope.TryGetValue(probe.Stream, out List<float>? samples))
                        scope[probe.Stream] = samples = new List<float>(scopeEvery * 2);
                    // A probe that started mid-frame is padded at the front, so every signal starts at the frame's index.
                    while (samples.Count < sample - scopeStart)
                        samples.Add(0);
                    if (index.TryGetValue(probe.Node, out int i))
                    {
                        double[] values = p.ProbeSamples[i];
                        for (int n = 0; n < Block; n++)
                            samples.Add((float)values[n]);
                    }
                    else
                    {
                        // Ground, or a node the circuit doesn't solve for: 0 V.
                        for (int n = 0; n < Block; n++)
                            samples.Add(0);
                    }
                }
            }
            else
                scope.Clear();
            sample += Block;

            if (sample >= nextScope)
            {
                nextScope = sample + scopeEvery;
                if (scope.Count > 0)
                {
                    SendScope(scopeStart, scope, watched);
                    scope.Clear();
                }
            }
            if (sample >= nextLevels)
            {
                nextLevels = sample + levelEvery;
                context.Hub.Broadcast("sim.levels", new JsonObject
                {
                    ["sim"] = Id,
                    ["inputs"] = p.HasInput ? new JsonArray(inputPeak) : new JsonArray(),
                    ["outputs"] = new JsonArray(outputPeak),
                });
            }
            if (sample >= nextStats)
            {
                nextStats = sample + (long)SampleRate;
                double elapsed = clock.Elapsed.TotalSeconds - statsSince;
                context.Hub.Broadcast("sim.stats", new JsonObject
                {
                    ["sim"] = Id,
                    ["load"] = elapsed > 0 ? Math.Round(busy / elapsed, 4) : 0,
                    // The engine doesn't count capped steps while running for speed; null says it isn't measured.
                    ["cappedSteps"] = null,
                    ["rejectedSettings"] = p.RejectedSettings,
                    ["dropouts"] = Interlocked.Read(ref dropouts),
                });
                busy = 0;
                statsSince = clock.Elapsed.TotalSeconds;
            }
        }
    }

    private static bool ProbesFinite(SchematicProcessor p)
    {
        foreach (double[] values in p.ProbeSamples)
            for (int n = 0; n < Block; n++)
                if (!double.IsFinite(values[n]))
                    return false;
        return true;
    }

    private void Fail(SchematicProcessor p, string failCode, string failMessage)
    {
        lock (sync)
        {
            if (processor != p)
                return;
            processor = null;
        }
        context.Log.Write(MessageType.Error, Id, failMessage);
        SetState("error", failCode, failMessage);
    }

    // As LiveSPICE does: silence, say so, and rebuild if it had run for more than a second.
    private void Diverged(SchematicProcessor p)
    {
        bool rebuild;
        lock (sync)
            rebuild = processor == p && DateTime.UtcNow - runningSince > TimeSpan.FromSeconds(1);
        Fail(p, ErrorCodes.Diverged, "The simulation diverged.");
        if (rebuild)
            _ = BuildAsync();
    }

    /// <summary>A scope frame (WebSocketApi.md, Binary frames) with each watched probe's samples, to every connection
    /// that has the scope on for this simulation.</summary>
    private void SendScope(long first, Dictionary<int, List<float>> scope, Probe[] watched)
    {
        var signals = watched.Where(p => scope.ContainsKey(p.Stream)).Select(p => (p.Stream, Samples: scope[p.Stream])).ToList();
        int size = 24 + signals.Sum(s => 8 + 4 * s.Samples.Count);
        var frame = new byte[size];
        Span<byte> span = frame;
        span[0] = 1;
        span[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)Number);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)first);
        BinaryPrimitives.WriteSingleLittleEndian(span[16..], (float)SampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], (ushort)signals.Count);
        int at = 24;
        foreach (var (stream, samples) in signals)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)stream);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], (uint)samples.Count);
            at += 8;
            foreach (float value in samples)
            {
                BinaryPrimitives.WriteSingleLittleEndian(span[at..], value);
                at += 4;
            }
        }
        foreach (Session session in context.Hub.Sessions)
            if (session.ScopeSimulation == Id)
                session.TrySendBinary(frame);
    }
}
