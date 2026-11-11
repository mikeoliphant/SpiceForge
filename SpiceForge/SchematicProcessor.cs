using Circuit;
using ComputerAlgebra;
using Util;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>Options for <see cref="SchematicProcessor"/>.</summary>
public sealed class SchematicProcessorOptions
{
    /// <summary>Independent copies of the circuit, one per audio channel.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Internal steps per sample. LiveSPICE's default.</summary>
    public int Oversample { get; init; } = 8;

    /// <summary>Newton's iteration cap per step. LiveSPICE's default.</summary>
    public int IterationCap { get; init; } = 8;

    /// <summary>How long a pot takes to reach a new position, in milliseconds, in a straight line. 0 moves at once.</summary>
    public double PotSmoothingMilliseconds { get; init; } = 5;

    /// <summary>Receives LiveSPICE's messages while the schematic loads; none by default.</summary>
    public ILog? Log { get; init; }

    /// <summary>Starting wiper positions (0 to 1) by pot name or group, in place of the schematic's. The circuit starts
    /// from its DC operating point at these, as if the schematic had them (to restore saved settings, say).</summary>
    public IReadOnlyDictionary<string, double>? PotPositions { get; init; }

    /// <summary>Starting switch positions by switch name or group, in place of the schematic's, the same way.</summary>
    public IReadOnlyDictionary<string, int>? SwitchPositions { get; init; }

    /// <summary>Whether starting positions for names the schematic doesn't have (or out of range) are skipped rather
    /// than throwing: for restoring saved settings onto a schematic that may have been edited since.</summary>
    public bool IgnoreUnknownControls { get; init; }

    /// <summary>Whether pots and switches are runtime parameters, as controls. False bakes them in at their starting
    /// positions, which is slightly faster but leaves <see cref="SchematicProcessor.Pots"/> and
    /// <see cref="SchematicProcessor.Switches"/> empty.</summary>
    public bool DynamicControls { get; init; } = true;
}

/// <summary>
/// Runs audio through a LiveSPICE schematic: loads and compiles it once, lists its pots and switches as controls, and
/// processes blocks of samples. The schematic needs one Input component, which the input samples drive, and at least one
/// Speaker, whose voltages are summed into the output; both are in LiveSPICE's normalized units, so a full-scale sample
/// is the component's 0 dBFS voltage. Pots and switches are runtime parameters, so moving them never re-solves anything.
/// <para>
/// Loading takes from a fraction of a second to a couple of seconds, so do it off the audio thread. After that,
/// <see cref="Process(double[][], double[][], int)"/> is meant for the audio thread, and controls can be set from any
/// thread: a change is picked up at the start of the next block.
/// </para>
/// </summary>
public sealed class SchematicProcessor
{
    private const int SmoothingStep = 32;

    private readonly CompiledCircuit[] engines;
    private readonly IReadOnlyList<RuntimeParameter> parameters;
    private readonly double[] scales;
    private readonly int speakerCount;
    private readonly long smoothingSamples;
    private double[][] buffers = Array.Empty<double[]>();
    // The controls as arrays, so the audio thread loops over them without allocating an enumerator.
    private readonly PotControl[] pots;
    private readonly SwitchControl[] switches;
    private double[] chunkIn = Array.Empty<double>();
    private readonly double[][] spanIn = new double[1][], spanOut = new double[1][];

    /// <summary>The schematic's name (its file name).</summary>
    public string Name { get; }

    public double SampleRate { get; }

    public int Channels => engines.Length;

    /// <summary>How many times a pot or switch setting couldn't be solved, so the circuit stayed at its last good one,
    /// summed over the channels. It has never happened on the test circuits; a host can watch it to say so.</summary>
    public long RejectedSettings
    {
        get
        {
            long total = 0;
            for (int c = 0; c < engines.Length; c++)
                total += engines[c].RejectedSettings;
            return total;
        }
    }

    /// <summary>One control per pot, or per group of ganged pots, in schematic order.</summary>
    public IReadOnlyList<PotControl> Pots { get; }

    /// <summary>One control per switch, or per group of ganged switches, in schematic order.</summary>
    public IReadOnlyList<SwitchControl> Switches { get; }

