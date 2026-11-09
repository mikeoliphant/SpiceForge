using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Compares every unknown on every internal timestep against stock Simulation, to find where they first split.</summary>
internal static class TraceCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int Samples = 4800;
    private const double Floor = 1e-9;

    internal static double[] WithNoise(double[] signal, double amount)
    {
        var random = new Random(7);
        return signal.Select(x => x * (1 + amount * (random.NextDouble() * 2 - 1))).ToArray();
    }

    public static int Run(string testsDir, string filter, double amplitude)
    {
        string file = new[] { "Circuits", "Examples", "Unstable" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx"))
            .First(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var circuit = Schematic.Load(file, new NullLog()).Build();
        Expression h = (Real)1 / (SampleRate * Oversample);
        Analysis analysis = circuit.Analyze();
        ExtractedSystem system = Extractor.Extract(analysis, h);
        TransientSolution solution = TransientSolution.Solve(analysis, h);
        Expression input = circuit.Components.Where(i => i is Input)
            .Select(i => Component.DependentVariable(i.Name, Component.t)).Single();

        // The same interpolated input Simulation would generate internally at 8x.
        int steps = Samples * Oversample;
        var interpolated = new double[steps];
        double u = 0;
        for (int n = 0, s = 0; n < Samples; n++)
        {
            double delta = (ReferenceCheck.Harmonics(n / (double)SampleRate, amplitude, 82, 2) - u) * (1.0 / Oversample);
            for (int ov = 0; ov < Oversample; ov++)
            {
                u += delta;
                interpolated[s++] = u;
            }
        }

        double[][] stock = system.Unknowns.Select(_ => new double[steps]).ToArray();
        new Simulation(solution) { Oversample = 1, Iterations = IterationCap, Input = new[] { input }, Output = system.Unknowns }
            .Run(interpolated, stock);

        double[][] ours = system.Unknowns.Select(_ => new double[steps]).ToArray();
        var interpreter = new CircuitInterpreter(system, input, system.Unknowns, h, 1, IterationCap, solution, matchLiveSpice: true);
        interpreter.Run(interpolated, ours);

        var newton = new HashSet<Expression>(solution.Solutions.OfType<NewtonIteration>().SelectMany(s => s.Unknowns));
        double[] peaks = stock.Select(o => Math.Max(o.Max(Math.Abs), Floor)).ToArray();

        double Error(int j, int step) => Math.Abs(stock[j][step] - ours[j][step]) / peaks[j];

        Console.WriteLine("relative error  first step (sample)  unknown");
        foreach (double threshold in new[] { 1e-13, 1e-12, 1e-11, 1e-10, 1e-9, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1 })
        {
            bool found = false;
            for (int step = 0; step < steps && !found; step++)
            {
                for (int j = 0; j < system.Unknowns.Length; j++)
                {
                    if (Error(j, step) > threshold)
                    {
                        Console.WriteLine($"{threshold,14:E0}  {step,10} ({step / Oversample,5})  {system.Unknowns[j]} [{(newton.Contains(system.Unknowns[j]) ? "Newton" : "post")}] iterations {interpreter.IterationCounts[step]:X2}");
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
                Console.WriteLine($"{threshold,14:E0}  never");
        }

        int[] newtonIndices = Enumerable.Range(0, system.Unknowns.Length).Where(j => newton.Contains(system.Unknowns[j])).ToArray();
        double[][] Perturbed(double amount)
        {
            double[][] result = system.Unknowns.Select(_ => new double[steps]).ToArray();
            new Simulation(solution) { Oversample = 1, Iterations = IterationCap, Input = new[] { input }, Output = system.Unknowns }
                .Run(WithNoise(interpolated, amount), result);
            return result;
        }
        double WorstAt(double[][] other, int step) => newtonIndices.Max(j => Math.Abs(stock[j][step] - other[j][step]) / peaks[j]);
        double[][] control15 = Perturbed(2e-16);
        double[][] control13 = Perturbed(1e-14);
        Console.WriteLine("Worst relative error over Newton unknowns (from stock):");
        Console.WriteLine($"{"step",8}{"sample",8}{"ours",12}{"noise 2e-16",13}{"noise 1e-14",13}");
        foreach (int step in new[] { 0, 8, 80, 400, 1000, 2000, 3000, 4000, 4600, 4627, 4628, 4640, 6000, 6128, 6149, 8000, 9283, 9284, 12000, 18651, 18652, 24000, 28023, 28024, 30000 })
            Console.WriteLine($"{step,8}{step / Oversample,8}{WorstAt(ours, step),12:E2}{WorstAt(control15, step),13:E2}{WorstAt(control13, step),13:E2}");
        var capped = Enumerable.Range(0, steps).Where(s => (interpreter.IterationCounts[s] & 0x80) != 0).ToList();
        Console.WriteLine($"Capped steps ({capped.Count}): " + string.Join(" ", capped.Take(40)));

        // Details around the first step whose error is well above roundoff.
        const double Detail = 1e-8;
        int first = -1;
        for (int step = 0; step < steps && first < 0; step++)
            for (int j = 0; j < system.Unknowns.Length; j++)
                if (Error(j, step) > Detail)
                {
                    first = step;
                    break;
                }

        if (first >= 0)
        {
            Console.WriteLine($"First step above {Detail:E0}: {first}. Iterations around it: "
                + string.Join(" ", Enumerable.Range(Math.Max(first - 4, 0), 9).Where(s => s < steps).Select(s => interpreter.IterationCounts[s].ToString("X2"))));
            foreach (int step in new[] { first - 1, first, first + 1 }.Where(s => s >= 0 && s < steps))
            {
                Console.WriteLine($"Step {step}:");
                foreach (int j in Enumerable.Range(0, system.Unknowns.Length).OrderByDescending(j => Error(j, step)).Take(10))
                    Console.WriteLine($"  {system.Unknowns[j],-16} {(newton.Contains(system.Unknowns[j]) ? "Newton" : "post"),-7} rel {Error(j, step),10:E2}  stock {stock[j][step],22:G17}  ours {ours[j][step],22:G17}");
            }
        }
        return 0;
    }
}
