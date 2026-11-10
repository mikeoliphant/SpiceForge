using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Which pots a memoryless black box replacing the Newton solve would need as inputs: moves each pot alone across its
/// travel and reports how much the folded Newton coefficients (Zr, Qzr, CPr, Czr) change. A pot that leaves them fixed
/// only changes the linear network around the solve, which stays outside the box.
/// </summary>
internal static class PotSensitivity
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;

    public static int Run(string testsDir, string filter)
    {
        string file = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx"))
            .First(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase));
        Expression h = (Real)1 / (SampleRate * Oversample);
        LoadedCircuit loaded = CircuitLoader.Load(file, h);
        var circuit = loaded.Circuit;
        Expression input = circuit.Components.OfType<Circuit.Input>().Select(i => Circuit.Component.DependentVariable(i.Name, Circuit.Component.t)).Single();
        var engine = new CircuitInterpreter(loaded.System, input, circuit.Nodes.Select(n => n.V).ToArray(), h, Oversample, 8, loaded.Solution);
        if (engine.reduction is not LinearPortReduction r)
        {
            Console.WriteLine($"{circuit.Name}: no reduced path (no pot legs to eliminate).");
            return 0;
        }

        var arrays = new (string Name, double[][] Values)[] { ("Zr", r.Zr), ("Qzr", r.Qzr), ("CPr", r.CPr), ("Czr", r.Czr) };
        double[][][] Snapshot() => arrays.Select(a => a.Values.Select(row => (double[])row.Clone()).ToArray()).ToArray();

        double[] schematic = loaded.Pots.Select(p => p.InitialValue).ToArray();
        void Set(double[] values) { values.CopyTo(engine.ParameterValues, 0); engine.UpdateReduction(); }
        Set(schematic);
        double[][][] at = Snapshot();

        Console.WriteLine($"{circuit.Name}: {r.N.Length} Newton port(s), {engine.dz} free unknown(s); largest change of each Newton coefficient array "
            + "relative to its largest entry, moving one pot from 0 to 1 with the others at their schematic positions");
        Console.WriteLine($"  {"pot",-12}" + string.Join("", arrays.Select(a => $"{a.Name,12}")));
        for (int j = 0; j < loaded.Pots.Count; j++)
        {
            var worst = new double[arrays.Length];
            for (int step = 0; step <= 20; step++)
            {
                double[] values = (double[])schematic.Clone();
                values[j] = loaded.Pots[j].ValueAt(step / 20.0);
                Set(values);
                for (int a = 0; a < arrays.Length; a++)
                {
                    double scale = at[a].SelectMany(row => row).Select(Math.Abs).DefaultIfEmpty(0).Max();
                    for (int i = 0; i < arrays[a].Values.Length; i++)
                        for (int c = 0; c < arrays[a].Values[i].Length; c++)
                            if (scale > 0)
                                worst[a] = Math.Max(worst[a], Math.Abs(arrays[a].Values[i][c] - at[a][i][c]) / scale);
                }
            }
            Console.WriteLine($"  {loaded.Pots[j].Name,-12}" + string.Join("", worst.Select((w, a) =>
                at[a].Length == 0 || at[a][0].Length == 0 ? $"{"-",12}" : w == 0 ? $"{"0",12}" : $"{20 * Math.Log10(w),9:F1} dB")));
        }
        Set(schematic);
        return 0;
    }
}
