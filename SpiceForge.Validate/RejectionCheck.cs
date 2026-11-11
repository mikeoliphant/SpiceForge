using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Checks that the linear-port reduction turns away a parameter setting it can't solve, instead of throwing on the
/// audio thread or filling the coefficients with NaN: with every runtime parameter made non-finite, an update must be
/// rejected and leave every coefficient bit-identical, and putting the parameters back must recompute exactly the
/// original coefficients.
/// </summary>
internal static class RejectionCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Expression h = (Real)1 / (SampleRate * Oversample);
        int problems = 0, checkedCircuits = 0;
        foreach (string file in files)
        {
            string name = Path.GetRelativePath(testsDir, file).Replace('\\', '/').Replace(".schx", "");
            try
            {
                LoadedCircuit loaded = CircuitLoader.Load(file, h);
                var inputs = loaded.Circuit.Components.OfType<Input>().ToList();
                var speakers = loaded.Circuit.Components.OfType<Speaker>().ToList();
                if (inputs.Count != 1 || speakers.Count == 0 || loaded.System.RuntimeParameters.Length == 0)
                    continue;
                Expression input = Component.DependentVariable(inputs[0].Name, Component.t);
                Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
                var engine = new CircuitInterpreter(loaded.System, input, nodes, h, Oversample, IterationCap, loaded.Solution);
                LinearPortReduction? reduction = engine.reduction;
                if (reduction == null || reduction.L.Length == 0)
                    continue;

                checkedCircuits++;
                double[][] original = Snapshot(reduction);
                double[] values = engine.ParameterValues;
                double[] saved = (double[])values.Clone();
                bool ok = true;
                string detail = "";
                foreach (double bad in new[] { double.NaN, double.PositiveInfinity })
                {
                    Array.Fill(values, bad);
                    long rejected = reduction.Rejected;
                    engine.UpdateReduction();
                    if (reduction.Rejected != rejected + 1)
                    {
                        ok = false;
                        detail += $" {bad} wasn't rejected;";
                    }
                    if (!Same(original, Snapshot(reduction)))
                    {
                        ok = false;
                        detail += $" {bad} changed the coefficients;";
                    }
                }
                saved.CopyTo(values, 0);
                engine.UpdateReduction();
                if (!Same(original, Snapshot(reduction)))
                {
                    ok = false;
                    detail += " restoring the parameters didn't restore the coefficients;";
                }
                Console.WriteLine($"  {name,-58}{(ok ? "ok" : "FAIL" + detail)}");
                if (!ok)
                    problems++;
            }
            catch (Exception ex)
            {
                problems++;
                Console.WriteLine($"  {name,-58}error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{checkedCircuits} circuit(s) checked, {problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    // Every coefficient the reduction folds, flattened.
    private static double[][] Snapshot(LinearPortReduction r) =>
        new[] { r.Zr, r.Qzr, r.CPr, r.Czr, r.Gain, r.Source, r.TermGain, r.FreeGain, r.ConstraintGain, r.ConstraintSource }
            .SelectMany(rows => rows.Select(row => (double[])row.Clone()))
            .Append((double[])r.Constant.Clone())
            .Append((double[])r.ConstraintConstant.Clone())
            .ToArray();

    // Bit for bit, so a NaN that was there before still matches.
    private static bool Same(double[][] a, double[][] b) =>
        a.Length == b.Length && a.Zip(b).All(p => p.First.Length == p.Second.Length &&
            p.First.Zip(p.Second).All(v => BitConverter.DoubleToInt64Bits(v.First) == BitConverter.DoubleToInt64Bits(v.Second)));
}
