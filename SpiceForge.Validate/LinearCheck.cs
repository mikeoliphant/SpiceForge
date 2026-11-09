using System.Diagnostics;
using System.Text;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Compares the linear interpreter with the reference captures, for circuits with no nonlinear terms.</summary>
internal static class LinearCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const double ThresholdDb = -80;

    private static readonly (string Name, double Amplitude)[] Stimuli = { ("harmonics-0.5", 0.5), ("harmonics-5", 5.0) };

    public static int Run(string testsDir, string repo, string? filter)
    {
        var files = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"{"Circuit",-46}{"stimulus",-15}{"error dB",10}{"cond",10}{"us/sample",11}  status");
        int passed = 0, failed = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            var circuit = Schematic.Load(file, new NullLog()).Build();
            Expression h = (Real)1 / (SampleRate * Oversample);

            ExtractedSystem system;
            try
            {
                system = Extractor.Extract(circuit.Analyze(), h);
            }
            catch (Exception)
            {
                continue;
            }
            if (system.Nonlinear.Length != 0)
                continue;

            Expression input = circuit.Components.Where(i => i is Input)
                .Select(i => Component.DependentVariable(i.Name, Component.t))
                .DefaultIfEmpty("V[t]")
                .Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

            foreach (var stimulus in Stimuli)
            {
                string label = $"{name,-46}{stimulus.Name,-15}";
                try
                {
                    double[][] reference = ReadOutputs(Path.Combine(repo, "Reference", "data", name + "." + stimulus.Name + ".ref"), outputs);
                    int samples = reference[0].Length;
                    var inputBuffer = new double[samples];
                    for (int n = 0; n < samples; n++)
                        inputBuffer[n] = Harmonics(n / (double)SampleRate, stimulus.Amplitude, 82, 2);

                    var interpreter = new LinearInterpreter(system, input, outputs, h, Oversample);
                    double[][] actual = outputs.Select(_ => new double[samples]).ToArray();
                    var watch = Stopwatch.StartNew();
                    interpreter.Run(inputBuffer, actual);
                    double microseconds = watch.Elapsed.TotalMilliseconds * 1000 / samples;

                    double errorDb = WorstErrorDb(reference, actual);
                    bool ok = errorDb <= ThresholdDb;
                    if (ok) passed++; else failed++;
                    Console.WriteLine($"{label}{errorDb,10:F1}{interpreter.ConditionNumber,10:E1}{microseconds,11:F2}  {(ok ? "ok" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"{label}error: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} not passing (threshold {ThresholdDb} dB).");
        return failed == 0 ? 0 : 1;
    }

    // The worst error across all nodes, relative to that node's peak. Nodes that sit near zero are
    // measured against the circuit's peak instead, so roundoff on them isn't reported as a large error.
    private static double WorstErrorDb(double[][] reference, double[][] actual)
    {
        double circuitPeak = reference.Max(o => o.Max(Math.Abs));
        double floor = Math.Max(circuitPeak * 1e-6, 1e-12);
        double worst = 0;
        for (int o = 0; o < reference.Length; o++)
        {
            double peak = Math.Max(reference[o].Max(Math.Abs), floor);
            for (int n = 0; n < reference[o].Length; n++)
            {
                double difference = Math.Abs(reference[o][n] - actual[o][n]);
                if (double.IsNaN(difference))
                    return double.PositiveInfinity;
                worst = Math.Max(worst, difference / peak);
            }
        }
        return worst == 0 ? double.NegativeInfinity : 20 * Math.Log10(worst);
    }

    private static double[][] ReadOutputs(string path, Expression[] outputs)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (reader.ReadInt32() != 1)
            throw new InvalidDataException("Unknown reference format.");
        int samples = reader.ReadInt32();
        int count = reader.ReadInt32();
        for (int o = 0; o < count; o++)
            if (reader.ReadString() != outputs[o].ToString())
                throw new InvalidDataException("Reference outputs don't match the circuit.");

        var data = new double[count][];
        for (int o = 0; o < count; o++)
        {
            data[o] = new double[samples];
            for (int n = 0; n < samples; n++)
                data[o][n] = reader.ReadDouble();
        }
        return data;
    }

    private static double Harmonics(double t, double amplitude, double f0, int count)
    {
        double s = 0;
        for (int i = 1; i <= count; ++i)
            s += Math.Sin(t * f0 * 2 * 3.1415 * i) / count;
        return amplitude * s;
    }
}
