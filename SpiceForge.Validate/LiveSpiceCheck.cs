using System.Diagnostics;
using Circuit;
using ComputerAlgebra;
using SpiceForge;
using SpiceForge.ReferenceCapture;
using Util;
using Expression = ComputerAlgebra.Expression;

// LiveSPICE marks its on/off Switch obsolete (single-pole multi-throw switches replace it), but older schematics
// still contain it, so it's handled here deliberately.
#pragma warning disable CS0612

/// <summary>
/// Checks our engine against an independent reference: LiveSPICE's own solver (the instrumented copy of Simulation),
/// tightly converged and with the fix from LiveSPICE pull request 284. LiveSPICE builds and solves the circuit through
/// its own symbolic path, so this is the one check that doesn't share our extraction, discretization or Newton code.
/// Ours runs with dynamic pots, set to each setting, and is tightly converged too, so any difference beyond roundoff
/// is a difference in the code, not in either solver's stopping error.
/// <para>
/// The control reruns the LiveSPICE reference with roundoff-sized noise on its input. A run whose error misses the
/// threshold counts as sensitive, rather than failed, when the reference itself moves that far under the noise.
/// </para>
/// </summary>
internal static class LiveSpiceCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int Samples = 4800;
    private const int TightCap = 100;
    private const double TightRelative = 1e-10;
    private const double TightAbsolute = 1e-12;

    /// <summary>Worst error, relative to each node's peak, that passes.</summary>
    public static double ThresholdDb = -100;

    /// <summary>Circuits to run at once, each in its own process (--jobs N).</summary>
    public static int Jobs = 1;

    // Every pot at the same position; null keeps the schematic's.
    private static readonly (string Name, double? Wipe)[] Settings = { ("schematic", null), ("all 0", 0), ("all 1", 1), ("all 0.3", 0.3) };
    private static readonly double[] Amplitudes = { 0.5, 5.0 };

    public static int Run(string testsDir, string? filter, string? exactFile = null)
    {
        var files = exactFile != null ? new List<string> { exactFile } : new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (Jobs > 1 && files.Count > 1)
        {
            var watch = Stopwatch.StartNew();
            int total = ParallelRunner.Run(files.Select(f => new[] { "livespice", "--file", f }).ToList(), Jobs);
            Console.WriteLine($"{total} problem(s).");
            Console.WriteLine($"{files.Count} circuits in {watch.Elapsed.TotalSeconds:F1} s with {Jobs} jobs.");
            return total == 0 ? 0 : 1;
        }

        int problems = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            Console.WriteLine($"=== {name}");
            try
            {
                problems += Check(file);
            }
            catch (Exception ex)
            {
                problems++;
                Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private static int Check(string file)
    {
        Expression h = (Real)1 / (SampleRate * Oversample);
        bool hasPots = PotsOf(Schematic.Load(file, new NullLog()).Build()).Any();
        int problems = 0;
        Console.WriteLine($"  {"setting",-12}{"in",5}{"ours vs LiveSPICE dB",22}{"control dB",12}{"capped ours/LS",16}  status");
        foreach (var setting in hasPots ? Settings : Settings.Take(1))
        {
            // LiveSPICE's side, built only from LiveSPICE: load, set the pots on its own components, solve.
            Circuit.Circuit theirs = Schematic.Load(file, new NullLog()).Build();
            if (setting.Wipe is double wipe)
                foreach (IPotControl pot in PotsOf(theirs))
                    pot.PotValue = wipe;
            TransientSolution solution = TransientSolution.Solve(theirs.Analyze(), h);
            Expression input = theirs.Components.OfType<Input>().Select(i => Component.DependentVariable(i.Name, Component.t)).DefaultIfEmpty("V[t]").Single();
            // Only nodes LiveSPICE's own equations determine: one that only an open switch contact touches appears in no
            // equation once open contacts are infinite, so it is undetermined, and LiveSPICE reports 0 for it where ours
            // follows the other side of the contact.
            Equal[] equations = theirs.Analyze().Equations.Substitute(SinglePoleSwitch.ExcludeOpen).OfType<Equal>().ToArray();
            Expression[] candidates = theirs.Nodes.Select(n => n.V).Where(v => equations.Any(e => e.DependsOn(v))).ToArray();

            // Ours, with dynamic pots loaded at the same positions so both start from the same DC point.
            Dictionary<string, double>? positions = null;
            if (setting.Wipe is double w)
                positions = CircuitLoader.PotNames(Schematic.Load(file, new NullLog()).Build()).ToDictionary(n => n, _ => w);
            LoadedCircuit ours = CircuitLoader.Load(file, h, dynamicPots: true, potPositions: positions);
            // And not nodes that only switch contacts touch (the Bassman's unused Bright throw, the Blues Breaker's
            // Bypass): they have no role in the circuit, LiveSPICE drops them when they float, and ours keeps them
            // weakly tied (or drops them when nothing else connects to them).
            var switchOnly = theirs.Nodes.Where(n => theirs.Components
                    .Where(c => c is not NamedWire && c.Terminals.Any(t => t.ConnectedTo == n))
                    .All(c => c is SinglePoleSwitch or Circuit.Switch))
                .Select(n => n.V).ToHashSet();
            Expression[] nodes = candidates.Where(v => !switchOnly.Contains(v)).ToArray();

            foreach (double amplitude in Amplitudes)
            {
                double[] signal = Harmonics(Samples, amplitude);
                (double[][] reference, int theirCapped) = RunLiveSpice(solution, input, nodes, signal);
                (double[][] noisy, _) = RunLiveSpice(solution, input, nodes, Signals.WithNoise(signal, 1e-14));
                (double[][] result, int ourCapped) = RunOurs(ours, input, nodes, h, signal);

                double errorDb = Signals.WorstErrorDb(reference, result);
                double controlDb = Signals.WorstErrorDb(reference, noisy);
                bool finite = result.All(o => o.All(double.IsFinite)) && reference.All(o => o.All(double.IsFinite));
                string status = !finite ? "NOT FINITE"
                    : errorDb <= ThresholdDb ? "ok"
                    : controlDb > ThresholdDb ? "sensitive"
                    : "FAIL";
                if (status != "ok" && status != "sensitive")
                    problems++;
                Console.WriteLine($"  {setting.Name,-12}{amplitude,5:0.0}{errorDb,22:F1}{controlDb,12:F1}{$"{ourCapped}/{theirCapped}",16}  {status}");
            }
        }
        return problems;
    }

    private static (double[][] Outputs, int Capped) RunLiveSpice(TransientSolution solution, Expression input, Expression[] nodes, double[] signal)
    {
        var recorder = new NewtonRecorder();
        var simulation = new InstrumentedSimulation(solution, recorder)
        {
            Oversample = Oversample,
            Iterations = TightCap,
            RelativeTolerance = TightRelative,
            AbsoluteTolerance = TightAbsolute,
            FixStalePostNewtonValues = true,
            Input = new[] { input },
            Output = nodes,
        };
        double[][] outputs = nodes.Select(_ => new double[signal.Length]).ToArray();
        simulation.Run(signal.Length, new[] { signal }, outputs);
        int capped = recorder.Steps.Sum(s => s.Count(b => (b & NewtonRecorder.CapFlag) != 0));
        return (outputs, capped);
    }

    private static (double[][] Outputs, int Capped) RunOurs(LoadedCircuit loaded, Expression input, Expression[] nodes, Expression h, double[] signal)
    {
        var engine = new CircuitInterpreter(loaded.System, input, nodes, h, Oversample, TightCap, loaded.Solution)
        {
            RelativeTolerance = TightRelative,
            AbsoluteTolerance = TightAbsolute,
        };
        double[][] outputs = nodes.Select(_ => new double[signal.Length]).ToArray();
        engine.Run(signal, outputs);
        return (outputs, engine.IterationCounts.Count(b => (b & 0x80) != 0));
    }

    private static IEnumerable<IPotControl> PotsOf(Circuit.Circuit circuit)
    {
        foreach (Component component in circuit.Components)
        {
            if (component is Circuit.Circuit inner)
                foreach (IPotControl pot in PotsOf(inner))
                    yield return pot;
            else if (component is IPotControl pot)
                yield return pot;
        }
    }

    private static double[] Harmonics(int samples, double amplitude)
    {
        var x = new double[samples];
        for (int n = 0; n < samples; n++)
            x[n] = Signals.Harmonics(n / (double)SampleRate, amplitude, 82, 2);
        return x;
    }
}
