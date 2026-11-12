using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Circuit;
using SpiceForge;
using SpiceForgeServer.Audio;
using SpiceForgeServer.Documents;
using SpiceForgeServer.Protocol;
using Util;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForgeServer.Simulating;

/// <summary>
/// One simulation (WebSocketApi.md, Simulation): a <see cref="SchematicProcessor"/> built from a client's document, run
/// on the audio device (see <see cref="AudioService"/>), or on the internal clock without one. The device calls
/// <see cref="Process"/> for every block: it drives the Input from a device channel or an expression, and plays the
/// Speakers on every output channel. A monitor thread does everything that mustn't happen on the audio thread: it
/// streams the probes' samples to the scope, and sends the levels and stats, and any failure.
/// </summary>
public sealed class Simulation : IAudioClient
{
    private const int MonitorRate = 60;
    private const int LevelRate = 10;
    private const double LevelDecaySeconds = 0.25;

    private static readonly string[] Colors = { "red", "green", "blue", "yellow", "cyan", "magenta", "orange" };

    private readonly ServerContext context;
    private readonly object sync = new();
    private readonly Thread monitor;
    private volatile bool stopping, started;

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
    private readonly Dictionary<string, double> potPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> switchPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Probe> probes = new();
    private volatile ProbeSet probeSnapshot = ProbeSet.Empty;
    private int nextProbe = 1;
    private InputSource? source;
    private volatile Func<double, double>? sourceFunction;
    private volatile int sourceChannel = -1;   // The device input driving the Input, or -1.
    private volatile float inputGain = 1, outputGain = 1;
    private double inputDb, outputDb;
    private DateTime runningSince;

    // The device's format, and the audio thread's buffers for it: replaced by Prepare, while the device is stopped.
    private volatile AudioFormat format = new(ClockDriver.DefaultSampleRate, 0, Array.Empty<string>(), Array.Empty<string>());
    private double[][] inputs = { Array.Empty<double>() }, outputs = { Array.Empty<double>() };

    // Written by the audio thread; read (and taken) by the monitor.
    private long sample;               // Samples processed, from the first build's first: the stream's index, and t.
    private long dropouts;
    private long busyTicks;
    private double inputPeak, outputPeak;
    private volatile bool scopeWatched;
    private volatile SchematicProcessor? failed;   // A build the audio thread has given up on.
    private Failure? pendingFailure;
    private readonly ScopeRing scope = new();

    private sealed record InputSource(JsonObject Json, Func<double, double>? Function, string? Channel);

    private sealed record Failure(SchematicProcessor Processor, bool Diverged, string Message);

    /// <summary>The probes as the audio thread reads them: their nodes, and the streams they go out on. A new set is
    /// made whenever the probes change, so the scope can tell where one set ends.</summary>
    private sealed class ProbeSet
    {
        public static readonly ProbeSet Empty = new(Array.Empty<Probe>());

        public ProbeSet(Probe[] probes)
        {
            Nodes = probes.Select(p => p.Node).ToArray();
            Streams = probes.Select(p => p.Stream).ToArray();
        }

