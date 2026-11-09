using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using SpiceForge.ReferenceCapture;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Steps a WAV file through the compiled engine and an instrumented copy of stock Simulation, one internal
/// timestep at a time, and reports the first step in a window where they split.
/// </summary>
internal static class WavTrace
{
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Block = 48000;
    public static double Tolerance = 1e-6;
    public static bool WatchAll;

    public static int Run(string schematic, string wav, double startSeconds, double lengthSeconds)
    {
        using var reader = new WaveFileReader(wav);
        int rate = reader.WaveFormat.SampleRate;
        int channels = reader.WaveFormat.Channels;
        ISampleProvider provider = reader.ToSampleProvider();
        int samples = (int)((startSeconds + lengthSeconds) * rate);
        var raw = new float[samples * channels];
        int got = 0, read;
        while (got < raw.Length && (read = provider.Read(raw.AsSpan(got))) > 0)
            got += read;

        // The interpolated input Simulation would generate internally.
        int steps = (got / channels) * Oversample;
        var interpolated = new double[steps];
        double u = 0;
        for (int n = 0, s = 0; n < got / channels; n++)
        {
            double delta = (raw[n * channels] - u) * (1.0 / Oversample);
            for (int ov = 0; ov < Oversample; ov++)
            {
                u += delta;
                interpolated[s++] = u;
            }
        }

        var circuit = Schematic.Load(schematic, new NullLog()).Build();
        Expression input = Component.DependentVariable(circuit.Components.OfType<Input>().Single().Name, Component.t);
        Expression h = (Real)1 / (rate * Oversample);
        Analysis analysis = circuit.Analyze();
        TransientSolution solution = TransientSolution.Solve(analysis, h);
        ExtractedSystem system = Extractor.Extract(analysis, h);
        Expression[] watched = WatchAll ? system.Unknowns : solution.Solutions.OfType<NewtonIteration>().SelectMany(s => s.Unknowns)
            .Where(x => system.Unknowns.Contains(x)).ToArray();

        var recorder = new NewtonRecorder();
        var stock = new InstrumentedSimulation(solution, recorder) { Oversample = 1, Iterations = IterationCap, Input = new[] { input }, Output = watched };
        var ours = new CompiledCircuit(system, input, watched, h, 1, IterationCap, solution);

        int windowStart = (int)(startSeconds * rate) * Oversample;
        var stockWindow = watched.Select(_ => new double[steps - windowStart]).ToArray();
        var ourWindow = watched.Select(_ => new double[steps - windowStart]).ToArray();
        for (int start = 0; start < steps; start += Block)
        {
            int count = Math.Min(Block, steps - start);
            double[] chunk = interpolated.AsSpan(start, count).ToArray();
            double[][] a = watched.Select(_ => new double[count]).ToArray();
            double[][] b = watched.Select(_ => new double[count]).ToArray();
            stock.Run(chunk, a);
            ours.Run(chunk, b);
            for (int j = 0; j < watched.Length; j++)
                for (int n = 0; n < count; n++)
                    if (start + n >= windowStart)
                    {
                        stockWindow[j][start + n - windowStart] = a[j][n];
                        ourWindow[j][start + n - windowStart] = b[j][n];
                    }
        }

        // Newton systems with no equations aren't in our counts; take the largest stock system.
        List<byte> stockCounts = recorder.Steps.OrderByDescending(s => s.Sum(x => x & 0x7F)).First();
        int length = steps - windowStart;
        int first = -1;
        for (int n = 0; n < length && first < 0; n++)
            for (int j = 0; j < watched.Length; j++)
                if (Math.Abs(stockWindow[j][n] - ourWindow[j][n]) > Tolerance * (1 + Math.Abs(stockWindow[j][n])))
                {
                    first = n;
                    break;
                }

        int matching = 0, compared = 0;
        for (int n = 0; n < length; n++)
        {
            int s = windowStart + n;
            if (s < stockCounts.Count && s < ours.IterationCounts.Count)
            {
                compared++;
                if (stockCounts[s] == ours.IterationCounts[s])
                    matching++;
            }
        }
        Console.WriteLine($"Window {startSeconds:F3}-{startSeconds + lengthSeconds:F3} s: iteration counts match on {matching} of {compared} steps.");
        if (first < 0)
        {
            Console.WriteLine("No difference above 1e-6 in the window.");
            return 0;
        }

        int at = windowStart + first;
        Console.WriteLine($"First difference at step {at} ({at / (double)(rate * Oversample):F6} s).");
        Console.WriteLine($"{"step",10}{"stock it",10}{"ours it",9}  worst unknown: stock / ours");
        for (int n = Math.Max(0, first - 6); n < Math.Min(length, first + 10); n++)
        {
            int s = windowStart + n;
            int worst = Enumerable.Range(0, watched.Length).OrderByDescending(j => Math.Abs(stockWindow[j][n] - ourWindow[j][n])).First();
            Console.WriteLine($"{s,10}{stockCounts[s],10:X2}{ours.IterationCounts[s],9:X2}  {watched[worst]}: {stockWindow[worst][n]:G10} / {ourWindow[worst][n]:G10}");
        }
        Console.WriteLine("Watched unknowns at the first difference, stock / ours:");
        for (int j = 0; j < watched.Length; j++)
            Console.WriteLine($"  {watched[j],-14} {stockWindow[j][first],22:G15} {ourWindow[j][first],22:G15}");
        return 0;
    }
}
