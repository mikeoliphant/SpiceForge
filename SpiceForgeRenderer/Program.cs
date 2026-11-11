using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

// Renders a WAV file through a LiveSPICE schematic. Each channel runs through its own copy of the circuit.
// The input samples drive the schematic's Input component, and the output is the sum of its speakers,
// both in LiveSPICE's normalized units, so a full-scale sample is the component's 0 dBFS voltage.
// Pots and switches are runtime parameters by default, held at their schematic positions or those given with --pot
// and --switch; --switch-at moves a switch at a given time, exactly on that sample. --static-pots bakes them in instead.

const int Oversample = 8;
const int Iterations = 8;
const int BlockSize = 4096;

string? engine = "compiled";
bool dynamicPots = true;
var potPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
var switchPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var switchEvents = new List<(double Seconds, string Name, int Position)>();
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--engine" && i + 1 < args.Length)
        engine = args[++i];
    else if (args[i] == "--static-pots")
        dynamicPots = false;
    else if (args[i] == "--pot" && i + 1 < args.Length)
    {
        string setting = args[++i];
        int equals = setting.LastIndexOf('=');
        if (equals <= 0 || !double.TryParse(setting[(equals + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double position))
            return Usage($"Expected --pot <name>=<position>, not '{setting}'.");
        potPositions[setting[..equals]] = position;
    }
    else if (args[i] == "--switch" && i + 1 < args.Length)
    {
        string setting = args[++i];
        int equals = setting.LastIndexOf('=');
        if (equals <= 0 || !int.TryParse(setting[(equals + 1)..], out int position))
            return Usage($"Expected --switch <name>=<position>, not '{setting}'.");
        switchPositions[setting[..equals]] = position;
    }
    else if (args[i] == "--switch-at" && i + 1 < args.Length)
    {
        // TIME:NAME=POSITION, time in seconds.
        string setting = args[++i];
        int colon = setting.IndexOf(':'), equals = setting.LastIndexOf('=');
        if (colon <= 0 || equals <= colon + 1
            || !double.TryParse(setting[..colon], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            || seconds < 0 || !int.TryParse(setting[(equals + 1)..], out int position))
            return Usage($"Expected --switch-at <seconds>:<name>=<position>, not '{setting}'.");
        switchEvents.Add((seconds, setting[(colon + 1)..equals], position));
    }
    else if (args[i].StartsWith("--"))
        return Usage($"Unknown option '{args[i]}'.");
    else
        positional.Add(args[i]);
}
if (positional.Count != 3)
    return Usage(null);
if (engine != "compiled" && engine != "stock")
    return Usage($"Unknown engine '{engine}'.");
if (switchEvents.Count > 0 && (engine != "compiled" || !dynamicPots))
    return Usage("--switch-at needs the compiled engine with dynamic switches (not --engine stock or --static-pots).");

string schematicPath = positional[0], inputPath = positional[1], outputPath = positional[2];

try
{
    using var reader = new WaveFileReader(inputPath);
    int sampleRate = reader.WaveFormat.SampleRate;
    int channels = reader.WaveFormat.Channels;
    ISampleProvider samples = reader.ToSampleProvider();

    var watch = System.Diagnostics.Stopwatch.StartNew();
    Expression h = (Real)1 / (sampleRate * Oversample);
    // The stock engine is LiveSPICE's own, which only runs with the pots baked in.
    LoadedCircuit loaded = CircuitLoader.Load(schematicPath, h, dynamicPots && engine == "compiled",
        new ConsoleLog { Verbosity = MessageType.Error }, potPositions, dynamicSwitches: dynamicPots && engine == "compiled", switchPositions);
    var circuit = loaded.Circuit;

    var inputs = circuit.Components.OfType<Input>().ToList();
    if (inputs.Count != 1)
        throw new InvalidOperationException($"The schematic needs exactly one Input component, and it has {inputs.Count}.");
    Expression input = Component.DependentVariable(inputs[0].Name, Component.t);

    var speakers = circuit.Components.OfType<Speaker>().ToList();
    if (speakers.Count == 0)
        throw new InvalidOperationException("The schematic has no Speaker component to take the output from.");

    Func<Processor> create = engine == "stock"
        ? StockFactory(loaded.Solution, input, speakers)
        : CompiledFactory(loaded.System, loaded.Solution, h, input, speakers);
    Processor[] processors = Enumerable.Range(0, channels).Select(_ => create()).ToArray();
    string pots = engine == "stock" ? "" : loaded.Pots.Count + loaded.Switches.Count > 0
        ? $", {loaded.Pots.Count} dynamic pot(s), {loaded.Switches.Select(c => c.Switch).Distinct().Count()} dynamic switch(es)" : dynamicPots ? "" : ", pots and switches baked in";
    Console.WriteLine($"Built {circuit.Name} for {sampleRate} Hz, {channels} channel(s), {engine} engine{pots}, in {watch.Elapsed.TotalSeconds:F2} s.");
    if (potPositions.Count > 0)
        Console.WriteLine("Pots: " + string.Join(", ", potPositions.Select(p => $"{p.Key} {p.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}")) + ".");
    if (switchPositions.Count > 0)
        Console.WriteLine("Switches: " + string.Join(", ", switchPositions.Select(p => $"{p.Key} {p.Value}")) + ".");

    // Switch automation: each event's sample, checked against the circuit's switches before rendering.
    var events = switchEvents.OrderBy(e => e.Seconds)
        .Select(e => (Sample: (long)Math.Round(e.Seconds * sampleRate), e.Name, e.Position)).ToList();
    foreach (var e in events)
        DynamicSwitches.Set(new double[loaded.Parameters.Count], loaded.Parameters, e.Name, e.Position);
    if (events.Count > 0)
        Console.WriteLine("Switch automation: " + string.Join(", ", events.Select(e => $"{e.Name} {e.Position} at {e.Sample / (double)sampleRate:0.###} s")) + ".");
    int nextEvent = 0;

    using var writer = new WaveFileWriter(outputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
    var interleaved = new float[BlockSize * channels];
    var channelIn = new double[BlockSize];
    var channelOut = new double[BlockSize];
    long frames = 0;
    double peak = 0;
    watch.Restart();

    int read;
    while ((read = samples.Read(interleaved.AsSpan())) > 0)
    {
        int count = read / channels;
        double[] blockIn = count == BlockSize ? channelIn : new double[count];
        double[] blockOut = count == BlockSize ? channelOut : new double[count];

        // Split the block where switch events fall, so each takes effect on its own sample.
        var cuts = new SortedSet<int> { 0, count };
        for (int k = nextEvent; k < events.Count && events[k].Sample < frames + count; k++)
            cuts.Add((int)Math.Max(0, events[k].Sample - frames));
        int[] bounds = cuts.ToArray();

        for (int c = 0; c < channels; c++)
        {
            for (int n = 0; n < count; n++)
                blockIn[n] = interleaved[n * channels + c];
            int k = nextEvent;
            for (int part = 0; part + 1 < bounds.Length; part++)
            {
                int start = bounds[part], length = bounds[part + 1] - start;
                for (; k < events.Count && events[k].Sample <= frames + start; k++)
                    DynamicSwitches.Set(processors[c].Parameters!, loaded.Parameters, events[k].Name, events[k].Position);
                if (bounds.Length == 2)
                    processors[c].Run(blockIn, blockOut);
                else
                {
                    double[] partOut = new double[length];
                    processors[c].Run(blockIn.AsSpan(start, length).ToArray(), partOut);
                    Array.Copy(partOut, 0, blockOut, start, length);
                }
            }
            for (int n = 0; n < count; n++)
            {
                if (double.IsNaN(blockOut[n]) || double.IsInfinity(blockOut[n]))
                    throw new InvalidOperationException($"The simulation diverged near {(frames + n) / (double)sampleRate:F3} s.");
                interleaved[n * channels + c] = (float)blockOut[n];
                peak = Math.Max(peak, Math.Abs(blockOut[n]));
            }
        }
        while (nextEvent < events.Count && events[nextEvent].Sample < frames + count)
            nextEvent++;

        writer.WriteSamples(interleaved, 0, count * channels);
        frames += count;
    }

    double seconds = frames / (double)sampleRate;
    Console.WriteLine($"Rendered {seconds:F2} s of audio in {watch.Elapsed.TotalSeconds:F2} s ({seconds / Math.Max(watch.Elapsed.TotalSeconds, 1e-9):F1}x real time). Output peak {peak:F3}{(peak > 1 ? ", which clips in integer formats" : "")}.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

// Runs the port-reduced compiled engine, reading each speaker's two nodes and combining them.
static Func<Processor> CompiledFactory(ExtractedSystem system, TransientSolution solution, Expression h, Expression input, List<Speaker> speakers)
{
    Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
    foreach (Expression node in nodes)
        if (!system.Unknowns.Contains(node) && !node.EqualsZero())
            throw new InvalidOperationException($"Speaker node {node} isn't an unknown of the circuit.");
    double[] scales = speakers.Select(s => 1 / (double)s.V0dBFS).ToArray();

    return () =>
    {
        var compiled = new CompiledCircuit(system, input, nodes, h, Oversample, Iterations, solution);
        double[][] buffers = Array.Empty<double[]>();
        return new Processor(compiled.ParameterValues, (inputBlock, outputBlock) =>
        {
            if (buffers.Length == 0 || buffers[0].Length != inputBlock.Length)
                buffers = nodes.Select(_ => new double[inputBlock.Length]).ToArray();
            compiled.Run(inputBlock, buffers);
            for (int n = 0; n < inputBlock.Length; n++)
            {
                double sum = 0;
                for (int s = 0; s < scales.Length; s++)
                    sum += (buffers[2 * s][n] - buffers[2 * s + 1][n]) * scales[s];
                outputBlock[n] = sum;
            }
        });
    };
}

// Runs LiveSPICE's own Simulation, for comparison.
static Func<Processor> StockFactory(TransientSolution solution, Expression input, List<Speaker> speakers)
{
    Expression output = speakers.Select(s => s.Out).Aggregate((a, b) => a + b);
    return () =>
    {
        var simulation = new Simulation(solution) { Oversample = Oversample, Iterations = Iterations, Input = new[] { input }, Output = new[] { output } };
        return new Processor(null, (inputBlock, outputBlock) => simulation.Run(inputBlock, outputBlock));
    };
}

static int Usage(string? problem)
{
    if (problem != null)
        Console.Error.WriteLine(problem);
    Console.Error.WriteLine("Usage: SpiceForgeRenderer <schematic.schx> <input.wav> <output.wav> [--engine compiled|stock] [--static-pots] [--pot <name>=<position>]... [--switch <name>=<position>]... [--switch-at <seconds>:<name>=<position>]...");
    Console.Error.WriteLine("  --pot sets a pot, or a group of ganged pots, by name to a wiper position from 0 to 1. Repeat it for more pots.");
    Console.Error.WriteLine("  --switch sets a switch, or a group of ganged switches, to a position: the throw it connects (from 0), or 0/1 for on/off.");
    Console.Error.WriteLine("  --switch-at TIME:NAME=POSITION moves a switch at TIME seconds, exactly on that sample. Repeat it for more changes.");
    Console.Error.WriteLine("  --static-pots bakes pots and switches in instead of making them runtime parameters.");
    return 2;
}

/// <summary>Processes one channel, one block at a time, keeping its circuit state between blocks.</summary>
sealed class Processor
{
    private readonly Action<double[], double[]> run;

    /// <summary>The engine's runtime parameter values (pots, then switch contacts), or null for LiveSPICE's own engine.
    /// Changes take effect from the next Run.</summary>
    public double[]? Parameters { get; }

    public Processor(double[]? parameters, Action<double[], double[]> run)
    {
        Parameters = parameters;
        this.run = run;
    }

    public void Run(double[] input, double[] output) => run(input, output);
}
