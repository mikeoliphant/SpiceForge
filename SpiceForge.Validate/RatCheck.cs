using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Compares the interpreter with stock Simulation on one circuit at several pot settings, re-solving at each.
/// The control reruns stock Simulation with the input changed by 1 part in 1e15, which shows how far
/// LiveSPICE's own output moves from a roundoff-sized change.
/// </summary>
internal static class RatCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const double ThresholdDb = -80;
    private static readonly double[] InputPerturbations = { 2e-16, 1e-15, 1e-14, 1e-13, 1e-12 };

    private static readonly (string Name, double[]? Wipes)[] Settings =
    {
        ("schematic", null),
        ("all 0.1", new[] { 0.1 }),
        ("all 0.9", new[] { 0.9 }),
        ("0.2/0.8/0.5", new[] { 0.2, 0.8, 0.5 }),
        ("0.8/0.2/0.5", new[] { 0.8, 0.2, 0.5 }),
        ("0.5/0.5/0.1", new[] { 0.5, 0.5, 0.1 }),
    };

    private static readonly double[] Amplitudes = { 0.5, 5.0 };

    public static int Run(string testsDir, string filter)
    {
        string file = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx"))
            .First(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"{"setting",-14}{"input",6}{"error dB",10}{"ctl 2e-16",11}{"1e-15",9}{"1e-14",9}{"1e-13",9}{"1e-12",9}{"-120dB at",11}{"us/sample",11}{"stock us",10}  status");
        int failed = 0;
        foreach (var setting in Settings)
        {
            foreach (double amplitude in Amplitudes)
            {
                string label = $"{setting.Name,-14}{amplitude,6:F1}";
                try
                {
                    var circuit = Schematic.Load(file, new NullLog()).Build();
                    var pots = circuit.Components.OfType<IPotControl>().ToList();
                    if (setting.Wipes != null)
                        for (int i = 0; i < pots.Count; i++)
                            pots[i].PotValue = setting.Wipes[i % setting.Wipes.Length];

                    Expression h = (Real)1 / (SampleRate * Oversample);
                    Analysis analysis = circuit.Analyze();
                    ExtractedSystem system = Extractor.Extract(analysis, h);
                    TransientSolution solution = TransientSolution.Solve(analysis, h);

                    Expression input = circuit.Components.Where(i => i is Input)
                        .Select(i => Component.DependentVariable(i.Name, Component.t))
                        .DefaultIfEmpty("V[t]")
                        .Single();
                    Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

                    var inputBuffer = new double[Samples];
                    for (int n = 0; n < Samples; n++)
                        inputBuffer[n] = ReferenceCheck.Harmonics(n / (double)SampleRate, amplitude, 82, 2);

                    double[][] stock = NewBuffers(outputs.Length);
                    var watch = Stopwatch.StartNew();
                    Stock(solution, input, outputs).Run(inputBuffer, stock);
                    double stockMicroseconds = watch.Elapsed.TotalMilliseconds * 1000 / Samples;

                    var controlDb = new List<double>();
                    foreach (double perturbation in InputPerturbations)
                    {
                        double[][] control = NewBuffers(outputs.Length);
                        Stock(solution, input, outputs).Run(TraceCheck.WithNoise(inputBuffer, perturbation), control);
                        controlDb.Add(ReferenceCheck.WorstErrorDb(stock, control));
                    }

                    var interpreter = new CircuitInterpreter(system, input, outputs, h, Oversample, IterationCap, solution);
                    double[][] actual = NewBuffers(outputs.Length);
                    watch.Restart();
                    interpreter.Run(inputBuffer, actual);
                    double microseconds = watch.Elapsed.TotalMilliseconds * 1000 / Samples;

                    double errorDb = ReferenceCheck.WorstErrorDb(stock, actual);
                    int firstLarge = FirstExceeding(stock, actual, -120);
                    bool ok = errorDb <= ThresholdDb;
                    if (!ok)
                        failed++;
                    Console.WriteLine($"{label}{errorDb,10:F1}{controlDb[0],11:F1}{controlDb[1],9:F1}{controlDb[2],9:F1}{controlDb[3],9:F1}{controlDb[4],9:F1}{firstLarge,11}{microseconds,11:F2}{stockMicroseconds,10:F2}  {(ok ? "ok" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"{label}  error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"{failed} not passing (threshold {ThresholdDb} dB).");
        return failed == 0 ? 0 : 1;
    }

    private static Simulation Stock(TransientSolution solution, Expression input, Expression[] outputs) =>
        new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { input }, Output = outputs };

    private static double[][] NewBuffers(int outputs) =>
        Enumerable.Range(0, outputs).Select(_ => new double[Samples]).ToArray();

    // First sample where any node's error exceeds the given level relative to that node's peak, or -1.
    private static int FirstExceeding(double[][] reference, double[][] actual, double db)
    {
        double circuitPeak = reference.Max(o => o.Max(Math.Abs));
        double floor = Math.Max(circuitPeak * 1e-6, 1e-12);
        double limit = Math.Pow(10, db / 20);
        for (int n = 0; n < Samples; n++)
            for (int o = 0; o < reference.Length; o++)
                if (Math.Abs(reference[o][n] - actual[o][n]) / Math.Max(reference[o].Max(Math.Abs), floor) > limit)
                    return n;
        return -1;
    }
}