    public SchematicProcessor(string schematicPath, double sampleRate, SchematicProcessorOptions? options = null)
    {
        options ??= new SchematicProcessorOptions();
        if (options.Channels < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Channels must be at least 1.");
        SampleRate = sampleRate;
        Expression h = 1 / ((Real)sampleRate * options.Oversample);
        LoadedCircuit loaded = CircuitLoader.Load(schematicPath, h, options.DynamicControls, options.Log, options.PotPositions,
            options.DynamicControls, options.SwitchPositions, options.IgnoreUnknownControls);
        Circuit.Circuit circuit = loaded.Circuit;
        Name = circuit.Name;
        parameters = loaded.Parameters;

        var inputs = circuit.Components.OfType<Input>().ToList();
        if (inputs.Count != 1)
            throw new InvalidOperationException($"The schematic needs exactly one Input component, and it has {inputs.Count}.");
        Expression input = Component.DependentVariable(inputs[0].Name, Component.t);
        var speakers = circuit.Components.OfType<Speaker>().ToList();
        if (speakers.Count == 0)
            throw new InvalidOperationException("The schematic has no Speaker component to take the output from.");
        Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
        foreach (Expression node in nodes)
            if (!loaded.System.Unknowns.Contains(node) && !node.EqualsZero())
                throw new InvalidOperationException($"Speaker node {node} isn't an unknown of the circuit.");
        scales = speakers.Select(s => 1 / (double)s.V0dBFS).ToArray();
        speakerCount = speakers.Count;

        engines = Enumerable.Range(0, options.Channels)
            .Select(_ => new CompiledCircuit(loaded.System, input, nodes, h, options.Oversample, options.IterationCap, loaded.Solution) { RecordIterations = false })
            .ToArray();
        smoothingSamples = (long)Math.Round(options.PotSmoothingMilliseconds / 1000 * sampleRate);

        var pots = new List<PotControl>();
        foreach (PotParameter pot in loaded.Pots)
        {
            string name = pot.Component is IPotControl c && !string.IsNullOrEmpty(c.Group) ? c.Group : pot.Name;
            if (pots.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            int[] indices = DynamicPots.IndicesOf(parameters, name).ToArray();
            pots.Add(new PotControl(name, pot.InitialWipe, indices.Select(j => (PotParameter)parameters[j]).ToArray(), indices));
        }
        this.pots = pots.ToArray();
        Pots = this.pots;
        switches = loaded.Switches.GroupBy(c => c.Switch)
            .Select(g =>
            {
                int[] indices = Enumerable.Range(0, parameters.Count).Where(j => parameters[j] is SwitchParameter contact && contact.Switch == g.Key).ToArray();
                return new SwitchControl(g.Key, g.First().Positions, g.First().InitialPosition, indices, indices.Select(j => (SwitchParameter)parameters[j]).ToArray());
            })
            .ToArray();
        Switches = switches;
    }

    /// <summary>The pot or group of ganged pots with this name, case-insensitive. Throws, listing the pots, if none.</summary>
    public PotControl GetPot(string name) => Pots.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No pot named '{name}'. The pots are: {List(Pots.Select(p => p.Name))}.");

    /// <summary>The switch or group of ganged switches with this name, case-insensitive. Throws, listing the switches, if
    /// none.</summary>
    public SwitchControl GetSwitch(string name) => Switches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No switch named '{name}'. The switches are: {List(Switches.Select(s => s.Name))}.");

    private static string List(IEnumerable<string> names) => names.Any() ? string.Join(", ", names) : "(none)";

    /// <summary>
    /// Processes <paramref name="count"/> samples of every channel: <c>inputs[c]</c> into <c>outputs[c]</c>, for each
    /// channel c. Each channel runs its own copy of the circuit.
    /// </summary>
    public void Process(double[][] inputs, double[][] outputs, int count)
    {
        if (inputs.Length < Channels || outputs.Length < Channels)
            throw new ArgumentException($"Expected {Channels} input and output buffers.");
        // The controls' current targets, read once so every channel sees the same ones. Nothing here allocates: moving a
        // pot or switch only recomputes the engines' folded coefficients, in place.
        bool smoothing = false;
        for (int k = 0; k < switches.Length; k++)
            switches[k].Latch();
        for (int k = 0; k < pots.Length; k++)
        {
            pots[k].Latch(smoothingSamples);
            smoothing |= pots[k].Moving;
        }

        for (int start = 0; start < count;)
        {
            int length = smoothing ? Math.Min(SmoothingStep, count - start) : count - start;
            for (int k = 0; k < pots.Length; k++)
                pots[k].Advance(length);
            for (int c = 0; c < Channels; c++)
            {
                double[] values = engines[c].ParameterValues;
                for (int k = 0; k < pots.Length; k++)
                    pots[k].Apply(values);
                for (int k = 0; k < switches.Length; k++)
                    switches[k].Apply(values);
                Run(engines[c], inputs[c], start, length, outputs[c]);
            }
            start += length;
        }
    }

    /// <summary>Processes samples through a single-channel processor: <paramref name="input"/> into
    /// <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<double> input, Span<double> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("The output is shorter than the input.", nameof(output));
        if (Channels != 1)
            throw new InvalidOperationException("With several channels, process them together with Process(inputs, outputs, count).");
        if (spanIn[0] == null || spanIn[0].Length < input.Length)
        {
            spanIn[0] = new double[Math.Max(input.Length, 4096)];
            spanOut[0] = new double[Math.Max(input.Length, 4096)];
        }
        input.CopyTo(spanIn[0]);
        Process(spanIn, spanOut, input.Length);
        spanOut[0].AsSpan(0, input.Length).CopyTo(output);
    }

    // Kept out of Run: a lambda capturing Run's parameters would make the compiler allocate its closure on every call.
    private void Grow(int length)
    {
        int size = Math.Max(length, 4096);
        buffers = Enumerable.Range(0, 2 * speakerCount).Select(_ => new double[size]).ToArray();
        chunkIn = new double[size];
    }

    private void Run(CompiledCircuit engine, double[] input, int start, int length, double[] output)
    {
        if (buffers.Length != 2 * speakerCount || buffers[0].Length < length || chunkIn.Length < length)
            Grow(length);
        Array.Copy(input, start, chunkIn, 0, length);
        engine.Run(chunkIn, buffers, length);
        for (int n = 0; n < length; n++)
        {
            double sum = 0;
            for (int s = 0; s < speakerCount; s++)
                sum += (buffers[2 * s][n] - buffers[2 * s + 1][n]) * scales[s];
            output[start + n] = sum;
        }
    }
}

/// <summary>A pot, or a group of ganged pots, of a <see cref="SchematicProcessor"/>.</summary>
public sealed class PotControl
{
    private readonly PotParameter[] pots;
    private readonly int[] indices;
    private double target;
    private double rampStart, rampTarget, current;
    private long rampLength, rampDone;

