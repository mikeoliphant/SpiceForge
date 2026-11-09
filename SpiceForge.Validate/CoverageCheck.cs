using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Runs every test circuit through the compiled engine and compares it with stock Simulation.
/// A run that misses the threshold counts as sensitive, rather than failed, when stock Simulation
/// itself misses it after roundoff-sized random noise is added to its input.
/// </summary>
internal static class CoverageCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const double ThresholdDb = -80;

    private static readonly double[] Amplitudes = { 0.5, 5.0 };
    private static readonly double[] NoiseLevels = { 2e-16, 1e-15, 1e-14 };

    public static int Run(string testsDir, string? filter)
    {
        int failed = 0;
        foreach (string folder in Directory.GetDirectories(testsDir).Select(Path.GetFileName).Where(f => Directory.GetFiles(Path.Combine(testsDir, f!), "*.schx").Length > 0).OrderBy(f => f == "Unstable").ThenBy(f => f, StringComparer.Ordinal)!)
        {
            var counts = new Dictionary<string, int>();
            void Count(string status) => counts[status] = counts.GetValueOrDefault(status) + 1;

            Console.WriteLine($"=== {folder}");
            Console.WriteLine($"{"Circuit",-44}{"in",5}{"error dB",10}{"noise dB",10}{"jit us",9}  result");
            foreach (string file in Directory.GetFiles(Path.Combine(testsDir, folder!), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            {
                if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);
                string shortName = name.Length <= 43 ? name : name.Substring(0, 43);

                Circuit.Circuit circuit;
                Analysis analysis;
                Expression h = (Real)1 / (SampleRate * Oversample);
                TransientSolution solution;
                try
                {
                    circuit = Schematic.Load(file, new NullLog()).Build();
                    analysis = circuit.Analyze();
                    solution = TransientSolution.Solve(analysis, h);
                }
                catch (Exception ex)
                {
                    Count("LiveSPICE can't solve");
                    Console.WriteLine($"{shortName,-44}  LiveSPICE can't solve it: {Clip(ex.Message)}");
                    continue;
                }

                Expression input = circuit.Components.Where(i => i is Input)
                    .Select(i => Component.DependentVariable(i.Name, Component.t))
                    .DefaultIfEmpty("V[t]")
                    .Single();
                Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

                ExtractedSystem system;
                try
                {
                    system = Extractor.Extract(analysis, h);
                }
                catch (Exception ex)
                {
                    Count("error");
                    failed++;
                    Console.WriteLine($"{shortName,-44}  extraction error: {Clip(ex.Message)}");
                    continue;
                }

                foreach (double amplitude in Amplitudes)
                {
                    string label = $"{shortName,-44}{amplitude,5:F1}";
                    try
                    {
                        var inputBuffer = new double[Samples];
                        for (int n = 0; n < Samples; n++)
                            inputBuffer[n] = ReferenceCheck.Harmonics(n / (double)SampleRate, amplitude, 82, 2);

                        double[][] stock = NewBuffers(outputs.Length);
                        Stock(solution, input, outputs).Run(inputBuffer, stock);

                        var compiled = new CompiledCircuit(system, input, outputs, h, Oversample, IterationCap, solution, matchLiveSpice: true);
                        double[][] actual = NewBuffers(outputs.Length);
                        var watch = Stopwatch.StartNew();
                        compiled.Run(inputBuffer, actual);
                        double microseconds = watch.Elapsed.TotalMilliseconds * 1000 / Samples;

                        double errorDb = ReferenceCheck.WorstErrorDb(stock, actual);
                        double noiseDb = double.NegativeInfinity;
                        if (errorDb > ThresholdDb)
                        {
                            foreach (double level in NoiseLevels)
                            {
                                double[][] control = NewBuffers(outputs.Length);
                                Stock(solution, input, outputs).Run(TraceCheck.WithNoise(inputBuffer, level), control);
                                noiseDb = Math.Max(noiseDb, ReferenceCheck.WorstErrorDb(stock, control));
                            }
                        }

                        string result = errorDb <= ThresholdDb ? "pass" : noiseDb > ThresholdDb ? "sensitive" : "FAIL";
                        Count(result);
                        if (result == "FAIL")
                            failed++;
                        Console.WriteLine($"{label}{errorDb,10:F1}{(double.IsNegativeInfinity(noiseDb) ? "-" : noiseDb.ToString("F1")),10}{microseconds,9:F2}  {result}");
                    }
                    catch (Exception ex)
                    {
                        Count("error");
                        failed++;
                        Console.WriteLine($"{label}  error: {ex.GetType().Name}: {Clip(ex.Message)}");
                        if (ex is SingularSystemException && system.Rows != system.Unknowns.Length)
                            DescribeNonSquare(system, solution);
                    }
                }
            }

            Console.WriteLine($"{folder} totals: " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
            Console.WriteLine();
        }

        return failed == 0 ? 0 : 1;
    }

    private static void DescribeNonSquare(ExtractedSystem system, TransientSolution solution)
    {
        int n = system.Unknowns.Length;
        var newton = solution.Solutions.OfType<NewtonIteration>().SelectMany(s => s.Unknowns).ToHashSet();
        var assigned = solution.Solutions.OfType<LinearSolutions>().SelectMany(s => s.Solutions).Select(a => a.Left).ToHashSet();
        for (int j = 0; j < n; j++)
        {
            int entries = Enumerable.Range(0, system.Rows).Count(i => system.E[i, j] != 0);
            bool inQ = Enumerable.Range(0, system.Ports).Any(r => system.Q[r, j] != 0);
            if (entries <= 1 || (!newton.Contains(system.Unknowns[j]) && !assigned.Contains(system.Unknowns[j])))
                Console.WriteLine($"    {system.Unknowns[j]}: {entries} E entries, port {inQ}, newton {newton.Contains(system.Unknowns[j])}, assigned {assigned.Contains(system.Unknowns[j])}");
        }
        foreach (NewtonIteration set in solution.Solutions.OfType<NewtonIteration>())
            Console.WriteLine($"    Newton: {set.Unknowns.Count()} unknowns, {set.Equations.Count()} equations, {set.UnknownDeltas.Count()} solved numerically");
    }

    private static Simulation Stock(TransientSolution solution, Expression input, Expression[] outputs) =>
        new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { input }, Output = outputs };

    private static double[][] NewBuffers(int outputs) =>
        Enumerable.Range(0, outputs).Select(_ => new double[Samples]).ToArray();

    private static string Clip(string s) => s.Length <= 90 ? s : s.Substring(0, 90);
}
