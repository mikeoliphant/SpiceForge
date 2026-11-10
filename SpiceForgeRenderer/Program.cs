using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

// Renders a WAV file through a LiveSPICE schematic. Each channel runs through its own copy of the circuit.
// The input samples drive the schematic's Input component, and the output is the sum of its speakers,
// both in LiveSPICE's normalized units, so a full-scale sample is the component's 0 dBFS voltage.
// Pots are runtime parameters by default, held at their schematic positions; --static-pots bakes them in instead.

const int Oversample = 8;
const int Iterations = 8;
const int BlockSize = 4096;

string? engine = "compiled";
bool dynamicPots = true;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--engine" && i + 1 < args.Length)
        engine = args[++i];
    else if (args[i] == "--static-pots")
        dynamicPots = false;
    else if (args[i].StartsWith("--"))
        return Usage($"Unknown option '{args[i]}'.");
    else
        positional.Add(args[i]);
}
if (positional.Count != 3)
    return Usage(null);
if (engine != "compiled" && engine != "stock")
    return Usage($"Unknown engine '{engine}'.");

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
        new ConsoleLog { Verbosity = MessageType.Error });
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
    string pots = engine == "stock" ? "" : loaded.Pots.Count > 0 ? $", {loaded.Pots.Count} dynamic pot(s)" : dynamicPots ? "" : ", pots baked in";
    Console.WriteLine($"Built {circuit.Name} for {sampleRate} Hz, {channels} channel(s), {engine} engine{pots}, in {watch.Elapsed.TotalSeconds:F2} s.");

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

        for (int c = 0; c < channels; c++)
        {
            for (int n = 0; n < count; n++)
                blockIn[n] = interleaved[n * channels + c];
            processors[c].Run(blockIn, blockOut);
            for (int n = 0; n < count; n++)
            {
                if (double.IsNaN(blockOut[n]) || double.IsInfinity(blockOut[n]))
                    throw new InvalidOperationException($"The simulation diverged near {(frames + n) / (double)sampleRate:F3} s.");
                interleaved[n * channels + c] = (float)blockOut[n];
                peak = Math.Max(peak, Math.Abs(blockOut[n]));
            }
        }

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
        return new Processor((inputBlock, outputBlock) =>
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
        return new Processor((inputBlock, outputBlock) => simulation.Run(inputBlock, outputBlock));
    };
}

static int Usage(string? problem)
{
    if (problem != null)
        Console.Error.WriteLine(problem);
    Console.Error.WriteLine("Usage: SpiceForgeRenderer <schematic.schx> <input.wav> <output.wav> [--engine compiled|stock] [--static-pots]");
    return 2;
}

/// <summary>Processes one channel, one block at a time, keeping its circuit state between blocks.</summary>
sealed class Processor
{
    private readonly Action<double[], double[]> run;
    public Processor(Action<double[], double[]> run) => this.run = run;
    public void Run(double[] input, double[] output) => run(input, output);
}