    internal PotControl(string name, double initial, PotParameter[] pots, int[] indices)
    {
        Name = name;
        DefaultPosition = initial;
        this.pots = pots;
        this.indices = indices;
        target = rampStart = rampTarget = current = initial;
    }

    /// <summary>The pot's name, or its group's.</summary>
    public string Name { get; }

    /// <summary>The wiper position in the schematic, from 0 to 1.</summary>
    public double DefaultPosition { get; }

    /// <summary>The pot's taper, which maps <see cref="Position"/> to its resistance.</summary>
    public SweepType Taper => pots[0].Sweep;

    /// <summary>The wiper position the pot is set to, from 0 to 1. Safe to set from any thread; the processor moves the
    /// pot there, smoothly, from its next block.</summary>
    public double Position
    {
        get => Volatile.Read(ref target);
        set
        {
            if (!(value >= 0 && value <= 1))
                throw new ArgumentOutOfRangeException(nameof(value), $"Pot '{Name}' position {value} isn't between 0 and 1.");
            Volatile.Write(ref target, value);
        }
    }

    internal bool Moving => rampDone < rampLength;

    internal void Latch(long smoothing)
    {
        double wanted = Volatile.Read(ref target);
        if (wanted == rampTarget)
            return;
        rampStart = current;
        rampTarget = wanted;
        rampLength = smoothing;
        rampDone = 0;
        if (smoothing == 0)
            current = wanted;
    }

    internal void Advance(int samples)
    {
        if (!Moving)
            return;
        rampDone = Math.Min(rampLength, rampDone + samples);
        current = rampStart + (rampTarget - rampStart) * rampDone / rampLength;
    }

    internal void Apply(double[] values)
    {
        for (int k = 0; k < indices.Length; k++)
            values[indices[k]] = pots[k].ValueAt(current);
    }
}

/// <summary>A switch, or a group of ganged switches, of a <see cref="SchematicProcessor"/>.</summary>
public sealed class SwitchControl
{
    private int target;
    private readonly int[] indices;
    private readonly SwitchParameter[] contacts;

    internal SwitchControl(string name, int positions, int initial, int[] indices, SwitchParameter[] contacts)
    {
        Name = name;
        Positions = positions;
        DefaultPosition = initial;
        target = Applied = initial;
        this.indices = indices;
        this.contacts = contacts;
    }

    /// <summary>The switch's name, or its group's.</summary>
    public string Name { get; }

    /// <summary>How many positions it has: the throws of a multi-throw switch, or 2 (off, on) for an on/off switch.</summary>
    public int Positions { get; }

    /// <summary>The position in the schematic.</summary>
    public int DefaultPosition { get; }

    /// <summary>The position the switch is set to: the throw it connects, from 0, or 0 off and 1 on. Safe to set from
    /// any thread; it takes effect at the start of the processor's next block.</summary>
    public int Position
    {
        get => Volatile.Read(ref target);
        set
        {
            if (value < 0 || value >= Positions)
                throw new ArgumentOutOfRangeException(nameof(value), $"Switch '{Name}' has positions 0 to {Positions - 1}, not {value}.");
            Volatile.Write(ref target, value);
        }
    }

    internal int Applied { get; private set; }

    internal void Latch() => Applied = Volatile.Read(ref target);

    internal void Apply(double[] values)
    {
        for (int k = 0; k < indices.Length; k++)
            values[indices[k]] = contacts[k].ValueAt(Applied);
    }
}
