using NAudio.Wave;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Evaluates a stage cut: splits a schematic at one node into an upstream circuit (the Input's side) and a downstream one
/// (the Speaker's side), runs them chained, and measures how far that is from the uncut circuit. The downstream side is
/// driven at the cut node by an ideal source following the upstream node's voltage, on top of the uncut circuit's DC
/// bias there. The upstream side sees either no load (a pure buffer, as netlist_codegen's sub-circuits) or a linear copy
/// of the downstream input network: its resistors, capacitors, inductors and pots reachable from the cut node, with the
/// nonlinear devices left out. Both halves run at the oversampled rate with the input interpolated exactly as the
/// engine does, and everything is tightly converged, so the only difference from the uncut circuit is the cut.
/// </summary>
internal static class CutCheck
{
    private const int Oversample = 8;
    private const int TightCap = 100;
    private const double TightRelative = 1e-10;
    private const double TightAbsolute = 1e-12;

    /// <summary>Nodes of a supply network (a B+ decoupling chain, say) to treat like rails: the components between them
    /// are copied into both halves, so stages aren't joined through the supply (each half gets its own).</summary>
    public static string[] SupplyNodes = Array.Empty<string>();

    public static int Run(string schematic, string wav, string? cutName, double seconds)
    {
        double[] input = ReadMono(wav, out int rate, seconds);
        Circuit.Circuit original = Load(schematic);
        if (cutName == null)
        {
            ListNodes(original);
            return 0;
        }
        Node cut = original.Nodes.FirstOrDefault(n => n.Name == cutName)
            ?? throw new ArgumentException($"No node named '{cutName}'. Run without a node to list them.");

        var supply = SupplyNodes.Select(name => original.Nodes.FirstOrDefault(n => n.Name == name)
            ?? throw new ArgumentException($"No supply node named '{name}'.")).ToHashSet();
        var (upstream, downstream, shared) = Partition(original, cut, supply);
        Console.WriteLine($"{Path.GetFileNameWithoutExtension(schematic)}, cut at {cutName}, {input.Length / (double)rate:F1} s at {rate} Hz.");
        Console.WriteLine($"  upstream: {string.Join(", ", upstream.OrderBy(x => x))}");
        Console.WriteLine($"  downstream: {string.Join(", ", downstream.OrderBy(x => x))}");
        if (supply.Count > 0)
            Console.WriteLine($"  copied into both: {string.Join(", ", shared.Where(name => original.Components.First(c => c.Name == name) is not (Ground or Rail)).OrderBy(x => x))}");

        Expression h = (Real)1 / (rate * Oversample);

        // The uncut circuit, tightly converged: the reference, and the DC bias at the cut node.
        var (originalEngine, originalNodes, originalSystem) = Engine(original, h, Oversample);
        double bias = originalEngine.history[0][Array.IndexOf(originalSystem.Unknowns, cut.V)];
        double[][] reference = Run(originalEngine, originalNodes.Length, input);
        Console.WriteLine($"  DC bias at the cut node: {bias:G6} V");

        double[] fine = Upsample(input);
        foreach (string load in new[] { "none", "linear" })
        {
            // Upstream: the original without the downstream side, with the chosen load at the cut node.
            Circuit.Circuit up = Load(schematic);
            var keep = new HashSet<string>(upstream.Concat(shared));
            if (load == "linear")
                keep.UnionWith(LinearLoad(original, cut, downstream));
            Remove(up, c => !keep.Contains(c.Name));
            var (upEngine, upNodes, _) = Engine(up, h, 1);
            double[][] upOut = Run(upEngine, upNodes.Length, fine);
            double[] cutVoltage = upOut[Array.IndexOf(upNodes, cut.V)];

            // Downstream: the original without the upstream side, driven at the cut node.
            Circuit.Circuit down = Load(schematic);
            Remove(down, c => upstream.Contains(c.Name));
            Node downCut = down.Nodes.First(n => n.Name == cutName);
            Node ground = down.Components.OfType<Ground>().First().Terminal.ConnectedTo!;
            var drive = new Input { Name = "CutDrive", V0dBFS = new Quantity(1, Units.V) };
            var offset = new VoltageSource { Name = "CutBias", Voltage = new Quantity(bias, Units.V) };
            var between = new Node { Name = "CutDrive.x" };
            down.Nodes.Add(between);
            down.Components.Add(drive);
            down.Components.Add(offset);
            drive.ConnectTo(between, ground);
            offset.ConnectTo(downCut, between);
            var (downEngine, downNodes, _) = Engine(down, h, 1, drive);
            double[][] downOut = Run(downEngine, downNodes.Length, cutVoltage.Select(v => v - bias).ToArray());

            // Back to the sample rate the way the engine averages its outputs, and compare on the nodes each side has.
            var chained = new Dictionary<Expression, double[]>();
            for (int i = 0; i < upNodes.Length; i++)
                chained[upNodes[i]] = Decimate(upOut[i]);
            for (int i = 0; i < downNodes.Length; i++)
                if (!chained.ContainsKey(downNodes[i]))
                    chained[downNodes[i]] = Decimate(downOut[i]);
            Report(load, original, originalNodes, reference, chained, cut, rate);
        }
        return 0;
    }

    // Upstream components, downstream components, and those both sides need (ground, rails and anything non-electrical).
    private static (HashSet<string> Up, HashSet<string> Down, HashSet<string> Shared) Partition(Circuit.Circuit circuit, Node cut, HashSet<Node> supply)
    {
        var excluded = new HashSet<Node>(supply) { cut };
        foreach (Component c in circuit.Components)
            if (c is Ground or Rail)
                foreach (Terminal t in c.Terminals)
                    if (t.ConnectedTo != null)
                        excluded.Add(t.ConnectedTo);

        var shared = new HashSet<string>();
        var electrical = new List<Component>();
        foreach (Component c in circuit.Components)
        {
            // Ground, rails, and the supply network (anything connected only to ground, rails and supply nodes).
            bool supplyOnly = supply.Count > 0 && c.Terminals.All(t => t.ConnectedTo is not Node node || (excluded.Contains(node) && node != cut));
            if (c is Ground or Rail || !c.Terminals.Any(t => t.ConnectedTo != null) || supplyOnly)
                shared.Add(c.Name);
            else
                electrical.Add(c);
        }

        // Union components that share a node other than the cut, ground and the rails.
        var parent = electrical.ToDictionary(c => c, c => c);
        Component Find(Component c) { while (parent[c] != c) c = parent[c] = parent[parent[c]]; return c; }
        var byNode = new Dictionary<Node, Component>();
        foreach (Component c in electrical)
            foreach (Terminal t in c.Terminals)
                if (t.ConnectedTo is Node node && !excluded.Contains(node))
                {
                    if (byNode.TryGetValue(node, out Component? other))
                        parent[Find(c)] = Find(other);
                    else
                        byNode[node] = c;
                }

        Component inputComponent = electrical.First(c => c is Input);
        Component speaker = electrical.First(c => c is Speaker);
        Component upRoot = Find(inputComponent), downRoot = Find(speaker);
        if (upRoot == downRoot)
            throw new ArgumentException("That node doesn't separate the Input from the Speaker.");

        var up = new HashSet<string>();
        var down = new HashSet<string>();
        foreach (Component c in electrical)
        {
            Component root = Find(c);
            // Anything on neither side (a load from the cut node to ground, say) stays with the upstream side.
            (root == downRoot ? down : up).Add(c.Name);
        }
        return (up, down, shared);
    }

    // The downstream components a linear load model keeps: linear passives reachable from the cut node through other
    // linear passives, without passing through ground or a rail.
    private static IEnumerable<string> LinearLoad(Circuit.Circuit circuit, Node cut, HashSet<string> downstream)
    {
        var stops = new HashSet<Node>();
        foreach (Component c in circuit.Components)
            if (c is Ground or Rail)
                foreach (Terminal t in c.Terminals)
                    if (t.ConnectedTo != null)
                        stops.Add(t.ConnectedTo);
        bool Linear(Component c) => c is Resistor or Capacitor or Inductor or Potentiometer or VariableResistor;
        var kept = new HashSet<string>();
        var frontier = new Queue<Node>(new[] { cut });
        var seen = new HashSet<Node> { cut };
        while (frontier.Count > 0)
        {
            Node node = frontier.Dequeue();
            foreach (Component c in circuit.Components.Where(c => downstream.Contains(c.Name) && Linear(c) && c.Terminals.Any(t => t.ConnectedTo == node)))
            {
                if (!kept.Add(c.Name))
                    continue;
                foreach (Terminal t in c.Terminals)
                    if (t.ConnectedTo is Node next && !stops.Contains(next) && seen.Add(next))
                        frontier.Enqueue(next);
            }
        }
        return kept;
    }

    private static void Report(string load, Circuit.Circuit original, Expression[] originalNodes, double[][] reference,
        Dictionary<Expression, double[]> chained, Node cut, int rate)
    {
        Speaker speaker = original.Components.OfType<Speaker>().First();
        double[] Out(Func<Expression, double[]?> node)
        {
            double[]? a = node(speaker.Anode.ConnectedTo!.V), c = node(speaker.Cathode.ConnectedTo!.V);
            int length = (a ?? c)!.Length;
            return Enumerable.Range(0, length).Select(n => (a?[n] ?? 0) - (c?[n] ?? 0)).ToArray();
        }
        double[]? Reference(Expression v) { int i = Array.IndexOf(originalNodes, v); return i < 0 ? null : reference[i]; }
        double[]? Chained(Expression v) => chained.TryGetValue(v, out double[]? x) ? x : null;

        double outputDb = Signals.WorstErrorDb(new[] { Out(Reference) }, new[] { Out(Chained) });
        double cutDb = Signals.WorstErrorDb(new[] { Reference(cut.V)! }, new[] { Chained(cut.V)! });
        Console.WriteLine($"  load {load,-7}: output {outputDb,7:F1} dB, cut node {cutDb,7:F1} dB (worst error relative to peak)");
    }

    private static (CircuitInterpreter Engine, Expression[] Nodes, ExtractedSystem System) Engine(Circuit.Circuit circuit, Expression h, int oversample, Input? drive = null)
    {
        TransientSolution solution = TransientSolution.Solve(circuit.Analyze(), h);
        ExtractedSystem system = Extractor.Extract(circuit.Analyze(), h);
        Input inputComponent = drive ?? circuit.Components.OfType<Input>().Single();
        Expression input = Component.DependentVariable(inputComponent.Name, Component.t);
        Expression[] nodes = circuit.Nodes.Select(n => n.V).Where(v => system.Unknowns.Contains(v)).ToArray();
        var engine = new CircuitInterpreter(system, input, nodes, h, oversample, TightCap, solution)
        {
            RelativeTolerance = TightRelative,
            AbsoluteTolerance = TightAbsolute,
        };
        return (engine, nodes, system);
    }

    private static double[][] Run(CircuitInterpreter engine, int outputs, double[] input)
    {
        const int block = 4800;
        var result = Enumerable.Range(0, outputs).Select(_ => new double[input.Length]).ToArray();
        for (int start = 0; start < input.Length; start += block)
        {
            int count = Math.Min(block, input.Length - start);
            double[][] buffers = Enumerable.Range(0, outputs).Select(_ => new double[count]).ToArray();
            engine.Run(input.AsSpan(start, count).ToArray(), buffers);
            for (int o = 0; o < outputs; o++)
                Array.Copy(buffers[o], 0, result[o], start, count);
        }
        return result;
    }

    // The input at each oversampled step, interpolated as the engine does: from the previous sample to this one.
    private static double[] Upsample(double[] input)
    {
        var fine = new double[input.Length * Oversample];
        double previous = 0;
        for (int n = 0; n < input.Length; n++)
        {
            double delta = (input[n] - previous) / Oversample;
            double value = previous;
            for (int j = 0; j < Oversample; j++)
                fine[n * Oversample + j] = value += delta;
            previous = input[n];
        }
        return fine;
    }

    // Averages each sample's oversampled steps, as the engine averages its outputs.
    private static double[] Decimate(double[] fine)
    {
        var coarse = new double[fine.Length / Oversample];
        for (int n = 0; n < coarse.Length; n++)
        {
            double sum = 0;
            for (int j = 0; j < Oversample; j++)
                sum += fine[n * Oversample + j];
            coarse[n] = sum / Oversample;
        }
        return coarse;
    }

    private static void ListNodes(Circuit.Circuit circuit)
    {
        foreach (Node node in circuit.Nodes.OrderBy(n => n.Name))
        {
            var names = circuit.Components.Where(c => c.Terminals.Any(t => t.ConnectedTo == node)).Select(c => c.Name);
            Console.WriteLine($"  {node.Name,-12} {string.Join(", ", names)}");
        }
    }

    private static Circuit.Circuit Load(string schematic) => Schematic.Load(schematic, new NullLog()).Build();

    private static void Remove(Circuit.Circuit circuit, Func<Component, bool> remove)
    {
        foreach (Component c in circuit.Components.ToList())
            if (remove(c))
            {
                foreach (Terminal t in c.Terminals)
                    t.ConnectTo(null);
                circuit.Components.Remove(c);
            }
        foreach (Node node in circuit.Nodes.ToList())
            if (!circuit.Components.Any(c => c.Terminals.Any(t => t.ConnectedTo == node)))
                circuit.Nodes.Remove(node);
    }

    private static double[] ReadMono(string path, out int rate, double seconds)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        rate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        var samples = reader.ToSampleProvider();
        int frames = (int)Math.Min(reader.SampleCount, (long)(seconds * rate));
        var buffer = new float[frames * channels];
        int read = samples.Read(buffer.AsSpan());
        return Enumerable.Range(0, read / channels).Select(n => (double)buffer[n * channels]).ToArray();
    }
}
