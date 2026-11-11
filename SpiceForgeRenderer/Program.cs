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
// and --switch; --pot-at and --switch-at move them at given times, exactly on that sample, and --pot-ramp spreads each
// pot move over a few milliseconds. --static-pots bakes them in.

const int Oversample = 8;
const int Iterations = 8;
const int BlockSize = 4096;
// A ramped pot move updates every this many samples (0.7 ms at 48 kHz).
const int RampStep = 32;

string? engine = "compiled";
bool dynamicPots = true;
var potPositions = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
var switchPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
// Automation: a pot (wipe from 0 to 1) or a switch (position) set at a time in seconds.
var automation = new List<(double Seconds, bool Pot, string Name, double Value)>();
double rampMilliseconds = 0;
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
    else if (args[i] == "--pot-ramp" && i + 1 < args.Length)
    {
        if (!double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rampMilliseconds)
            || rampMilliseconds < 0)
            return Usage($"Expected --pot-ramp <milliseconds>, not '{args[i]}'.");
    }
    else if ((args[i] == "--switch-at" || args[i] == "--pot-at") && i + 1 < args.Length)
    {
        // TIME:NAME=VALUE, time in seconds; a switch position, or a pot's wiper position from 0 to 1.
        bool pot = args[i] == "--pot-at";
        string setting = args[++i];
        int colon = setting.IndexOf(':'), equals = setting.LastIndexOf('=');
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        double value = 0;
        int position = 0;
        if (colon <= 0 || equals <= colon + 1
            || !double.TryParse(setting[..colon], System.Globalization.NumberStyles.Float, invariant, out double seconds) || seconds < 0
            || (pot ? !double.TryParse(setting[(equals + 1)..], System.Globalization.NumberStyles.Float, invariant, out value)
                    : !int.TryParse(setting[(equals + 1)..], out position)))
            return Usage($"Expected {args[i - 1]} <seconds>:<name>=<position>, not '{setting}'.");
        automation.Add((seconds, pot, setting[(colon + 1)..equals], pot ? value : position));
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
if (automation.Count > 0 && (engine != "compiled" || !dynamicPots))
    return Usage("--pot-at and --switch-at need the compiled engine with dynamic pots and switches (not --engine stock or --static-pots).");

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

    // Automation, as steps: each sets some parameters at a sample. Events at the same time apply in the order given,
    // and names and positions are checked before rendering. A pot move with --pot-ramp becomes a line of steps, every
    // RampStep samples, from where the pot is to its target; a later move of the same pot drops what's left of it.
    var parameters = loaded.Parameters;
    var ordered = automation.Select((e, order) => (e, order)).OrderBy(x => x.e.Seconds).ThenBy(x => x.order).Select(x => x.e).ToList();
    long ramp = (long)Math.Round(rampMilliseconds / 1000 * sampleRate);
    var steps = new List<(long Sample, int Order, Action<double[]> Set)>();
    var timeline = parameters.Select(p => new List<(long Sample, int Order, double Wipe)>
        { (-1, -1, p is PotParameter pot ? pot.InitialWipe : 0) }).ToArray();
    for (int index = 0; index < ordered.Count; index++)
    {
        var e = ordered[index];
        long at = (long)Math.Round(e.Seconds * sampleRate);
        // Steps at the same sample apply in event order, and a ramp's own steps after its first.
        int order = index * 1_000_000;
        if (!e.Pot)
        {
            DynamicSwitches.Set(new double[parameters.Count], parameters, e.Name, (int)e.Value);
            steps.Add((at, order, values => DynamicSwitches.Set(values, parameters, e.Name, (int)e.Value)));
            continue;
        }
        DynamicPots.Set(new double[parameters.Count], parameters, e.Name, e.Value);
        foreach (int j in DynamicPots.IndicesOf(parameters, e.Name))
        {
            var line = timeline[j];
            line.RemoveAll(point => point.Sample > at);
            double start = line[^1].Wipe;
            int count = ramp == 0 ? 1 : (int)Math.Max(1, (ramp + RampStep - 1) / RampStep);
            for (int k = 1; k <= count; k++)
                line.Add((at + (ramp == 0 ? 0 : (long)Math.Round((double)ramp * k / count)), order + k, start + (e.Value - start) * k / count));
        }
    }
    for (int j = 0; j < parameters.Count; j++)
        foreach (var point in timeline[j].Skip(1))
        {
            int index = j;
            double value = ((PotParameter)parameters[j]).ValueAt(Math.Clamp(point.Wipe, 0, 1));
            steps.Add((point.Sample, point.Order, values => values[index] = value));
        }
    var events = steps.OrderBy(s => s.Sample).ThenBy(s => s.Order).ToList();
    if (ordered.Count > 0)
        Console.WriteLine("Automation: " + string.Join(", ", ordered.Select(e =>
            $"{(e.Pot ? "pot" : "switch")} {e.Name} {e.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} at {e.Seconds:0.###} s"))
            + (ramp > 0 && ordered.Any(e => e.Pot) ? $"; pot moves ramped over {rampMilliseconds:0.###} ms" : "") + ".");
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

        // Split the block where events fall, so each takes effect on its own sample.
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
                    events[k].Set(processors[c].Parameters!);
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
    Console.Error.WriteLine("Usage: SpiceForgeRenderer <schematic.schx> <input.wav> <output.wav> [--engine compiled|stock] [--static-pots] [--pot <name>=<position>]... [--switch <name>=<position>]... [--switch-at <seconds>:<name>=<position>]... [--pot-at <seconds>:<name>=<position>]... [--pot-ramp <ms>]");
    Console.Error.WriteLine("  --pot sets a pot, or a group of ganged pots, by name to a wiper position from 0 to 1. Repeat it for more pots.");
    Console.Error.WriteLine("  --switch sets a switch, or a group of ganged switches, to a position: the throw it connects (from 0), or 0/1 for on/off.");
    Console.Error.WriteLine("  --switch-at TIME:NAME=POSITION moves a switch at TIME seconds, exactly on that sample. Repeat it for more changes.");
    Console.Error.WriteLine("  --pot-at TIME:NAME=POSITION moves a pot (or group) to a wiper position from 0 to 1 at TIME seconds, the same way.");
    Console.Error.WriteLine("  --pot-ramp MS spreads each --pot-at move over MS milliseconds, in a straight line from where the pot is (default 0).");
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