        public string[] Nodes { get; }
        public int[] Streams { get; }
    }

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
        monitor = new Thread(Monitor) { IsBackground = true, Name = $"Simulation {Id} monitor" };
    }

    /// <summary>Starts building, and the monitor. Attach the simulation to the audio device first, so it builds for the
    /// device's rate.</summary>
    public void Start()
    {
        started = true;
        monitor.Start();
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
        AudioFormat device = format;
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
                SchematicProcessor p = Create(doc, options, pots, switches, log, device.SampleRate);
                // The first time, the Input plays the device's first input, or without one a 220 Hz sine at half its
                // full scale, until the client sets a source.
                InputSource? initial = null;
                if (needSource && p.HasInput)
                {
                    if (device.Inputs.Count > 0)
                        initial = new InputSource(new JsonObject { ["channel"] = device.Inputs[0] }, null, device.Inputs[0]);
                    else
                    {
                        string text = $"{Format(p.InputFullScale / 2)}*Sin(2*Pi*220*t)";
                        initial = new InputSource(new JsonObject { ["expression"] = text }, CompileSource(text), null);
                    }
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
                lookup = new ProbeLookup(built, built.ProbeNodes.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i));
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

    private SchematicProcessor Create(JsonObject doc, JsonObject options, Dictionary<string, double> pots, Dictionary<string, int> switches, ILog log, double sampleRate)
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
        return new SchematicProcessor(Build, sampleRate, new SchematicProcessorOptions
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
                ["sampleRate"] = format.SampleRate,
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
            // Every output channel plays the Speakers. Without a device there are none: the output only drives the meters.
            ["outputs"] = Json.Array(format.Outputs.Select(channel => (JsonNode?)new JsonObject
            {
                ["channel"] = channel,
                ["signal"] = new JsonObject { ["speakers"] = true },
            })),
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
            newSource = new InputSource(new JsonObject { ["silence"] = true }, null, null);
        else if (sourceJson["expression"] is JsonValue e && e.TryGetValue(out string? text))
        {
            Func<double, double> function = await Engine.Run(() => CompileSource(text));
            newSource = new InputSource(new JsonObject { ["expression"] = text }, function, null);
        }
        else if (sourceJson["channel"] is JsonValue c && c.TryGetValue(out string? channel))
        {
            IReadOnlyList<string> available = format.Inputs;
            string name = available.FirstOrDefault(a => string.Equals(a, channel, StringComparison.OrdinalIgnoreCase))
                ?? throw new ApiException(ErrorCodes.InvalidValue, available.Count > 0
                    ? $"The audio device has no input '{channel}' open. Its inputs are: {string.Join(", ", available)}."
                    : $"No audio inputs are open, so there's no input '{channel}': configure the audio device's inputs.",
                    Json.Strings(available));
            newSource = new InputSource(new JsonObject { ["channel"] = name }, null, name);
        }
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
        sourceChannel = ChannelIndex(newSource.Channel, format);
    }

    // The index of an input channel among the device's open inputs, or -1: a channel that isn't open reads silence.
    private static int ChannelIndex(string? channel, AudioFormat device) =>
        channel == null ? -1 : device.Inputs.ToList().FindIndex(i => string.Equals(i, channel, StringComparison.OrdinalIgnoreCase));

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
            probeSnapshot = new ProbeSet(probes.ToArray());
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
            probeSnapshot = new ProbeSet(probes.ToArray());
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
        probeSnapshot = new ProbeSet(probes.ToArray());
    }

    private void BroadcastProbes()
    {
        JsonObject data;
        lock (sync)
            data = new JsonObject { ["sim"] = Id, ["probes"] = Json.Array(probes.Select(p => (JsonNode?)p.ToJson())) };
        context.Hub.Broadcast("sim.probes", data);
    }

    // ---- Running ----

    /// <summary>The probe nodes' indices into a build's <see cref="SchematicProcessor.ProbeSamples"/>, with the build they
    /// belong to, so the audio thread never reads one build's samples by another's indices.</summary>
    private sealed record ProbeLookup(SchematicProcessor? Processor, Dictionary<string, int> Index);

    private volatile ProbeLookup lookup = new(null, new Dictionary<string, int>());

    /// <summary>The device's format, before it starts and whenever it changes, while it's stopped: sizes the buffers,
    /// and rebuilds if the rate changed.</summary>
    public void Prepare(AudioFormat newFormat)
    {
        AudioFormat old = format;
        inputs = new[] { new double[newFormat.MaxFrames] };
        outputs = new[] { new double[newFormat.MaxFrames] };
        format = newFormat;
        lock (sync)
            sourceChannel = ChannelIndex(source?.Channel, newFormat);
        if (!started || stopping)
            return;
        if (newFormat.SampleRate != old.SampleRate)
            _ = BuildAsync();
        BroadcastControls();
    }

    /// <summary>One block, on the audio thread. Allocates nothing, and takes no lock: what the monitor needs is left in
    /// fields and in the scope ring.</summary>
    public void Process(AudioBlock block)
    {
        int frames = block.Frames;
        SchematicProcessor? p = processor;
        double rate = format.SampleRate;
        // Building, failed, or built for another rate (until the rebuild Prepare started): silence.
        if (p == null || p == failed || stopping || p.SampleRate != rate || frames > inputs[0].Length)
        {
            Silence(block);
            return;
        }
        long began = Stopwatch.GetTimestamp();
        if (block.Skipped > 0 || block.Xruns > 0)
        {
            Interlocked.Add(ref dropouts, (block.Skipped > 0 ? 1 : 0) + block.Xruns);
            sample += block.Skipped;
        }

        // The input: a device channel in the Input's full-scale units, or the source in volts; after the input gain.
        double[] input = inputs[0];
        double blockIn = 0;
        int channel = sourceChannel;
        if (channel >= 0 && channel < block.Inputs.Length)
        {
            float[] from = block.Inputs[channel];
            double gain = inputGain;
            for (int n = 0; n < frames; n++)
            {
                double x = from[n] * gain;
                input[n] = x;
                blockIn = Math.Max(blockIn, Math.Abs(x));
            }
        }
        else
        {
            Func<double, double>? f = sourceFunction;
            double gain = inputGain / p.InputFullScale;
            for (int n = 0; n < frames; n++)
            {
                double x = f == null ? 0 : f((sample + n) / rate) * gain;
                input[n] = x;
                blockIn = Math.Max(blockIn, Math.Abs(x));
            }
        }

        try
        {
            p.Process(inputs, outputs, frames);
        }
        catch (Exception ex)
        {
            GiveUp(new Failure(p, false, $"The simulation failed: {ex.Message}"));
            Silence(block);
            return;
        }

        double blockOut = 0;
        float outGain = outputGain;
        double[] output = outputs[0];
        for (int n = 0; n < frames; n++)
            blockOut = Math.Max(blockOut, Math.Abs(output[n] * outGain));
        if (double.IsNaN(blockOut) || double.IsInfinity(blockOut) || !ProbesFinite(p, frames))
        {
            GiveUp(new Failure(p, true, "The simulation diverged."));
            Silence(block);
            return;
        }
        // Every output channel plays the Speakers.
        for (int o = 0; o < block.Outputs.Length; o++)
        {
            float[] to = block.Outputs[o];
            for (int n = 0; n < frames; n++)
                to[n] = (float)(output[n] * outGain);
        }
        Accumulate.Max(ref inputPeak, blockIn);
        Accumulate.Max(ref outputPeak, blockOut);

        // Probe samples for the scope, for as long as anyone is watching. Ground, or a node the circuit doesn't solve
        // for, reads 0 V.
        ProbeSet set = probeSnapshot;
        if (scopeWatched && set.Nodes.Length > 0 && scope.Begin(set.Nodes.Length, frames))
        {
            ProbeLookup probesOf = lookup;
            for (int j = 0; j < set.Nodes.Length; j++)
            {
                if (probesOf.Processor == p && probesOf.Index.TryGetValue(set.Nodes[j], out int i))
                {
                    double[] values = p.ProbeSamples[i];
                    for (int n = 0; n < frames; n++)
                        scope.Put(j, n, (float)values[n]);
                }
                else
                    for (int n = 0; n < frames; n++)
                        scope.Put(j, n, 0);
            }
            scope.Commit(sample, set.Streams);
        }
        sample += frames;
        Interlocked.Add(ref busyTicks, Stopwatch.GetTimestamp() - began);
    }

    private static void Silence(AudioBlock block)
    {
        for (int o = 0; o < block.Outputs.Length; o++)
            Array.Clear(block.Outputs[o], 0, block.Frames);
    }

    // The audio thread stops running this build at once; the monitor reports it (and rebuilds, after a divergence).
    private void GiveUp(Failure failure)
    {
        failed = failure.Processor;
        Interlocked.CompareExchange(ref pendingFailure, failure, null);
    }

    private static bool ProbesFinite(SchematicProcessor p, int frames)
    {
        foreach (double[] values in p.ProbeSamples)
            for (int n = 0; n < frames; n++)
                if (!double.IsFinite(values[n]))
                    return false;
        return true;
    }

    /// <summary>The monitor thread: about 60 times a second it sends the scope's frames, and any failure; the levels at
    /// about 10 Hz and the stats every second.</summary>
    private void Monitor()
    {
        var clock = Stopwatch.StartNew();
        double nextLevels = 0, lastLevels = 0, nextStats = 1, statsSince = 0;
        double shownIn = 0, shownOut = 0;
        SchematicProcessor? statsOf = null;
        long cappedBefore = 0;
        while (!stopping)
        {
            Thread.Sleep(1000 / MonitorRate);
            if (Interlocked.Exchange(ref pendingFailure, null) is Failure failure)
            {
                if (failure.Diverged)
                    Diverged(failure.Processor);
                else
                    Fail(failure.Processor, ErrorCodes.Internal, failure.Message);
            }

            List<Session> watching = context.Hub.Sessions.Where(s => s.ScopeSimulation == Id).ToList();
            scopeWatched = watching.Count > 0;
            foreach (byte[] frame in scope.Drain((uint)Number, (float)format.SampleRate))
                foreach (Session session in watching)
                    session.TrySendBinary(frame);

            SchematicProcessor? p = processor;
            double now = clock.Elapsed.TotalSeconds;
            if (now >= nextLevels)
            {
                double decay = Math.Exp(-(now - lastLevels) / LevelDecaySeconds);
                lastLevels = now;
                nextLevels = now + 1.0 / LevelRate;
                shownIn = Math.Max(shownIn * decay, Interlocked.Exchange(ref inputPeak, 0));
                shownOut = Math.Max(shownOut * decay, Interlocked.Exchange(ref outputPeak, 0));
                if (p != null)
                {
                    var levels = new JsonArray();
                    for (int o = 0; o < Math.Max(1, format.Outputs.Count); o++)
                        levels.Add(shownOut);
                    context.Hub.Broadcast("sim.levels", new JsonObject
                    {
                        ["sim"] = Id,
                        ["inputs"] = p.HasInput ? new JsonArray(shownIn) : new JsonArray(),
                        ["outputs"] = levels,
                    });
                }
            }
            if (now >= nextStats)
            {
                double elapsed = now - statsSince;
                statsSince = now;
                nextStats = now + 1;
                long busy = Interlocked.Exchange(ref busyTicks, 0);
                if (p != null)
                {
                    if (p != statsOf)
                    {
                        statsOf = p;
                        cappedBefore = 0;
                    }
                    long capped = p.CappedSteps;
                    context.Hub.Broadcast("sim.stats", new JsonObject
                    {
                        ["sim"] = Id,
                        ["load"] = elapsed > 0 ? Math.Round(busy / (double)Stopwatch.Frequency / elapsed, 4) : 0,
                        ["cappedSteps"] = capped - cappedBefore,
                        ["rejectedSettings"] = p.RejectedSettings,
                        ["dropouts"] = Interlocked.Read(ref dropouts),
                    });
                    cappedBefore = capped;
                }
            }
        }
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
}
