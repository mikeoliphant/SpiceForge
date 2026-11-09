using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Compares compiled code with the interpreter and benchmarks it against stock Simulation.</summary>
internal static class JitCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const double ThresholdDb = -180;
    private const double Amplitude = 0.5;
    private const int TimingPasses = 5;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"{"Circuit",-46}{"k",4}{"LS solve",9}{"vs interp dB",14}{"iters",7}{"out vs stock",13}{"stock us",10}{"jit us",9}{"speedup",9}{"build ms",10}  status");
        int passed = 0, failed = 0;
        var speedups = new List<double>();
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            string label = $"{(name.Length <= 45 ? name : name.Substring(0, 45)),-46}";
            try
            {
                var circuit = Schematic.Load(file, new NullLog()).Build();
                Expression h = (Real)1 / (SampleRate * Oversample);
                Analysis analysis = circuit.Analyze();
                ExtractedSystem system;
                TransientSolution solution;
                try
                {
                    system = Extractor.Extract(analysis, h);
                    solution = TransientSolution.Solve(analysis, h);
                }
                catch (Exception)
                {
                    continue;
                }

                Expression input = circuit.Components.Where(i => i is Input)
                    .Select(i => Component.DependentVariable(i.Name, Component.t))
                    .DefaultIfEmpty("V[t]")
                    .Single();
                Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

                var inputBuffer = new double[Samples];
                for (int n = 0; n < Samples; n++)
                    inputBuffer[n] = ReferenceCheck.Harmonics(n / (double)SampleRate, Amplitude, 82, 2);

                var build = Stopwatch.StartNew();
                var compiled = new CompiledCircuit(system, input, outputs, h, Oversample, IterationCap, solution);
                double buildMs = build.Elapsed.TotalMilliseconds;

                var interpreter = new CircuitInterpreter(system, input, outputs, h, Oversample, IterationCap, solution);
                double[][] expected = outputs.Select(_ => new double[Samples]).ToArray();
                double[][] actual = outputs.Select(_ => new double[Samples]).ToArray();
                interpreter.Run(inputBuffer, expected);
                compiled.Run(inputBuffer, actual);

                double db = ReferenceCheck.WorstErrorDb(expected, actual);
                bool sameIterations = interpreter.IterationCounts.SequenceEqual(compiled.IterationCounts);
                bool ok = sameIterations && db <= ThresholdDb;
                if (ok) passed++; else failed++;

                // Benchmark one output, as LiveSPICE's own benchmark does.
                Expression[] benchmarkOutput = { BenchmarkOutput(circuit, outputs) };
                var single = new CompiledCircuit(system, input, benchmarkOutput, h, Oversample, IterationCap, solution);
                expected = new[] { new double[Samples] };
                actual = new[] { new double[Samples] };

                var stock = new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { input }, Output = benchmarkOutput };
                stock.Run(inputBuffer, expected);
                single.Run(inputBuffer, actual);
                double singleDb = ReferenceCheck.WorstErrorDb(expected, actual);
                stock.Run(inputBuffer, expected);

                double stockUs = Measure(() => stock.Run(inputBuffer, expected));
                double jitUs = Measure(() => single.Run(inputBuffer, actual));
                double speedup = stockUs / jitUs;
                speedups.Add(speedup);

                Console.WriteLine($"{label}{system.Ports,4}{solution.Solutions.OfType<NewtonIteration>().Sum(s => s.UnknownDeltas.Count()),9}{db,14:F1}{(sameIterations ? "same" : "DIFF"),7}{singleDb,13:F1}{stockUs,10:F2}{jitUs,9:F2}{speedup,8:F2}x{buildMs,10:F0}  {(ok ? "ok" : "FAIL")}");
            }
            catch (Exception ex)
            {
                failed++;
                string message = ex.Message.Length > 100 ? ex.Message.Substring(0, 100) : ex.Message;
                Console.WriteLine($"{label}error: {ex.GetType().Name}: {message}");
            }
        }

        speedups.Sort();
        if (speedups.Count > 0)
            Console.WriteLine($"Speedup vs stock: min {speedups[0]:F2}x, median {speedups[speedups.Count / 2]:F2}x, max {speedups[^1]:F2}x");
        Console.WriteLine($"{passed} passed, {failed} not passing.");
        return failed == 0 ? 0 : 1;
    }

    private static Expression BenchmarkOutput(Circuit.Circuit circuit, Expression[] nodes)
    {
        Speaker? speaker = circuit.Components.OfType<Speaker>().FirstOrDefault();
        Expression? anode = speaker?.Anode.ConnectedTo?.V;
        return anode ?? nodes[0];
    }

    // Best of several passes, in microseconds per input sample.
    private static double Measure(Action run)
    {
        double best = double.MaxValue;
        for (int pass = 0; pass < TimingPasses; pass++)
        {
            var watch = Stopwatch.StartNew();
            run();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds * 1000 / Samples);
        }
        return best;
    }
}
