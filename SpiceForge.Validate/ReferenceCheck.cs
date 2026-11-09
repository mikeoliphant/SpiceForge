using System.Diagnostics;
using System.Text;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Compares the interpreter's output and Newton iteration counts with the reference captures.</summary>
internal static class ReferenceCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const double ThresholdDb = -80;

    private static readonly (string Name, double Amplitude)[] Stimuli = { ("harmonics-0.5", 0.5), ("harmonics-5", 5.0) };

    private sealed record Reference(double[][] Outputs, byte[][] Steps);

    public static int Run(string testsDir, string repo, bool nonlinear, string? filter)
    {
        var files = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"{"Circuit",-46}{"stimulus",-15}{"error dB",10}{"cond",10}{"k",4}{"LSsys",6}{"iters",8}{"LS",8}{"iterMatch",11}{"us/samp",9}  status");
        int passed = 0, failed = 0;
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
                continue;
            }
            if ((system.Nonlinear.Length != 0) != nonlinear)
                continue;

            Expression input = circuit.Components.Where(i => i is Input)
                .Select(i => Component.DependentVariable(i.Name, Component.t))
                .DefaultIfEmpty("V[t]")
                .Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

            foreach (var stimulus in Stimuli)
            {
                string label = $"{Clip(name, 45),-46}{stimulus.Name,-15}";
                try
                {
                    Reference reference = Read(Path.Combine(repo, "Reference", "data", name + "." + stimulus.Name + ".ref"), outputs);
                    int samples = reference.Outputs[0].Length;
                    var inputBuffer = new double[samples];
                    for (int n = 0; n < samples; n++)
                        inputBuffer[n] = Harmonics(n / (double)SampleRate, stimulus.Amplitude, 82, 2);

                    var interpreter = new CircuitInterpreter(system, input, outputs, h, Oversample, IterationCap, solution);
                    double[][] actual = outputs.Select(_ => new double[samples]).ToArray();
                    var watch = Stopwatch.StartNew();
                    interpreter.Run(inputBuffer, actual);
                    double microseconds = watch.Elapsed.TotalMilliseconds * 1000 / samples;

                    if (Dump)
                        DumpDetails(system, solution, outputs, reference, actual, interpreter);
                    if (Dump)
                        DumpUnknowns(system, solution, input, h, inputBuffer);

                    double errorDb = WorstErrorDb(reference.Outputs, actual);
                    long ours = interpreter.IterationCounts.Sum(b => (long)(b & 0x7F));
                    long theirs = reference.Steps.Sum(s => s.Sum(b => (long)(b & 0x7F)));
                    string match = reference.Steps.Length == 1 && nonlinear
                        ? $"{MatchPercent(interpreter.IterationCounts, reference.Steps[0]):F2}%"
                        : "-";

                    bool ok = errorDb <= ThresholdDb;
                    if (ok) passed++; else failed++;
                    Console.WriteLine($"{label}{errorDb,10:F1}{interpreter.ConditionNumber,10:E1}{system.Ports,4}{reference.Steps.Length,6}{ours,8}{theirs,8}{match,11}{microseconds,9:F2}  {(ok ? "ok" : "FAIL")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"{label}error: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
                }
            }
        }

        Console.WriteLine($"{passed} passed, {failed} not passing (threshold {ThresholdDb} dB).");
        return failed == 0 ? 0 : 1;
    }

    public static bool Dump { get; set; }

    private static void DumpUnknowns(ExtractedSystem system, TransientSolution solution, Expression input, Expression h, double[] inputBuffer)
    {
        int samples = inputBuffer.Length;
        double[][] stock = system.Unknowns.Select(_ => new double[samples]).ToArray();
        new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { input }, Output = system.Unknowns }.Run(inputBuffer, stock);

        double[][] ours = system.Unknowns.Select(_ => new double[samples]).ToArray();
        new CircuitInterpreter(system, input, system.Unknowns, h, Oversample, IterationCap, solution).Run(inputBuffer, ours);

        Console.WriteLine("All unknowns, largest error first:");
        var rows = Enumerable.Range(0, system.Unknowns.Length).Select(j =>
        {
            int first = -1, at = 0;
            double worst = 0, peak = stock[j].Max(Math.Abs);
            for (int n = 0; n < samples; n++)
            {
                double difference = Math.Abs(stock[j][n] - ours[j][n]);
                if (first < 0 && difference > 1e-10 * Math.Max(peak, 1e-9))
                    first = n;
                if (difference > worst) { worst = difference; at = n; }
            }
            return (j, first, at, worst, peak);
        }).OrderBy(r => r.first < 0 ? int.MaxValue : r.first).ThenByDescending(r => r.worst).Take(14);
        foreach (var r in rows)
            Console.WriteLine($"  {system.Unknowns[r.j],-22} peak {r.peak,12:G5} worst {r.worst,12:G5} at {r.at,5} first {r.first,5}  sample0 LS {stock[r.j][0],14:G8} ours {ours[r.j][0],14:G8}");
    }

    // ComputerAlgebra's string visitor throws on some products, so fall back to printing their factors.
    private static string Show(Expression e)
    {
        try { return e.ToString(); }
        catch (Exception)
        {
            var factors = Product.TermsOf(e).ToList();
            return factors.Count > 1 ? "(" + string.Join(" * ", factors.Select(Show)) + ")" : "<unprintable>";
        }
    }

    private static void DumpDetails(ExtractedSystem system, TransientSolution solution, Expression[] outputs, Reference reference, double[][] actual, CircuitInterpreter interpreter)
    {
        Console.WriteLine("Nonlinear terms:");
        foreach (Expression term in system.Nonlinear)
            Console.WriteLine("  " + Show(term));
        Console.WriteLine("Ports:");
        for (int r = 0; r < system.Ports; r++)
            Console.WriteLine("  " + string.Join(" + ", Enumerable.Range(0, system.Unknowns.Length)
                .Where(j => system.Q[r, j] != 0).Select(j => $"{system.Q[r, j]:G6}*{system.Unknowns[j]}")));
        foreach (SolutionSet set in solution.Solutions)
        {
            if (set is LinearSolutions linear)
            {
                Console.WriteLine("Linear solutions:");
                foreach (Arrow arrow in linear.Solutions)
                    Console.WriteLine("  " + Clip(Show(arrow.Left) + " -> " + Show(arrow.Right), 420));
            }
            else if (set is NewtonIteration newton)
            {
                Console.WriteLine("Newton unknowns: " + string.Join(", ", newton.Unknowns));
                Console.WriteLine("Known deltas:");
                foreach (Arrow arrow in newton.KnownDeltas)
                    Console.WriteLine("  " + Clip(Show(arrow.Left) + " -> " + Show(arrow.Right), 420));
            }
        }

        for (int o = 0; o < outputs.Length; o++)
        {
            double peak = reference.Outputs[o].Max(Math.Abs);
            double worst = 0;
            int first = -1, at = 0;
            for (int n = 0; n < reference.Outputs[o].Length; n++)
            {
                double difference = Math.Abs(reference.Outputs[o][n] - actual[o][n]);
                if (first < 0 && difference > 1e-9 * Math.Max(peak, 1e-6))
                    first = n;
                if (difference > worst)
                {
                    worst = difference;
                    at = n;
                }
            }
            Console.WriteLine($"  {outputs[o],-24} peak {peak,12:G5}  worst {worst,12:G5} at {at,5}  first differs at {first,5}  ref {reference.Outputs[o][at],12:G6} ours {actual[o][at],12:G6}");
        }

        byte[] steps = reference.Steps.Length > 0 ? reference.Steps[0] : Array.Empty<byte>();
        for (int i = 0; i < Math.Min(steps.Length, interpreter.IterationCounts.Count); i++)
        {
            if (steps[i] != interpreter.IterationCounts[i])
            {
                Console.WriteLine($"  first iteration difference at step {i} (sample {i / Oversample}): LS {steps[i]:X2} ours {interpreter.IterationCounts[i]:X2}");
                break;
            }
        }
    }

    private static double MatchPercent(List<byte> ours, byte[] theirs)
    {
        if (ours.Count != theirs.Length)
            return 0;
        int same = 0;
        for (int i = 0; i < theirs.Length; i++)
            if (ours[i] == theirs[i])
                same++;
        return 100.0 * same / theirs.Length;
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

    private static Reference Read(string path, Expression[] outputs)
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

        var steps = new byte[reader.ReadInt32()][];
        for (int s = 0; s < steps.Length; s++)
            steps[s] = reader.ReadBytes(reader.ReadInt32());
        return new Reference(data, steps);
    }

    private static string Clip(string s, int length) => s.Length <= length ? s : s.Substring(0, length);

    private static double Harmonics(double t, double amplitude, double f0, int count)
    {
        double s = 0;
        for (int i = 1; i <= count; ++i)
            s += Math.Sin(t * f0 * 2 * 3.1415 * i) / count;
        return amplitude * s;
    }
}
