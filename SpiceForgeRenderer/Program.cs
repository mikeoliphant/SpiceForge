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
    var log = new ConsoleLog { Verbosity = MessageType.Error };
    var invariant = System.Globalization.CultureInfo.InvariantCulture;

    // The compiled engine, through SchematicProcessor (one copy of the circuit per channel); or LiveSPICE's own
    // Simulation, which only runs with pots and switches baked in.
    SchematicProcessor? processor = null;
    Simulation[] stock = Array.Empty<Simulation>();
    string name;
    if (engine == "compiled")
    {
        processor = new SchematicProcessor(schematicPath, sampleRate, new SchematicProcessorOptions
        {
            Channels = channels,
            Oversample = Oversample,
            IterationCap = Iterations,
            PotSmoothingMilliseconds = rampMilliseconds,
            Log = log,
            PotPositions = potPositions,
            SwitchPositions = switchPositions,
            DynamicControls = dynamicPots,
        });
        name = processor.Name;
    }
    else
    {
        Expression h = (Real)1 / (sampleRate * Oversample);
        LoadedCircuit loaded = CircuitLoader.Load(schematicPath, h, dynamicPots: false, log, potPositions, dynamicSwitches: false, switchPositions);
        name = loaded.Circuit.Name;
        var inputs = loaded.Circuit.Components.OfType<Input>().ToList();
        if (inputs.Count != 1)
            throw new InvalidOperationException($"The schematic needs exactly one Input component, and it has {inputs.Count}.");
        var speakers = loaded.Circuit.Components.OfType<Speaker>().ToList();
        if (speakers.Count == 0)
            throw new InvalidOperationException("The schematic has no Speaker component to take the output from.");
        Expression input = Component.DependentVariable(inputs[0].Name, Component.t);
        Expression output = speakers.Select(s => s.Out).Aggregate((a, b) => a + b);
        stock = Enumerable.Range(0, channels)
            .Select(_ => new Simulation(loaded.Solution) { Oversample = Oversample, Iterations = Iterations, Input = new[] { input }, Output = new[] { output } })
            .ToArray();
    }

    string controls = processor == null ? "" : processor.Pots.Count + processor.Switches.Count > 0
        ? $", {processor.Pots.Count} dynamic pot(s), {processor.Switches.Count} dynamic switch(es)" : dynamicPots ? "" : ", pots and switches baked in";
    Console.WriteLine($"Built {name} for {sampleRate} Hz, {channels} channel(s), {engine} engine{controls}, in {watch.Elapsed.TotalSeconds:F2} s.");
    if (potPositions.Count > 0)
        Console.WriteLine("Pots: " + string.Join(", ", potPositions.Select(p => $"{p.Key} {p.Value.ToString("0.###", invariant)}")) + ".");
    if (switchPositions.Count > 0)
        Console.WriteLine("Switches: " + string.Join(", ", switchPositions.Select(p => $"{p.Key} {p.Value}")) + ".");

    // Automation: each event sets a control at its sample (blocks are split there), in the order given for events at
    // the same time. Names and positions are checked before rendering. Pot moves are ramped by the processor's
    // smoothing (--pot-ramp), from wherever the pot is.
    var events = automation.Select((e, order) => (e, order)).OrderBy(x => x.e.Seconds).ThenBy(x => x.order)
        .Select(x => (Sample: (long)Math.Round(x.e.Seconds * sampleRate), Set: Control(x.e))).ToList();
    Action Control((double Seconds, bool Pot, string Name, double Value) e)
    {
        if (e.Pot)
        {
            PotControl pot = processor!.GetPot(e.Name);
            if (!(e.Value >= 0 && e.Value <= 1))
                throw new ArgumentException($"Pot '{e.Name}' position {e.Value} isn't between 0 and 1.");
            return () => pot.Position = e.Value;
        }
        SwitchControl sw = processor!.GetSwitch(e.Name);
        if (e.Value < 0 || e.Value >= sw.Positions)
            throw new ArgumentException($"Switch '{e.Name}' has positions 0 to {sw.Positions - 1}, not {e.Value}.");
        return () => sw.Position = (int)e.Value;
    }
    if (events.Count > 0)
        Console.WriteLine("Automation: " + string.Join(", ", automation.OrderBy(e => e.Seconds).Select(e =>
            $"{(e.Pot ? "pot" : "switch")} {e.Name} {e.Value.ToString("0.###", invariant)} at {e.Seconds:0.###} s"))
            + (rampMilliseconds > 0 && automation.Any(e => e.Pot) ? $"; pot moves ramped over {rampMilliseconds:0.###} ms" : "") + ".");
    int nextEvent = 0;

    using var writer = new WaveFileWriter(outputPath, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
    var interleaved = new float[BlockSize * channels];
    double[][] blockIn = Enumerable.Range(0, channels).Select(_ => new double[BlockSize]).ToArray();
    double[][] blockOut = Enumerable.Range(0, channels).Select(_ => new double[BlockSize]).ToArray();
    double[][] partIn = Enumerable.Range(0, channels).Select(_ => new double[BlockSize]).ToArray();
    double[][] partOut = Enumerable.Range(0, channels).Select(_ => new double[BlockSize]).ToArray();
    long frames = 0;
    double peak = 0;
    watch.Restart();

    int read;
    while ((read = samples.Read(interleaved.AsSpan())) > 0)
    {
        int count = read / channels;
        for (int c = 0; c < channels; c++)
            for (int n = 0; n < count; n++)
                blockIn[c][n] = interleaved[n * channels + c];

        if (processor == null)
        {
            for (int c = 0; c < channels; c++)
            {
                double[] input = count == BlockSize ? blockIn[c] : blockIn[c].AsSpan(0, count).ToArray();
                double[] output = count == BlockSize ? blockOut[c] : new double[count];
                stock[c].Run(input, output);
                Array.Copy(output, blockOut[c], count);
            }
        }
        else
        {
            // Split the block where events fall, so each takes effect on its own sample.
            var cuts = new SortedSet<int> { 0, count };
            for (int k = nextEvent; k < events.Count && events[k].Sample < frames + count; k++)
                cuts.Add((int)Math.Max(0, events[k].Sample - frames));
            int[] bounds = cuts.ToArray();
            for (int part = 0; part + 1 < bounds.Length; part++)
            {
                int start = bounds[part], length = bounds[part + 1] - start;
                for (; nextEvent < events.Count && events[nextEvent].Sample <= frames + start; nextEvent++)
                    events[nextEvent].Set();
                if (length == 0)
                    continue;
                if (start == 0 && length == count)
                    processor.Process(blockIn, blockOut, count);
                else
                {
                    for (int c = 0; c < channels; c++)
                        Array.Copy(blockIn[c], start, partIn[c], 0, length);
                    processor.Process(partIn, partOut, length);
                    for (int c = 0; c < channels; c++)
                        Array.Copy(partOut[c], 0, blockOut[c], start, length);
                }
            }
        }

        for (int c = 0; c < channels; c++)
            for (int n = 0; n < count; n++)
            {
                double value = blockOut[c][n];
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidOperationException($"The simulation diverged near {(frames + n) / (double)sampleRate:F3} s.");
                interleaved[n * channels + c] = (float)value;
                peak = Math.Max(peak, Math.Abs(value));
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
