using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Experiment: how much exact segmentation appears when op-amps are ideal. LiveSPICE's OpAmp macromodel has a 100 Ω
/// output, finite gain and rail-clamp diodes, so stages on either side of it load each other within a sample and merge
/// into one Newton group. An IdealOpAmp draws no input current and drives its output as an ideal source, so both of its
/// sides should become one-way boundaries, which the strongly connected components split exactly. Reports each
/// circuit's segments both ways, and how far the ideal version's output is from the macromodel's (that is a change to
/// the circuit, not an error of the engine).
/// </summary>
internal static class IdealOpAmpCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Expression h = (Real)1 / (SampleRate * Oversample);
        Console.WriteLine($"{"circuit",-44}{"op-amps",8}  {"segments (ports each): macromodel",-36}{"ideal",-24}{"ideal vs macro dB",18}");
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!OpAmpsOf(Schematic.Load(file, new NullLog()).Build()).Any())
                continue;
            try
            {
                var (macroSegments, macroOut) = Simulate(file, h, ideal: false, out int count);
                var (idealSegments, idealOut) = Simulate(file, h, ideal: true, out _);
                string[] common = macroOut.Keys.Intersect(idealOut.Keys).OrderBy(k => k).ToArray();
                double db = Signals.WorstErrorDb(common.Select(k => macroOut[k]).ToArray(), common.Select(k => idealOut[k]).ToArray());
                Console.WriteLine($"{(name.Length <= 43 ? name : name[..43]),-44}{count,8}  {macroSegments,-36}{idealSegments,-24}{db,18:F1}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{name,-44}  error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return 0;
    }

    private static (string Segments, Dictionary<string, double[]> Outputs) Simulate(string file, Expression h, bool ideal, out int opAmps)
    {
        Circuit.Circuit circuit = Schematic.Load(file, new NullLog()).Build();
        opAmps = OpAmpsOf(circuit).Count();
        if (ideal)
            MakeIdeal(circuit);
        TransientSolution solution = TransientSolution.Solve(circuit.Analyze(), h);
        ExtractedSystem system = Extractor.Extract(circuit.Analyze(), h);
        Expression input = circuit.Components.OfType<Input>().Select(i => Component.DependentVariable(i.Name, Component.t)).DefaultIfEmpty("V[t]").Single();
        // Compare on the nodes both versions have (the macromodel's internal nodes don't exist in the ideal one).
        Expression[] nodes = circuit.Nodes.Select(n => n.V).Where(v => system.Unknowns.Contains(v)).OrderBy(v => v.ToString()).ToArray();
        var engine = new CircuitInterpreter(system, input, nodes, h, Oversample, IterationCap, solution);
        var signal = Enumerable.Range(0, Samples).Select(n => Signals.Harmonics(n / (double)SampleRate, 0.5, 82, 2)).ToArray();
        double[][] outputs = nodes.Select(_ => new double[Samples]).ToArray();
        engine.Run(signal, outputs);
        string segments = engine.Segments.Count == 0 ? "(linear)"
            : $"{engine.Segments.Count}: " + string.Join("+", engine.Segments.Select(s => s.Ports.Length + (s.Free.Length > 0 ? $"(+{s.Free.Length}z)" : "")));
        return (segments, nodes.Select((v, i) => (v.ToString(), outputs[i])).ToDictionary(x => x.Item1, x => x.Item2));
    }

    private static IEnumerable<IdealOpAmp> OpAmpsOf(Circuit.Circuit circuit) =>
        circuit.Components.SelectMany(c => c is Circuit.Circuit inner ? OpAmpsOf(inner) : c is OpAmp op ? new IdealOpAmp[] { op } : Array.Empty<IdealOpAmp>());

    // Swaps every OpAmp macromodel, including inside subcircuits, for an IdealOpAmp on the same +, - and Out nodes.
    private static void MakeIdeal(Circuit.Circuit circuit)
    {
        foreach (Component component in circuit.Components.ToList())
        {
            if (component is Circuit.Circuit inner)
            {
                MakeIdeal(inner);
                continue;
            }
            if (component is not OpAmp op)
                continue;
            Node? p = op.Positive.ConnectedTo, n = op.Negative.ConnectedTo, o = op.Out.ConnectedTo;
            foreach (Terminal terminal in op.Terminals)
                terminal.ConnectTo(null);
            circuit.Components.Remove(op);
            var replacement = new IdealOpAmp { Name = op.Name };
            replacement.Positive.ConnectTo(p);
            replacement.Negative.ConnectTo(n);
            replacement.Out.ConnectTo(o);
            circuit.Components.Add(replacement);
        }
    }
}
