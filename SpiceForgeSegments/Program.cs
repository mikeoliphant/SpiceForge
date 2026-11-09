using System.Text.Json;
using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

// Finds the candidate black-box segments of a schematic, runs a WAV file through it, and records each
// segment's inputs and outputs: training data for a network, or the samples to build a lookup table from.

const int Oversample = 8;
const int Iterations = 8;
const int BlockSize = 4800;

int stride = Oversample;
double verifySeconds = 2;
bool lut = false;
int lutResolution = 0;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--stride" && i + 1 < args.Length)
        stride = int.Parse(args[++i]);
    else if (args[i] == "--lut")
        lut = true;
    else if (args[i] == "--lut-resolution" && i + 1 < args.Length)
        lutResolution = int.Parse(args[++i]);
    else if (args[i] == "--verify-seconds" && i + 1 < args.Length)
        verifySeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i].StartsWith("--"))
        return Usage($"Unknown option '{args[i]}'.");
    else
        positional.Add(args[i]);
}
if (positional.Count != 3 || stride < 1)
    return Usage(null);
string schematicPath = positional[0], wavPath = positional[1], outputDirectory = positional[2];

try
{
    double[] input = ReadMono(wavPath, out int sampleRate);
    var circuit = Schematic.Load(schematicPath, new ConsoleLog { Verbosity = MessageType.Error }).Build();
    string name = Path.GetFileNameWithoutExtension(schematicPath);
    var inputs = circuit.Components.OfType<Input>().ToList();
    if (inputs.Count != 1)
        throw new InvalidOperationException($"The schematic needs exactly one Input component, and it has {inputs.Count}.");
    Expression inputSymbol = Component.DependentVariable(inputs[0].Name, Component.t);
    var speakers = circuit.Components.OfType<Speaker>().ToList();
    if (speakers.Count == 0)
        throw new InvalidOperationException("The schematic has no Speaker component.");
    Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
    double[] scales = speakers.Select(s => 1 / (double)s.V0dBFS).ToArray();

    Expression h = (Real)1 / (sampleRate * Oversample);
    Analysis analysis = circuit.Analyze();
    TransientSolution solution = TransientSolution.Solve(analysis, h);
    ExtractedSystem system = Extractor.Extract(analysis, h);
    // The default exact rebuild is the same as black-box mode, so the data matches what a box will see.
    CircuitInterpreter Create() => new(system, inputSymbol, nodes, h, Oversample, Iterations, solution);
    CircuitInterpreter CreateWithCap(int cap) => new(system, inputSymbol, nodes, h, Oversample, cap, solution);
    CompiledCircuit Compile(ISegmentSolver? box) => new(system, inputSymbol, nodes, h, Oversample, Iterations, solution, segmentSolver: box);
    double[] Stock(double[] samples)
    {
        var simulation = new Simulation(solution) { Oversample = Oversample, Iterations = Iterations, Input = new[] { inputSymbol }, Output = new[] { speakers.Select(s => s.Out).Aggregate((a, b) => a + b) } };
        var result = new double[samples.Length];
        for (int start = 0; start < samples.Length; start += BlockSize)
        {
            int count = Math.Min(BlockSize, samples.Length - start);
            var block = new double[count];
            simulation.Run(samples.AsSpan(start, count).ToArray(), block);
            Array.Copy(block, 0, result, start, count);
        }
        return result;
    }

    var engine = Create();
    if (engine.Segments.Count == 0)
    {
        Console.WriteLine($"{name} has no nonlinear terms, so there is no Newton solve to replace.");
        return 0;
    }

    Console.WriteLine($"{name}: {engine.Segments.Count} segment(s) at {sampleRate} Hz x{Oversample}.");
    foreach (Segment segment in engine.Segments)
        Console.WriteLine($"  segment {segment.Index}: {segment.Ports.Length} port(s), {segment.Free.Length} free unknown(s), "
            + $"{segment.Upstream.Length} upstream input(s), {segment.NonlinearTerms.Length} nonlinear term(s) -> {segment.InputCount} inputs, {segment.OutputCount} outputs");

    // Record every step's statistics, and every stride-th step's data. Capped steps are always kept.
    Directory.CreateDirectory(outputDirectory);
    long expectedRows = (long)input.Length * Oversample / stride;
    var recorder = new Recorder(engine.Segments, outputDirectory, stride, (int)Math.Max(1, expectedRows / 200000));
    engine.SegmentObserver = recorder;
    Run(engine, input, nodes.Length, scales);
    recorder.Dispose();
    double solveShare = engine.SolveTicks / (double)Math.Max(engine.RunTicks, 1);

    var replay = Verify(Create, input, sampleRate, verifySeconds, nodes.Length, scales);
    double stockDb = CompareWithStock(solution, inputSymbol, speakers, input, sampleRate, verifySeconds, Create, nodes.Length, scales);

    long totalSteps = engine.IterationCounts.Count;
    double meanIterations = engine.IterationCounts.Average(b => b & 0x7F);
    var summary = new
    {
        circuit = name,
        schematic = Path.GetFullPath(schematicPath),
        audio = Path.GetFullPath(wavPath),
        sampleRate,
        oversample = Oversample,
        stride,
        steps = totalSteps,
        recordedRows = recorder.Rows,
        meanIterations,
        cappedSteps = engine.IterationCounts.Count(b => (b & 0x80) != 0),
        solveShareOfRuntime = solveShare,
        reconstruction = "Unknowns are rebuilt exactly from the segment outputs, as in black-box mode, rather than through LiveSPICE's post-Newton evaluation.",
        contract = "Each step, a segment's outputs are a fixed function of its inputs. Segments run in order; " +
                   "upstream inputs are the outputs of earlier segments on the same step. All circuit state stays outside the boxes.",
        replay,
        recordingVersusLiveSpiceDb = stockDb,
        files = new
        {
            steps = "steps.npy: one row per recorded step: [step index, Newton iterations, 1 if the iteration cap was hit]",
            inputs = "segment<N>_inputs.npy: one row per recorded step, columns as listed in that segment's inputs",
            outputs = "segment<N>_outputs.npy: one row per recorded step, columns as listed in that segment's outputs",
        },
        segments = engine.Segments.Select(s =>
        {
            SegmentStatistics stats = recorder.Statistics[s.Index];
            double[] mean = stats.Mean, deviation = stats.Deviation;
            return new
            {
                index = s.Index,
                ports = s.Ports.Length,
                freeUnknowns = s.Free.Length,
                upstream = s.Upstream.Length,
                constantInputs = stats.ConstantInputs,
                effectiveInputDimension = new { at1e2 = stats.EffectiveDimension(1e-2), at1e4 = stats.EffectiveDimension(1e-4) },
                inputSpectrum = stats.Spectrum(),
                inputs = s.InputNames.Select((label, i) => new { label, min = stats.InputMin[i], max = stats.InputMax[i], mean = mean[i], deviation = deviation[i] }),
                outputs = s.OutputNames.Select((label, o) => new { label, min = stats.OutputMin[o], max = stats.OutputMax[o] }),
                nonlinearTerms = s.NonlinearTerms.Select(c => Show(system.Nonlinear[c])),
            };
        }),
    };
    File.WriteAllText(Path.Combine(outputDirectory, "segments.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));

    Console.WriteLine($"Solve is {solveShare:P0} of runtime; {meanIterations:F2} Newton iterations per step; {summary.cappedSteps} of {totalSteps} steps hit the cap.");
    foreach (Segment segment in engine.Segments)
    {
        SegmentStatistics stats = recorder.Statistics[segment.Index];
        Console.WriteLine($"  segment {segment.Index}: {stats.ConstantInputs} constant input(s); effective input dimension {stats.EffectiveDimension(1e-2)} at 1e-2, {stats.EffectiveDimension(1e-4)} at 1e-4, of {segment.InputCount}");
        Console.WriteLine("    standardized input spectrum: " + string.Join(", ", stats.Spectrum().Select(v => v.ToString("G3"))));
    }
    Console.WriteLine($"Recorded {recorder.Rows} rows to {Path.GetFullPath(outputDirectory)}.");
    Console.WriteLine($"Replay check over {replay.seconds:F1} s: output matches to {replay.outputErrorDb:F1} dB, segment inputs to {replay.inputErrorDb:F1} dB.");
    Console.WriteLine($"Recorded behaviour versus LiveSPICE over the same span: {stockDb:F1} dB.");
    if (lut)
        TryLookupTables(Create, CreateWithCap, Compile, Stock, recorder, input, sampleRate, nodes.Length, scales, outputDirectory, lutResolution);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static double[] Run(CircuitInterpreter engine, double[] input, int nodeCount, double[] scales)
{
    var output = new double[input.Length];
    double[][] buffers = Enumerable.Range(0, nodeCount).Select(_ => new double[BlockSize]).ToArray();
    for (int start = 0; start < input.Length; start += BlockSize)
    {
        int count = Math.Min(BlockSize, input.Length - start);
        if (count != BlockSize)
            buffers = Enumerable.Range(0, nodeCount).Select(_ => new double[count]).ToArray();
        engine.Run(input.AsSpan(start, count).ToArray(), buffers);
        for (int n = 0; n < count; n++)
        {
            double sum = 0;
            for (int s = 0; s < scales.Length; s++)
                sum += (buffers[2 * s][n] - buffers[2 * s + 1][n]) * scales[s];
            output[start + n] = sum;
        }
    }
    return output;
}

// Records the first few seconds at full resolution, then reruns them with each segment replaced by a box that
// plays those recorded outputs back. If the recorded inputs and outputs are everything a box needs, the circuit
// reproduces its own output. Black-box mode rebuilds the other unknowns exactly from the box outputs, rather
// than through LiveSPICE's post-Newton evaluation, so this measures that difference too.
static (double seconds, double outputErrorDb, double inputErrorDb) Verify(Func<CircuitInterpreter> create, double[] input, int sampleRate, double seconds, int nodeCount, double[] scales)
{
    int samples = Math.Min(input.Length, (int)(seconds * sampleRate));
    if (samples == 0)
        return (0, double.NaN, double.NaN);
    double[] clip = input.AsSpan(0, samples).ToArray();

    var reference = create();
    var memory = new MemoryRecorder(reference.Segments.Count);
    reference.SegmentObserver = memory;
    double[] expected = Run(reference, clip, nodeCount, scales);

    var replayed = create();
    var player = new ReplaySolver(memory);
    replayed.SegmentSolver = player;
    double[] actual = Run(replayed, clip, nodeCount, scales);

    double peak = expected.Max(Math.Abs), error = 0;
    for (int n = 0; n < samples; n++)
        error = Math.Max(error, Math.Abs(expected[n] - actual[n]));
    Console.WriteLine($"Worst replay input mismatch: step {player.Worst.Step}, segment {player.Worst.Segment} input {player.Worst.Input}: recorded {player.Worst.Expected:G10}, replayed {player.Worst.Actual:G10}.");
    return (samples / (double)sampleRate, Db(error / Math.Max(peak, 1e-30)), Db(player.WorstInputError));
}

// How far the recorded behaviour (exact reconstruction) is from stock LiveSPICE, as worst error relative to peak.
static double CompareWithStock(TransientSolution solution, Expression inputSymbol, List<Speaker> speakers, double[] input, int sampleRate, double seconds, Func<CircuitInterpreter> create, int nodeCount, double[] scales)
{
    int samples = Math.Min(input.Length, (int)(seconds * sampleRate));
    if (samples == 0)
        return double.NaN;
    double[] clip = input.AsSpan(0, samples).ToArray();
    double[] ours = Run(create(), clip, nodeCount, scales);
    Expression output = speakers.Select(s => s.Out).Aggregate((a, b) => a + b);
    var stock = new double[samples];
    new Simulation(solution) { Oversample = Oversample, Iterations = Iterations, Input = new[] { inputSymbol }, Output = new[] { output } }.Run(clip, stock);
    double peak = stock.Max(Math.Abs), error = 0;
    for (int n = 0; n < samples; n++)
        error = Math.Max(error, Math.Abs(stock[n] - ours[n]));
    return Db(error / Math.Max(peak, 1e-30));
}

// Builds a lookup table for every segment, then renders the whole input with them as the black boxes and compares
// that with the normal engine. Both renders are written out for listening.
static void TryLookupTables(Func<CircuitInterpreter> create, Func<int, CircuitInterpreter> createWithCap, Func<ISegmentSolver?, CompiledCircuit> compile, Func<double[], double[]> stock, Recorder recorder, double[] input, int sampleRate, int nodeCount, double[] scales, string directory, int resolution)
{
    var builder = create();
    var tables = new List<LookupTable>();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    foreach (Segment segment in builder.Segments)
    {
        int dimensions = recorder.Statistics[segment.Index].EffectiveDimension(1e-4);
        int size = resolution > 0 ? resolution : dimensions <= 1 ? 4096 : 256;
        LookupTable? table = LookupTable.Build(builder, segment, recorder.SampleInputs[segment.Index], recorder.SampleOutputs[segment.Index], size, out string reason);
        if (table == null)
        {
            Console.WriteLine($"Lookup table: segment {segment.Index} can't use one: {reason}");
            return;
        }
        Console.WriteLine($"Lookup table: segment {segment.Index}: {table.Dimensions} dimension(s), {table.Resolution} points per dimension, "
            + $"projection residual {table.ProjectionResidual:E1}, {table.FailedPoints} grid point(s) didn't converge.");
        tables.Add(table);
    }
    Console.WriteLine($"Built in {watch.Elapsed.TotalSeconds:F2} s from {recorder.SampleInputs[0].Count} sample inputs.");

    var newton = create();
    double[] expected = Run(newton, input, nodeCount, scales);
    var boxed = create();
    boxed.SegmentSolver = new LookupTableSolver(tables);
    double[] actual = Run(boxed, input, nodeCount, scales);

    double peak = expected.Max(Math.Abs), worst = 0;
    int window = sampleRate / 10, badWindows = 0, windows = 0;
    for (int start = 0; start < input.Length; start += window, windows++)
    {
        double windowWorst = 0;
        for (int n = start; n < Math.Min(input.Length, start + window); n++)
            windowWorst = Math.Max(windowWorst, Math.Abs(expected[n] - actual[n]));
        if (windowWorst > peak * 1e-3 || double.IsNaN(windowWorst))
            badWindows++;
        worst = Math.Max(worst, windowWorst);
    }

    double Share(CircuitInterpreter e) => e.SolveTicks / (double)Math.Max(e.RunTicks, 1);
    double Microseconds(CircuitInterpreter e) => e.RunTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / input.Length;
    Console.WriteLine($"Lookup table versus Newton over {input.Length / (double)sampleRate:F1} s: worst error {Db(worst / Math.Max(peak, 1e-30)):F1} dB re peak; "
        + $"{badWindows} of {windows} 100 ms windows above -60 dB; {tables.Sum(tb => tb.OutOfRange)} lookups outside the table.");
    Console.WriteLine($"Interpreter time per sample: Newton {Microseconds(newton):F2} us ({Share(newton):P0} solving), lookup table {Microseconds(boxed):F2} us ({Share(boxed):P0} solving).");

    // A tightly converged render, to tell table error from the normal engine's own stopping error.
    var tight = createWithCap(100);
    tight.RelativeTolerance = 1e-10;
    tight.AbsoluteTolerance = 1e-12;
    double[] reference = RunWithCap(tight, input, nodeCount, scales);
    Console.WriteLine($"Against a tightly converged render: Newton {Compare(reference, expected, sampleRate)}; lookup table {Compare(reference, actual, sampleRate)}.");
    // The same two configurations compiled, which is how they would really run.
    var compiledBoxed = compile(new LookupTableSolver(tables));
    var watchCompiled = System.Diagnostics.Stopwatch.StartNew();
    double[] compiledActual = RunCompiled(compiledBoxed, input, nodeCount, scales);
    double boxedSeconds = watchCompiled.Elapsed.TotalSeconds;
    var compiledNewton = compile(null);
    watchCompiled.Restart();
    RunCompiled(compiledNewton, input, nodeCount, scales);
    double newtonSeconds = watchCompiled.Elapsed.TotalSeconds;
    watchCompiled.Restart();
    stock(input);
    double stockSeconds = watchCompiled.Elapsed.TotalSeconds;
    bool same = compiledActual.Zip(actual, (a, b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b)).All(s => s);
    double audio = input.Length / (double)sampleRate;
    Console.WriteLine($"Compiled lookup table output {(same ? "is identical to" : "differs from")} the interpreter's.");
    Console.WriteLine($"Compiled time per sample: stock LiveSPICE {stockSeconds * 1e6 / input.Length:F2} us, Newton {newtonSeconds * 1e6 / input.Length:F2} us, "
        + $"lookup table {boxedSeconds * 1e6 / input.Length:F2} us ({audio / boxedSeconds:F0}x real time; {newtonSeconds / boxedSeconds:F2}x Newton, {stockSeconds / boxedSeconds:F2}x stock).");

    WriteWav(Path.Combine(directory, "newton.wav"), expected, sampleRate);
    WriteWav(Path.Combine(directory, "lut.wav"), actual, sampleRate);
    Console.WriteLine($"Wrote newton.wav and lut.wav to {Path.GetFullPath(directory)}.");
}

static double[] RunWithCap(CircuitInterpreter engine, double[] input, int nodeCount, double[] scales) => Run(engine, input, nodeCount, scales);

static string Compare(double[] reference, double[] actual, int sampleRate)
{
    double peak = reference.Max(Math.Abs), worst = 0;
    int window = sampleRate / 10, bad = 0, windows = 0;
    for (int start = 0; start < reference.Length; start += window, windows++)
    {
        double windowWorst = 0;
        for (int n = start; n < Math.Min(reference.Length, start + window); n++)
            windowWorst = Math.Max(windowWorst, Math.Abs(reference[n] - actual[n]));
        if (windowWorst > peak * 1e-3 || double.IsNaN(windowWorst))
            bad++;
        worst = Math.Max(worst, windowWorst);
    }
    return $"worst {Db(worst / Math.Max(peak, 1e-30)):F1} dB, {bad} of {windows} windows above -60 dB";
}
static double[] RunCompiled(CompiledCircuit engine, double[] input, int nodeCount, double[] scales)
{
    var output = new double[input.Length];
    double[][] buffers = Enumerable.Range(0, nodeCount).Select(_ => new double[BlockSize]).ToArray();
    for (int start = 0; start < input.Length; start += BlockSize)
    {
        int count = Math.Min(BlockSize, input.Length - start);
        if (count != BlockSize)
            buffers = Enumerable.Range(0, nodeCount).Select(_ => new double[count]).ToArray();
        engine.Run(input.AsSpan(start, count).ToArray(), buffers);
        for (int n = 0; n < count; n++)
        {
            double sum = 0;
            for (int s = 0; s < scales.Length; s++)
                sum += (buffers[2 * s][n] - buffers[2 * s + 1][n]) * scales[s];
            output[start + n] = sum;
        }
    }
    return output;
}

static void WriteWav(string path, double[] samples, int sampleRate)
{
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
    writer.WriteSamples(samples.Select(x => (float)x).ToArray(), 0, samples.Length);
}

static double Db(double ratio) => ratio == 0 ? double.NegativeInfinity : 20 * Math.Log10(ratio);

static double[] ReadMono(string path, out int sampleRate)
{
    using var reader = new WaveFileReader(path);
    sampleRate = reader.WaveFormat.SampleRate;
    int channels = reader.WaveFormat.Channels;
    if (channels > 1)
        Console.WriteLine($"Using the first of {channels} channels.");
    ISampleProvider provider = reader.ToSampleProvider();
    var buffer = new float[BlockSize * channels];
    var samples = new List<double>();
    int read;
    while ((read = provider.Read(buffer.AsSpan())) > 0)
        for (int i = 0; i < read; i += channels)
            samples.Add(buffer[i]);
    return samples.ToArray();
}

static string Show(Expression e)
{
    try { return e.ToString(); }
    catch (Exception) { return "<unprintable>"; }
}

static int Usage(string? problem)
{
    if (problem != null)
        Console.Error.WriteLine(problem);
    Console.Error.WriteLine("Usage: SpiceForgeSegments <schematic.schx> <input.wav> <output folder> [--stride N] [--verify-seconds S] [--lut] [--lut-resolution N]");
    Console.Error.WriteLine("  --stride N          record every Nth internal step (default 8, one per sample); capped steps are always recorded");
    Console.Error.WriteLine("  --verify-seconds S  length of the replay check (default 2, 0 to skip)");
    Console.Error.WriteLine("  --lut               build a lookup table for each segment, render with them, and compare with Newton");
    Console.Error.WriteLine("  --lut-resolution N  grid points per dimension (default 4096 for one dimension, 256 for two)");
    return 2;
}

/// <summary>Writes segment data to .npy files and keeps statistics over every step.</summary>
sealed class Recorder : ISegmentObserver, IDisposable
{
    private readonly int stride;
    private readonly NpyWriter steps;
    private readonly NpyWriter[] inputs;
    private readonly NpyWriter[] outputs;
    private readonly bool[] keep;
    private int lastStep = -1;

    public SegmentStatistics[] Statistics { get; }
    public List<double[]>[] SampleInputs { get; }
    public List<double[]>[] SampleOutputs { get; }
    private readonly int sampleEvery;
    private long kept;
    public long Rows => steps.Rows;

    public Recorder(IReadOnlyList<Segment> segments, string directory, int stride, int sampleEvery)
    {
        this.stride = stride;
        steps = new NpyWriter(Path.Combine(directory, "steps.npy"), 3);
        inputs = segments.Select(s => new NpyWriter(Path.Combine(directory, $"segment{s.Index}_inputs.npy"), s.InputCount)).ToArray();
        outputs = segments.Select(s => new NpyWriter(Path.Combine(directory, $"segment{s.Index}_outputs.npy"), s.OutputCount)).ToArray();
        Statistics = segments.Select(s => new SegmentStatistics(s.InputCount, s.OutputCount)).ToArray();
        keep = new bool[1];
        this.sampleEvery = sampleEvery;
        SampleInputs = segments.Select(_ => new List<double[]>()).ToArray();
        SampleOutputs = segments.Select(_ => new List<double[]>()).ToArray();
    }

    public void Observe(int step, Segment segment, ReadOnlySpan<double> input, ReadOnlySpan<double> output, int iterations, bool capped)
    {
        Statistics[segment.Index].Add(input, output, capped);
        if (step != lastStep)
        {
            lastStep = step;
            keep[0] = step % stride == 0 || capped;
            if (keep[0])
            {
                steps.Write(new double[] { step, iterations, capped ? 1 : 0 });
                kept++;
            }
        }
        if (keep[0])
        {
            inputs[segment.Index].Write(input);
            outputs[segment.Index].Write(output);
            if ((kept - 1) % sampleEvery == 0)
            {
                SampleInputs[segment.Index].Add(input.ToArray());
                SampleOutputs[segment.Index].Add(output.ToArray());
            }
        }
    }

    public void Dispose()
    {
        steps.Dispose();
        foreach (NpyWriter writer in inputs.Concat(outputs))
            writer.Dispose();
    }
}

/// <summary>Keeps every step's segment inputs and outputs in memory.</summary>
sealed class MemoryRecorder : ISegmentObserver
{
    public List<double[]>[] Inputs { get; }
    public List<double[]>[] Outputs { get; }

    public MemoryRecorder(int segments)
    {
        Inputs = Enumerable.Range(0, segments).Select(_ => new List<double[]>()).ToArray();
        Outputs = Enumerable.Range(0, segments).Select(_ => new List<double[]>()).ToArray();
    }

    public void Observe(int step, Segment segment, ReadOnlySpan<double> input, ReadOnlySpan<double> output, int iterations, bool capped)
    {
        Inputs[segment.Index].Add(input.ToArray());
        Outputs[segment.Index].Add(output.ToArray());
    }
}

/// <summary>A black box that plays recorded outputs back, step by step, and measures how far its inputs drift.</summary>
sealed class ReplaySolver : ISegmentSolver
{
    private readonly MemoryRecorder recording;
    private readonly int[] position;

    public double WorstInputError { get; private set; }
    public (int Step, int Segment, int Input, double Expected, double Actual) Worst { get; private set; }

    public ReplaySolver(MemoryRecorder recording)
    {
        this.recording = recording;
        position = new int[recording.Inputs.Length];
    }

    public void Solve(Segment segment, ReadOnlySpan<double> inputs, Span<double> outputs)
    {
        int step = position[segment.Index]++;
        double[] expected = recording.Inputs[segment.Index][step];
        for (int i = 0; i < inputs.Length; i++)
        {
            double error = Math.Abs(inputs[i] - expected[i]) / (1 + Math.Abs(expected[i]));
            if (error > WorstInputError)
            {
                WorstInputError = error;
                Worst = (step, segment.Index, i, expected[i], inputs[i]);
            }
        }
        recording.Outputs[segment.Index][step].CopyTo(outputs);
    }
}
