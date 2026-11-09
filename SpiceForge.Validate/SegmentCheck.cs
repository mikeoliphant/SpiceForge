using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Checks that solving port groups in dependency order gives the same result as the joint solve.</summary>
internal static class SegmentCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const double ThresholdDb = -180;

    private static readonly double[] Amplitudes = { 0.5, 5.0 };

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"{"Circuit",-46}{"in",5}{"k",4}{"groups",18}{"blocks",8}{"dropped",10}{"diff dB",10}{"iters",7}{"same",6}{"joint us",10}{"split us",10}  status");
        int passed = 0, failed = 0, skipped = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
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
                skipped++;
                continue;
            }
            if (system.Nonlinear.Length == 0)
                continue;

            Expression input = circuit.Components.Where(i => i is Input)
                .Select(i => Component.DependentVariable(i.Name, Component.t))
                .DefaultIfEmpty("V[t]")
                .Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

            foreach (double amplitude in Amplitudes)
            {
                string label = $"{(name.Length <= 45 ? name : name.Substring(0, 45)),-46}{amplitude,5:F1}";
                try
                {
                    var inputBuffer = new double[Samples];
                    for (int n = 0; n < Samples; n++)
                        inputBuffer[n] = ReferenceCheck.Harmonics(n / (double)SampleRate, amplitude, 82, 2);

                    var joint = new CircuitInterpreter(system, input, outputs, h, Oversample, IterationCap, solution, segment: false, matchLiveSpice: true);
                    var split = new CircuitInterpreter(system, input, outputs, h, Oversample, IterationCap, solution, segment: true, matchLiveSpice: true);

                    double[][] jointOut = outputs.Select(_ => new double[Samples]).ToArray();
                    double[][] splitOut = outputs.Select(_ => new double[Samples]).ToArray();
                    var watch = Stopwatch.StartNew();
                    joint.Run(inputBuffer, jointOut);
                    double jointUs = watch.Elapsed.TotalMilliseconds * 1000 / Samples;
                    watch.Restart();
                    split.Run(inputBuffer, splitOut);
                    double splitUs = watch.Elapsed.TotalMilliseconds * 1000 / Samples;

                    double db = ReferenceCheck.WorstErrorDb(jointOut, splitOut);
                    bool sameIterations = joint.IterationCounts.SequenceEqual(split.IterationCounts);
                    bool identical = jointOut.Zip(splitOut, (a, b) => a.Zip(b, (x, y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)).All(s => s)).All(s => s);
                    bool ok = sameIterations && db <= ThresholdDb;
                    if (ok) passed++; else failed++;

                    string groups = string.Join("+", split.GroupSizes);
                    Console.WriteLine($"{label}{system.Ports,4}{groups,18}{split.StateBlocks,8}{split.LargestDroppedCoupling,10:E1}{db,10:F1}{(sameIterations ? "same" : "DIFF"),7}{(identical ? "bits" : "-"),6}{jointUs,10:F2}{splitUs,10:F2}  {(ok ? "ok" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"{label}  error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} not passing, {skipped} circuits not solvable by LiveSPICE.");
        return failed == 0 ? 0 : 1;
    }
}
