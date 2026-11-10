using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Checks dynamic pots. At fixed settings, the dynamic engine should give the same result as the same circuit with
/// its pots baked in. Swept while running, it should stay stable, agree with a tightly converged sweep, and the
/// compiled version should agree with the interpreter.
/// </summary>
internal static class DynamicPotCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    public static int Samples = 4800;
    public static int SweepSamples = 24000;
    public static bool Timing;
    public static int SweepBlock = 64;
    public static int SweepCap = IterationCap;

    private static readonly (string Name, double[]? Wipes)[] Settings =
    {
        ("schematic", null),
        ("all 0", new[] { 0.0 }),
        ("all 1", new[] { 1.0 }),
        ("all 0.1", new[] { 0.1 }),
        ("all 0.9", new[] { 0.9 }),
        ("0.2/0.8/0.5", new[] { 0.2, 0.8, 0.5 }),
        ("0.95/0.05", new[] { 0.95, 0.05 }),
        ("0.9/0.1", new[] { 0.9, 0.1 }),
    };

    /// <summary>
    /// A small set covering the main structures, for checking while iterating: one diode pair, free unknowns, a large
    /// circuit, high gain, op amps with several pots, and a linear-only circuit.
    /// </summary>
    public static readonly string[] QuickSet =
    {
        "Ibanez Tube Screamer TS-9",
        "Boss Super Overdrive SD-1",
        "Marshall Blues Breaker",
        "Big Muff Pi",
        "Pro Co Rat",
        "59 Bassman Tone Stack",
    };

    /// <summary>Runs these circuits only (--quick).</summary>
    public static bool Quick;

    /// <summary>Circuits to run at once, each in its own process (--jobs N).</summary>
    public static int Jobs = 1;

    /// <summary>Arguments a child needs to run the same checks (everything but the circuit selection and --jobs).</summary>
    public static string[] ChildArguments = Array.Empty<string>();

    public static int Run(string testsDir, string? filter, string? exactFile = null)
    {
        var files = exactFile != null ? new List<string> { exactFile } : new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(p => !Quick || QuickSet.Contains(Path.GetFileNameWithoutExtension(p)))
            .ToList();

        if (Jobs > 1 && files.Count > 1)
        {
            var watch = Stopwatch.StartNew();
            int problems = ParallelRunner.Run(files.Select(f => new[] { "dynamic", "--file", f }.Concat(ChildArguments).ToArray()).ToList(), Jobs);
            Console.WriteLine($"{problems} problem(s).");
            Phases.Report(Console.Out);
            Console.WriteLine($"{files.Count} circuits in {watch.Elapsed.TotalSeconds:F1} s with {Jobs} jobs.");
            return problems == 0 ? 0 : 1;
        }

        int failures = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            var probe = Phases.Time("load schematic", () => Schematic.Load(file, new NullLog()).Build());
            if (!probe.Components.OfType<IPotControl>().Any())
                continue;

            Console.WriteLine($"=== {name}");
            try
            {
                failures += CheckSettings(file);
                failures += CheckSweep(file);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{failures} problem(s).");
        Phases.Report(Console.Out);
        return failures == 0 ? 0 : 1;
    }

    private static Expression h => (Real)1 / (SampleRate * Oversample);

    private sealed record Built(Circuit.Circuit Circuit, ExtractedSystem System, TransientSolution Solution, Expression Input, Expression[] Outputs, IReadOnlyList<PotParameter> Pots);

    // Loads the circuit with its pots at a setting, twice: baked in, and with pots as runtime parameters. Both share
    // LiveSPICE's solution of the baked circuit (the dynamic engine only uses it for its stopping rule).
    private static (Built Baked, Built Dynamic) Load(string file, double[]? wipes)
    {
        Circuit.Circuit Make()
        {
            var circuit = Phases.Time("load schematic", () => Schematic.Load(file, new NullLog()).Build());
            var pots = circuit.Components.OfType<IPotControl>().ToList();
            if (wipes != null)
                for (int i = 0; i < pots.Count; i++)
                    pots[i].PotValue = wipes[i % wipes.Length];
            return circuit;
        }

        Built Finish(Circuit.Circuit circuit, TransientSolution solution, IReadOnlyList<PotParameter> parameters)
        {
            ExtractedSystem system = Phases.Time("extract", () => Extractor.Extract(circuit.Analyze(), h, parameters));
            Expression input = circuit.Components.Where(i => i is Input).Select(i => Component.DependentVariable(i.Name, Component.t)).DefaultIfEmpty("V[t]").Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();
            return new Built(circuit, system, solution, input, outputs, parameters);
        }

        Circuit.Circuit baked = Make();
        TransientSolution solution = Phases.Time("LiveSPICE solve", () => TransientSolution.Solve(baked.Analyze(), h));
        Circuit.Circuit dynamic = Make();
        IReadOnlyList<PotParameter> parameters = DynamicPots.Replace(dynamic);
        return (Finish(baked, solution, Array.Empty<PotParameter>()), Finish(dynamic, solution, parameters));
    }

    private static int CheckSettings(string file)
    {
        int failures = 0;
        Console.WriteLine($"  {"setting",-14}{"in",5}{"dynamic vs baked dB",21}{"iters baked",13}{"iters dynamic",15}{"capped b/d",12}  status");
        foreach (var setting in Settings)
        {
            (Built baked, Built dynamic) = Load(file, setting.Wipes);
            foreach (double amplitude in new[] { 0.5, 5.0 })
            {
                double[] input = Harmonics(Samples, amplitude);
                var a = Phases.Time("build interpreter", () => new CircuitInterpreter(baked.System, baked.Input, baked.Outputs, h, Oversample, IterationCap, baked.Solution));
                var b = Phases.Time("build interpreter", () => new CircuitInterpreter(dynamic.System, dynamic.Input, dynamic.Outputs, h, Oversample, IterationCap, dynamic.Solution));
                double[][] outA = baked.Outputs.Select(_ => new double[Samples]).ToArray();
                double[][] outB = dynamic.Outputs.Select(_ => new double[Samples]).ToArray();
                Phases.Time("settings: simulate", () => { a.Run(input, outA); b.Run(input, outB); });
                double db = Signals.WorstErrorDb(outA, outB);
                bool ok = db <= -100;
                if (!ok)
                    failures++;
                Console.WriteLine($"  {setting.Name,-14}{amplitude,5:F1}{db,21:F1}{a.IterationCounts.Sum(x => x & 0x7F),13}{b.IterationCounts.Sum(x => x & 0x7F),15}{a.IterationCounts.Count(x => (x & 0x80) != 0),6}/{b.IterationCounts.Count(x => (x & 0x80) != 0),-5}  {(ok ? "ok" : "CHECK")}");
            }
        }
        return failures;
    }

    // Sweeps every pot end to end and back while running, updating every SweepBlock samples.
    private static int CheckSweep(string file)
    {
        (Built baked, Built built) = Load(file, null);
        double[] input = Harmonics(SweepSamples, 1.0);
        int pots = built.Pots.Count;
        double Wipe(int n, int pot) { double phase = (n / (double)SweepSamples + pot / (double)Math.Max(pots, 1)) % 1; return phase < 0.5 ? 2 * phase : 2 - 2 * phase; }

        double[] Run(Action<double[][], double[], int, int> run, double[] parameters)
        {
            var result = new double[built.Outputs.Length * SweepSamples];
            double[][] buffers = built.Outputs.Select(_ => new double[SweepBlock]).ToArray();
            for (int start = 0; start < SweepSamples; start += SweepBlock)
            {
                for (int p = 0; p < pots; p++)
                    parameters[p] = built.Pots[p].ValueAt(Wipe(start, p));
                run(buffers, input, start, SweepBlock);
                for (int o = 0; o < buffers.Length; o++)
                    Array.Copy(buffers[o], 0, result, o * SweepSamples + start, SweepBlock);
            }
            return result;
        }

        var engine = Phases.Time("build interpreter", () => new CircuitInterpreter(built.System, built.Input, built.Outputs, h, Oversample, SweepCap, built.Solution));
        double[] ours = Phases.Time("sweep: interpreter", () => Run((b, x, s, c) => engine.Run(x.AsSpan(s, c).ToArray(), b), engine.ParameterValues));

        // A tightly converged sweep to compare against. It only changes with the circuit and the sweep, so it is cached
        // on disk (--refresh recomputes it).
        double[] reference = Phases.Time("sweep: tight reference", () => ReferenceCache.GetOrCreate(file, $"sweep {SweepSamples} {SweepBlock}", () =>
        {
            var tight = new CircuitInterpreter(built.System, built.Input, built.Outputs, h, Oversample, 100, built.Solution)
            {
                RelativeTolerance = 1e-10,
                AbsoluteTolerance = 1e-12,
            };
            return Run((b, x, s, c) => tight.Run(x.AsSpan(s, c).ToArray(), b), tight.ParameterValues);
        }));

        var compiled = Phases.Time("JIT compile", () => new CompiledCircuit(built.System, built.Input, built.Outputs, h, Oversample, IterationCap, built.Solution));
        var watch = Stopwatch.StartNew();
        double[] jit = Run((b, x, s, c) => compiled.Run(x.AsSpan(s, c).ToArray(), b), compiled.ParameterValues);
        double dynamicUs = watch.Elapsed.TotalMilliseconds * 1000 / SweepSamples;
        Phases.Add("sweep: compiled", watch.Elapsed);

        var staticCompiled = Phases.Time("JIT compile", () => new CompiledCircuit(baked.System, baked.Input, baked.Outputs, h, Oversample, IterationCap, baked.Solution));
        double[][] scratch = baked.Outputs.Select(_ => new double[SweepBlock]).ToArray();
        watch.Restart();
        for (int start = 0; start < SweepSamples; start += SweepBlock)
            staticCompiled.Run(input.AsSpan(start, SweepBlock).ToArray(), scratch);
        double staticUs = watch.Elapsed.TotalMilliseconds * 1000 / SweepSamples;
        Phases.Add("sweep: compiled", watch.Elapsed);
        string stillNote = "";
        if (Timing)
        {
            var timingWatch = Stopwatch.StartNew();

            // Profiling: the same circuit with every live coefficient compiled as a constant, and repeated timings.
            double Best(Func<CompiledCircuit> make)
            {
                double best = double.MaxValue;
                for (int pass = 0; pass < 3; pass++)
                {
                    var engineUnderTest = make();
                    double[][] buffers = built.Outputs.Select(_ => new double[SweepBlock]).ToArray();
                    var timer = Stopwatch.StartNew();
                    for (int start = 0; start < SweepSamples; start += SweepBlock)
                        engineUnderTest.Run(input.AsSpan(start, SweepBlock).ToArray(), buffers);
                    best = Math.Min(best, timer.Elapsed.TotalMilliseconds * 1000 / SweepSamples);
                }
                return best;
            }
            double liveBest = Best(() => new CompiledCircuit(built.System, built.Input, built.Outputs, h, Oversample, IterationCap, built.Solution));
            CompiledCircuit.InlineLiveForTesting = true;
            double inlinedBest = Best(() => new CompiledCircuit(built.System, built.Input, built.Outputs, h, Oversample, IterationCap, built.Solution));
            CompiledCircuit.InlineLiveForTesting = false;
            double bakedBest = Best(() => new CompiledCircuit(baked.System, baked.Input, baked.Outputs, h, Oversample, IterationCap, baked.Solution));
            Console.WriteLine($"  best of 3, pots still: baked {bakedBest:F2} us, dynamic {liveBest:F2} us, dynamic with live coefficients inlined {inlinedBest:F2} us");
            // The same compiled dynamic engine with its pots held still, so it never recomputes its reduction.
            var still = new CompiledCircuit(built.System, built.Input, built.Outputs, h, Oversample, IterationCap, built.Solution);
            double[][] stillBuffers = built.Outputs.Select(_ => new double[SweepBlock]).ToArray();
            watch.Restart();
            for (int start = 0; start < SweepSamples; start += SweepBlock)
                still.Run(input.AsSpan(start, SweepBlock).ToArray(), stillBuffers);
            double stillUs = watch.Elapsed.TotalMilliseconds * 1000 / SweepSamples;
            int reads = ArrayReads.Count(CompiledCircuit.LastStepForTesting!);
            CompiledCircuit.InlineLiveForTesting = true;
            _ = new CompiledCircuit(built.System, built.Input, built.Outputs, h, Oversample, IterationCap, built.Solution);
            CompiledCircuit.InlineLiveForTesting = false;
            int inlinedReads = ArrayReads.Count(CompiledCircuit.LastStepForTesting!);
            Console.WriteLine($"  live coefficient reads per step: {reads - inlinedReads}");
            Phases.Add("timing (best of 3 etc.)", timingWatch.Elapsed);
            stillNote = $"; compiled with pots still {stillUs:F2} us/sample";
        }
        static int NonZero(double[,] matrix) { int count = 0; foreach (double x in matrix) if (x != 0) count++; return count; }
        var bakedPlan = new CircuitInterpreter(baked.System, baked.Input, baked.Outputs, h, Oversample, IterationCap, baked.Solution);
        Console.WriteLine($"  inputs per step {baked.System.Parameters.Length} baked / {built.System.Parameters.Length} dynamic; gain nonzeros {NonZero(bakedPlan.parameterGain)} / {NonZero(engine.parameterGain)} of {bakedPlan.parameterGain.Length} / {engine.parameterGain.Length}");
        if (engine.reduction is LinearPortReduction red)
        {
            int affected = red.Affected.Count(x => x), live = red.GainUsed.Where((row, i) => red.Affected[i]).Sum(row => row.Count(x => x));
            int bakedRow = 0;
            for (int i = 0; i < baked.System.Unknowns.Length; i++)
            {
                int j = Array.IndexOf(built.System.Unknowns, baked.System.Unknowns[i]);
                if (j >= 0 && red.Affected[j])
                    for (int c = 0; c < bakedPlan.parameterGain.GetLength(1); c++)
                        if (bakedPlan.parameterGain[i, c] != 0) bakedRow++;
            }
            Console.WriteLine($"  affected unknowns {affected} of {red.Affected.Length}; their live gains {live} vs {bakedRow} nonzero baked gains for the same unknowns; "
                + $"term gains {red.TermGainUsed.Sum(row => row.Count(x => x))}; nonlinear ports {red.N.Length}, eliminated {red.L.Length}; free unknowns baked {bakedPlan.dz}, dynamic {engine.dz}; nonlinear terms {red.TN.Length} (baked {bakedPlan.m})");
        }
        Console.WriteLine($"  sizes: unknowns {baked.System.Unknowns.Length} baked / {built.System.Unknowns.Length} dynamic; ports {baked.System.Ports} / {built.System.Ports} "
            + $"({engine.EliminatedPorts} eliminated); terms {baked.System.Nonlinear.Length} / {built.System.Nonlinear.Length}; "
            + $"reductions recomputed during the sweep {engine.ReductionUpdates}{stillNote}.");
        double[][] Split(double[] all) => Enumerable.Range(0, built.Outputs.Length).Select(o => all.AsSpan(o * SweepSamples, SweepSamples).ToArray()).ToArray();
        bool finite = ours.All(double.IsFinite);
        double vsTight = Signals.WorstErrorDb(Split(reference), Split(ours));
        // The compiled reduction folds the eliminated ports into its coefficients, so it rounds differently.
        double jitDb = Signals.WorstErrorDb(Split(ours), Split(jit));
        bool same = jitDb <= -100;
        int capped = engine.IterationCounts.Count(x => (x & 0x80) != 0);
        double meanIterations = engine.IterationCounts.Count == 0 ? 0 : engine.IterationCounts.Average(x => x & 0x7F);

        if (capped > 100)
        {
            // Where the capped steps fall, with each pot's position at the middle of each tenth of the sweep.
            int tenth = engine.IterationCounts.Count / 10;
            for (int part = 0; part < 10; part++)
            {
                int count = engine.IterationCounts.Skip(part * tenth).Take(tenth).Count(x => (x & 0x80) != 0);
                int middle = (part * tenth + tenth / 2) / Oversample;
                string positions = string.Join(" ", Enumerable.Range(0, pots).Select(p => Wipe(middle, p).ToString("F2")));
                Console.WriteLine($"    {part * 0.2:F1}-{part * 0.2 + 0.2:F1} s: {count,6} capped, wipes {positions}");
            }
        }
        bool ok = finite && same;
        Console.WriteLine($"  sweep of {pots} pot(s) over {SweepSamples / (double)SampleRate:F1} s: {(finite ? "stable" : "NOT FINITE")}, vs tightly converged sweep {vsTight:F1} dB, "
            + $"{meanIterations:F2} iterations per step, {capped} capped; JIT vs interpreter {jitDb:F1} dB; "
            + $"compiled {dynamicUs:F2} us/sample dynamic vs {staticUs:F2} static; linear-only {engine.LinearOnly}.");
        return ok ? 0 : 1;
    }

    private static double[] Harmonics(int samples, double amplitude)
    {
        var x = new double[samples];
        for (int n = 0; n < samples; n++)
            x[n] = Signals.Harmonics(n / (double)SampleRate, amplitude, 82, 2);
        return x;
    }
}
