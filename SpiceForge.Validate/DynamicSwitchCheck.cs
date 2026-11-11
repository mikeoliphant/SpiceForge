using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Checks dynamic switches. At every combination of switch positions (up to <see cref="MaxCombinations"/>), the dynamic
/// engine should give the same result as the circuit with its switches baked in at those positions. Switched while
/// running, it should stay finite, agree with a tightly converged run switched the same way, and the compiled version
/// should agree with the interpreter. Pots are baked in throughout, so only the switches differ.
/// </summary>
internal static class DynamicSwitchCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const int MaxCombinations = 27;
    private const double ThresholdDb = -100;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Expression h = (Real)1 / (SampleRate * Oversample);
        int problems = 0;
        foreach (string file in files)
        {
            LoadedCircuit probe;
            try
            {
                probe = CircuitLoader.Load(file, h, dynamicPots: false);
            }
            catch (Exception)
            {
                continue; // LiveSPICE can't solve it (Unstable/Triode stage).
            }
            if (probe.Switches.Count == 0)
                continue;
            Console.WriteLine($"=== {Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "")}");
            try
            {
                problems += Check(file, h, probe);
            }
            catch (Exception ex)
            {
                problems++;
                Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private static int Check(string file, Expression h, LoadedCircuit probe)
    {
        // Each switch (or group) once, with its number of positions.
        var switches = probe.Switches.GroupBy(c => c.Switch).Select(g => (Name: g.Key, Positions: g.First().Positions)).ToArray();
        Console.WriteLine("  switches: " + string.Join(", ", switches.Select(s => $"{s.Name} ({s.Positions} positions)")));
        var combinations = new List<int[]> { Array.Empty<int>() };
        foreach (var sw in switches)
            combinations = combinations.SelectMany(c => Enumerable.Range(0, sw.Positions).Select(p => c.Append(p).ToArray())).ToList();
        if (combinations.Count > MaxCombinations)
            combinations = combinations.Take(MaxCombinations).ToList();

        int problems = 0;
        Console.WriteLine($"  {"positions",-14}{"in",5}{"vs baked (circuit) dB",21}{"(per node) dB",16}{"iters baked",13}{"iters dynamic",15}  status");
        foreach (int[] combination in combinations)
        {
            var positions = switches.Select((s, i) => (s.Name, combination[i])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.OrdinalIgnoreCase);
            LoadedCircuit baked = CircuitLoader.Load(file, h, dynamicPots: false, dynamicSwitches: false, switchPositions: positions);
            LoadedCircuit dynamic = CircuitLoader.Load(file, h, dynamicPots: false, dynamicSwitches: true, switchPositions: positions);
            foreach (double amplitude in new[] { 0.5, 5.0 })
            {
                double[] input = Harmonics(Samples, amplitude);
                var (bakedOut, aIterations) = Run(baked, h, input);
                var (dynamicOut, bIterations) = Run(dynamic, h, input);
                // Compare by node name: a closed contact can merge nodes in LiveSPICE's baked circuit.
                string[] common = bakedOut.Keys.Intersect(dynamicOut.Keys).OrderBy(k => k).ToArray();
                double[][] a = common.Select(k => bakedOut[k]).ToArray(), b = common.Select(k => dynamicOut[k]).ToArray();
                double db = Signals.WorstErrorDb(a, b);
                bool finite = b.All(o => o.All(double.IsFinite));
                // Judged against the circuit's peak: a node an open contact leaves floating sits near zero, and the
                // open contact's 10^15 ohms put a tiny voltage on it that a per-node measure inflates.
                double circuitDb = CircuitErrorDb(a, b);
                bool ok = finite && circuitDb <= ThresholdDb;
                if (!ok) problems++;
                Console.WriteLine($"  {string.Join("/", combination),-14}{amplitude,5:0.0}{circuitDb,21:F1}{db,16:F1}{aIterations,13}{bIterations,15}  {(ok ? "ok" : "CHECK")}");
            }
        }

        // Switching while running: every 0.1 s, the next combination.
        LoadedCircuit loaded = CircuitLoader.Load(file, h, dynamicPots: false);
        Expression inputSymbol = Input(loaded.Circuit);
        Expression[] nodes = Nodes(loaded);
        double[] signal = Harmonics(SampleRate / 2, 0.5);
        double[] Switched(Action<double[], int> run, double[] values)
        {
            var output = new double[signal.Length * nodes.Length];
            for (int start = 0, block = 0; start < signal.Length; start += Samples, block++)
            {
                int[] combination = combinations[block % combinations.Count];
                for (int s = 0; s < switches.Length; s++)
                    DynamicSwitches.Set(values, loaded.Parameters, switches[s].Name, combination[s]);
                run(output, start);
            }
            return output;
        }
        double[] RunBlocks(Action<double[], double[][]> engine, double[] output, int start)
        {
            int count = Math.Min(Samples, signal.Length - start);
            double[][] buffers = nodes.Select(_ => new double[count]).ToArray();
            engine(signal.AsSpan(start, count).ToArray(), buffers);
            for (int o = 0; o < nodes.Length; o++)
                Array.Copy(buffers[o], 0, output, o * signal.Length + start, count);
            return output;
        }
        var interpreter = new CircuitInterpreter(loaded.System, inputSymbol, nodes, h, Oversample, IterationCap, loaded.Solution);
        var tight = new CircuitInterpreter(loaded.System, inputSymbol, nodes, h, Oversample, 100, loaded.Solution) { RelativeTolerance = 1e-10, AbsoluteTolerance = 1e-12 };
        var compiled = new CompiledCircuit(loaded.System, inputSymbol, nodes, h, Oversample, IterationCap, loaded.Solution);
        double[] ours = Switched((o, s) => RunBlocks(interpreter.Run, o, s), interpreter.ParameterValues);
        double[] reference = Switched((o, s) => RunBlocks(tight.Run, o, s), tight.ParameterValues);
        double[] jit = Switched((o, s) => RunBlocks(compiled.Run, o, s), compiled.ParameterValues);
        double[][] Split(double[] all) => Enumerable.Range(0, nodes.Length).Select(o => all.AsSpan(o * signal.Length, signal.Length).ToArray()).ToArray();
        bool stable = ours.All(double.IsFinite);
        double vsTight = Signals.WorstErrorDb(Split(reference), Split(ours));
        double jitDb = Signals.WorstErrorDb(Split(ours), Split(jit));
        bool switchedOk = stable && jitDb <= -200;
        if (!switchedOk) problems++;
        Console.WriteLine($"  switched every 0.1 s over 0.5 s: {(stable ? "stable" : "NOT FINITE")}, vs tightly converged {vsTight:F1} dB, "
            + $"JIT vs interpreter {jitDb:F1} dB, {interpreter.IterationCounts.Count(c => (c & 0x80) != 0)} capped  {(switchedOk ? "ok" : "CHECK")}");
        return problems;
    }

    // Worst error on any node, relative to the largest value on any node.
    private static double CircuitErrorDb(double[][] reference, double[][] actual)
    {
        double peak = reference.Max(o => o.Max(Math.Abs)), worst = 0;
        for (int o = 0; o < reference.Length; o++)
            for (int n = 0; n < reference[o].Length; n++)
            {
                double difference = Math.Abs(reference[o][n] - actual[o][n]);
                if (double.IsNaN(difference))
                    return double.PositiveInfinity;
                worst = Math.Max(worst, difference);
            }
        return worst == 0 ? double.NegativeInfinity : 20 * Math.Log10(worst / Math.Max(peak, 1e-300));
    }

    private static (Dictionary<string, double[]> Outputs, long Iterations) Run(LoadedCircuit loaded, Expression h, double[] input)
    {
        Expression[] nodes = Nodes(loaded);
        var engine = new CircuitInterpreter(loaded.System, Input(loaded.Circuit), nodes, h, Oversample, IterationCap, loaded.Solution);
        double[][] outputs = nodes.Select(_ => new double[input.Length]).ToArray();
        engine.Run(input, outputs);
        return (nodes.Select((v, i) => (v.ToString(), outputs[i])).ToDictionary(x => x.Item1, x => x.Item2), engine.IterationCounts.Sum(c => (long)(c & 0x7F)));
    }

    // The circuit's own nodes (not the switches' internal unknowns), in a stable order, so both versions compare.
    private static Expression[] Nodes(LoadedCircuit loaded) =>
        loaded.Circuit.Nodes.Select(n => n.V).Where(v => loaded.System.Unknowns.Contains(v)).OrderBy(v => v.ToString()).ToArray();

    private static Expression Input(Circuit.Circuit circuit) =>
        circuit.Components.OfType<Circuit.Input>().Select(i => Component.DependentVariable(i.Name, Component.t)).DefaultIfEmpty("V[t]").Single();

    private static double[] Harmonics(int samples, double amplitude) =>
        Enumerable.Range(0, samples).Select(n => Signals.Harmonics(n / (double)SampleRate, amplitude, 82, 2)).ToArray();
}
