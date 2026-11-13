using System.Diagnostics;
using System.Globalization;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Times stock LiveSPICE's Simulation against SpiceForge's compiled engine, with its pots and switches fixed (built in,
/// as LiveSPICE has them) and live (runtime parameters), on the example and unstable circuits. Engines only, with no
/// application layer on either side, at LiveSPICE's settings: 8x oversampling, an iteration cap of 8, linear
/// resampling, one output (the speaker's), as LiveSPICE's own benchmark does. Prints a Markdown table.
/// </summary>
internal static class BenchCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 24000;
    private const int TimingPasses = 5;
    private const double Amplitude = 0.5;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Speed only: accuracy against LiveSPICE is the livespice and jit checks' job.
        var input = new double[Samples];
        for (int n = 0; n < Samples; n++)
            input[n] = Signals.Harmonics(n / (double)SampleRate, Amplitude, 82, 2);

        Console.WriteLine("| Circuit | Pots | Switches | Stock µs | Fixed µs | Live µs | Fixed vs stock | Live vs stock | Live cost |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        var fixedSpeeds = new List<double>();
        var liveSpeeds = new List<double>();
        var liveCosts = new List<double>();
        int errors = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            try
            {
                Expression h = (Real)1 / (SampleRate * Oversample);
                LoadedCircuit fixedCircuit, live;
                try
                {
                    fixedCircuit = CircuitLoader.Load(file, h, dynamicPots: false, dynamicSwitches: false);
                    live = CircuitLoader.Load(file, h, dynamicPots: true, dynamicSwitches: true);
                }
                catch (Exception ex) when (ex.Message.Contains("Failed to eliminate differentials"))
                {
                    // LiveSPICE's own solve fails (the Triode stage), and both engines start from it.
                    Console.WriteLine($"| {name} | | | | | | | | LiveSPICE can't solve it |");
                    continue;
                }

                Expression inputSymbol = fixedCircuit.Circuit.Components.Where(i => i is Input)
                    .Select(i => Component.DependentVariable(i.Name, Component.t))
                    .DefaultIfEmpty("V[t]")
                    .Single();
                Expression[] output = { JitCheck.BenchmarkOutput(fixedCircuit.Circuit, fixedCircuit.Circuit.Nodes.Select(i => i.V).ToArray()) };

                var stock = new Simulation(fixedCircuit.Solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { inputSymbol }, Output = output };
                var ours = new CompiledCircuit(fixedCircuit.System, inputSymbol, output, h, Oversample, IterationCap, fixedCircuit.Solution) { RecordIterations = false };
                var ourLive = new CompiledCircuit(live.System, inputSymbol, output, h, Oversample, IterationCap, live.Solution) { RecordIterations = false };

                // The first run, from each engine's starting state, checks they agree before anything is timed.
                double[][] stockOut = { new double[Samples] }, fixedOut = { new double[Samples] }, liveOut = { new double[Samples] };
                bool stockFinite = true;
                try
                {
                    stock.Run(input, stockOut);
                    stockFinite = stockOut[0].All(double.IsFinite);
                }
                catch (Exception)
                {
                    stockFinite = false;
                }
                ours.Run(input, fixedOut);
                ourLive.Run(input, liveOut);
                bool oursFinite = fixedOut[0].All(double.IsFinite) && liveOut[0].All(double.IsFinite);

                double fixedUs = Measure(() => ours.Run(input, fixedOut));
                double liveUs = Measure(() => ourLive.Run(input, liveOut));
                double liveCost = liveUs / fixedUs - 1;
                string pots = live.Pots.Count.ToString(CultureInfo.InvariantCulture);
                string switches = live.Switches.Select(s => s.Switch).Distinct().Count().ToString(CultureInfo.InvariantCulture);

                if (!oursFinite)
                    errors++;
                liveCosts.Add(liveCost);
                if (stockFinite)
                {
                    double stockUs = Measure(() => stock.Run(input, stockOut));
                    double fixedSpeed = stockUs / fixedUs, liveSpeed = stockUs / liveUs;
                    fixedSpeeds.Add(fixedSpeed);
                    liveSpeeds.Add(liveSpeed);
                    Console.WriteLine($"| {name} | {pots} | {switches} | {F(stockUs)} | {F(fixedUs)} | {F(liveUs)} | {fixedSpeed:F2}× | {liveSpeed:F2}× | {Percent(liveCost)} |");
                }
                else
                {
                    Console.WriteLine($"| {name} | {pots} | {switches} | unstable | {F(fixedUs)} | {F(liveUs)} | | | {Percent(liveCost)} |");
                }
                if (!oursFinite)
                    Console.WriteLine($"<!-- {name}: SpiceForge output not finite -->");
            }
            catch (Exception ex)
            {
                errors++;
                string message = ex.Message.Length > 100 ? ex.Message.Substring(0, 100) : ex.Message;
                Console.WriteLine($"| {name} | | | | | | | | error: {ex.GetType().Name}: {message.Replace('|', '/')} |");
            }
        }

        Console.WriteLine();
        if (fixedSpeeds.Count > 0)
            Console.WriteLine($"Speed vs stock, median over {fixedSpeeds.Count} circuits: fixed {Median(fixedSpeeds):F2}×, live {Median(liveSpeeds):F2}×. "
                + $"Live cost over fixed, median {Percent(Median(liveCosts))}, range {Percent(liveCosts.Min())} to {Percent(liveCosts.Max())}.");
        return errors == 0 ? 0 : 1;
    }

    private static string F(double us) => us.ToString(us < 10 ? "F2" : "F1", CultureInfo.InvariantCulture);

    private static string Percent(double fraction) => (fraction * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    // Best of several passes, in microseconds per input sample. The engine runs on from where the last pass left it.
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
