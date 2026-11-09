using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Measures several engines against a tightly converged reference: the same equations with a far stricter
/// stopping rule and more iterations, rebuilt exactly. Whatever any engine differs from it by is error from its
/// own stopping tolerance (or roundoff), amplified by however sensitive the circuit is.
/// </summary>
internal static class AccuracyCheck
{
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int TightCap = 100;
    private const double TightRelative = 1e-10;
    private const double TightAbsolute = 1e-12;
    private const int BlockSize = 4800;

    public static int Run(string schematic, string wav, double seconds)
    {
        double[] input = ReadMono(wav, seconds, out int rate);
        var circuit = Schematic.Load(schematic, new NullLog()).Build();
        Expression inputSymbol = Component.DependentVariable(circuit.Components.OfType<Input>().Single().Name, Component.t);
        var speakers = circuit.Components.OfType<Speaker>().ToList();
        Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
        double[] scales = speakers.Select(s => 1 / (double)s.V0dBFS).ToArray();
        Expression output = speakers.Select(s => s.Out).Aggregate((a, b) => a + b);

        Expression h = (Real)1 / (rate * Oversample);
        Analysis analysis = circuit.Analyze();
        TransientSolution solution = TransientSolution.Solve(analysis, h);
        ExtractedSystem system = Extractor.Extract(analysis, h);

        double[] Stock(double[] samples)
        {
            var result = new double[samples.Length];
            var simulation = new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { inputSymbol }, Output = new[] { output } };
            for (int start = 0; start < samples.Length; start += BlockSize)
            {
                int count = Math.Min(BlockSize, samples.Length - start);
                var block = new double[count];
                simulation.Run(samples.AsSpan(start, count).ToArray(), block);
                Array.Copy(block, 0, result, start, count);
            }
            return result;
        }

        (double[] Output, int Capped) Ours(int cap, bool exact, double relative, double absolute)
        {
            var engine = new CircuitInterpreter(system, inputSymbol, nodes, h, Oversample, cap, solution, matchLiveSpice: !exact)
            {
                RelativeTolerance = relative,
                AbsoluteTolerance = absolute,
            };
            var result = new double[input.Length];
            for (int start = 0; start < input.Length; start += BlockSize)
            {
                int count = Math.Min(BlockSize, input.Length - start);
                double[][] buffers = nodes.Select(_ => new double[count]).ToArray();
                engine.Run(input.AsSpan(start, count).ToArray(), buffers);
                for (int n = 0; n < count; n++)
                {
                    double sum = 0;
                    for (int s = 0; s < scales.Length; s++)
                        sum += (buffers[2 * s][n] - buffers[2 * s + 1][n]) * scales[s];
                    result[start + n] = sum;
                }
            }
            return (result, engine.IterationCounts.Count(b => (b & 0x80) != 0));
        }

        var tight = Ours(TightCap, true, TightRelative, TightAbsolute);
        var tightMatched = Ours(TightCap, false, TightRelative, TightAbsolute);
        var engines = new List<(string Name, double[] Output, string Note)>
        {
            ("stock LiveSPICE", Stock(input), ""),
            ("stock + 1e-14 input noise", Stock(TraceCheck.WithNoise(input, 1e-14)), "sensitivity"),
            ("matched (LiveSPICE-faithful)", Ours(IterationCap, false, 1e-4, 1e-6).Output, ""),
            ("recording (exact rebuild)", Ours(IterationCap, true, 1e-4, 1e-6).Output, ""),
            ("tight, LiveSPICE-faithful rebuild", tightMatched.Output, "checks the reference"),
        };

        Console.WriteLine($"{Path.GetFileNameWithoutExtension(schematic)}, {input.Length / (double)rate:F1} s. Reference: tolerance {TightRelative:E0} relative + {TightAbsolute:E0}, cap {TightCap}, exact rebuild; {tight.Capped} capped steps.");
        Console.WriteLine($"{"engine",-36}{"vs reference dB",17}{"vs stock dB",13}{"-60 dB at s",13}  note");
        double[] stock = engines[0].Output;
        foreach (var engine in engines)
        {
            double vsReference = WorstDb(tight.Output, engine.Output, out int first);
            double vsStock = WorstDb(stock, engine.Output, out _);
            string at = first < 0 ? "never" : (first / (double)rate).ToString("F3");
            Console.WriteLine($"{engine.Name,-36}{vsReference,17:F1}{vsStock,13:F1}{at,13}  {engine.Note}");
        }
        return 0;
    }

    // Worst error relative to the reference's peak, and the first sample where it passes -60 dB.
    private static double WorstDb(double[] reference, double[] actual, out int firstAbove60)
    {
        double peak = reference.Max(Math.Abs), worst = 0, limit = peak * 1e-3;
        firstAbove60 = -1;
        for (int n = 0; n < reference.Length; n++)
        {
            double error = Math.Abs(reference[n] - actual[n]);
            if (double.IsNaN(error))
                return double.PositiveInfinity;
            if (firstAbove60 < 0 && error > limit)
                firstAbove60 = n;
            worst = Math.Max(worst, error);
        }
        return worst == 0 ? double.NegativeInfinity : 20 * Math.Log10(worst / peak);
    }

    private static double[] ReadMono(string path, double seconds, out int rate)
    {
        using var reader = new WaveFileReader(path);
        rate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        ISampleProvider provider = reader.ToSampleProvider();
        int wanted = (int)(seconds * rate);
        var samples = new List<double>(wanted);
        var buffer = new float[BlockSize * channels];
        int read;
        while (samples.Count < wanted && (read = provider.Read(buffer.AsSpan())) > 0)
            for (int i = 0; i < read && samples.Count < wanted; i += channels)
                samples.Add(buffer[i]);
        return samples.ToArray();
    }
}
