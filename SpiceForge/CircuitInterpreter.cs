using Circuit;
using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>
/// Runs a circuit from one offline factorization of E, with Newton's method on the k ports only.
/// Oversampling, input interpolation, output averaging, history handling, the initial guess and
/// the stopping rule all follow LiveSPICE's Simulation.
/// </summary>
public sealed class CircuitInterpreter
{
    private const int MaxHistory = 6;
    private const byte CapFlag = 0x80;

    private enum Kind { Time, Input, InputHistory, History, Current }

    private readonly record struct Binding(Kind Kind, int Unknown, int Delay);

    private static readonly Variable t = Component.t;

    private readonly Binding[] bindings;
    private readonly double[] values;
    private readonly int parameterCount;
    private readonly double[,] parameterGain;
    private readonly double[] constant;
    private readonly double[,] sourceGain;
    private readonly ExpressionEvaluator? sourceEvaluator;
    private readonly double[] sourceValues;
    private readonly double[] sourceCoefficients;
    private readonly int[] outputs;
    private readonly int oversample;
    private readonly int iterationCap;
    private readonly double substep;
    private readonly double inverseOversample;

    // Port reduction: w = voc + Z·q(w), and y = yLinear + G·q.
    private readonly int k;
    private readonly int m;
    private readonly double[,] Q;
    private readonly double[,] G;
    private readonly double[,] Z;
    private readonly ExpressionEvaluator? nonlinearEvaluator;
    private readonly double[] nonlinearInputs = Array.Empty<double>();
    private readonly double[] nonlinearResults = Array.Empty<double>();
    private readonly int[] portSymbols = Array.Empty<int>();
    private readonly int[] convergence;
    private readonly double[,] D;
    private readonly Binding[] finalBindings = Array.Empty<Binding>();
    private readonly double[] finalInputs = Array.Empty<double>();
    private readonly double[] finalResults = Array.Empty<double>();
    private readonly int[] finalTargets = Array.Empty<int>();
    private readonly FinalSolutionEvaluator? finalEvaluator;
    private readonly int[] finalNewton = Array.Empty<int>();
    private readonly double[] yBeforeUpdate;
    private readonly double[][] augmented;

    private readonly double[] y;
    private readonly double[] yLinear;
    private readonly double[] w;
    private readonly double[] voc;
    private readonly double[] q;
    private readonly double[,] jacobian;
    private readonly double[] linearizedQ;
    private readonly double[][] history;
    private double time;
    private double input;
    private double inputPrevious;

    public double ConditionNumber { get; }

    /// <summary>One entry per oversampled step: the Newton iteration count, with 0x80 set if the cap was hit.</summary>
    public List<byte> IterationCounts { get; } = new();

    /// <param name="solution">LiveSPICE's solution of the circuit. It decides which unknowns the stopping rule tests,
    /// and how the unknowns outside its Newton systems are computed once Newton has finished.</param>
    public CircuitInterpreter(
        ExtractedSystem system,
        Expression inputSymbol,
        IReadOnlyList<Expression> outputSymbols,
        Expression timeStep,
        int oversample,
        int iterationCap,
        TransientSolution solution)
    {
        this.oversample = oversample;
        this.iterationCap = iterationCap;
        inverseOversample = 1.0 / oversample;
        substep = (double)(timeStep * oversample) / oversample;

        k = system.Ports;
        m = system.Nonlinear.Length;
        Q = system.Q;
        D = new double[m, k];
        jacobian = new double[m, k];
        w = new double[k];
        q = new double[m];

        if (m > 0)
        {
            if (system.Nonlinear.Any(term => system.Parameters.Any(p => term.DependsOn(p))))
                throw new NotSupportedException("A nonlinear term reads inputs or past values.");

            // Each port's pivot unknown stands in for it: with the other unknowns at zero, Q·y = w.
            Expression[] pivots = PortPivots(system).Select(j => system.Unknowns[j]).ToArray();
            var expressions = system.Nonlinear.ToList();
            foreach (Expression term in system.Nonlinear)
            {
                Dictionary<Expression, Expression> gradient = term.Gradient(pivots);
                expressions.AddRange(pivots.Select(p => gradient[p]));
            }

            Expression[] nonlinearSymbols = expressions.SelectMany(SymbolScan.Collect).Concat(pivots).Distinct().ToArray();
            nonlinearEvaluator = new ExpressionEvaluator(nonlinearSymbols, expressions);
            nonlinearInputs = new double[nonlinearSymbols.Length];
            nonlinearResults = new double[expressions.Count];
            portSymbols = pivots.Select(p => Array.IndexOf(nonlinearSymbols, p)).ToArray();
        }

        DenseLu lu = Factor(system, out double[,] shifted);
        ConditionNumber = lu.ConditionNumber;

        int n = system.Unknowns.Length;
        int rows = system.Rows;
        Expression[] symbols = system.Parameters
            .Concat(system.Sources.SelectMany(s => SymbolScan.Collect(s.Body)))
            .Distinct().ToArray();

        bindings = symbols.Select(s => Bind(s, system.Unknowns, inputSymbol, timeStep)).ToArray();
        values = new double[symbols.Length];
        int maxDelay = bindings.Where(b => b.Kind == Kind.History).Select(b => b.Delay).DefaultIfEmpty(0).Max();

        // Parameters come first in the symbol list; symbols only read by sources follow them.
        parameterCount = system.Parameters.Length;
        parameterGain = SolveColumns(lu, system.H, n);
        constant = lu.Solve(system.C);

        var sourceRows = new double[rows, system.Sources.Length];
        for (int x = 0; x < system.Sources.Length; x++)
            sourceRows[system.Sources[x].Row, x] = 1;
        sourceGain = SolveColumns(lu, sourceRows, n);
        sourceCoefficients = system.Sources.Select(s => s.Coefficient).ToArray();
        sourceValues = new double[system.Sources.Length];
        if (system.Sources.Length > 0)
            sourceEvaluator = new ExpressionEvaluator(symbols, system.Sources.Select(s => s.Body).ToArray());

        outputs = outputSymbols.Select(o => Array.IndexOf(system.Unknowns, o)).ToArray();

        G = SolveColumns(lu, system.P, n);
        Z = new double[k, m];
        for (int r = 0; r < k; r++)
            for (int c = 0; c < m; c++)
                for (int j = 0; j < n; j++)
                    Z[r, c] += Q[r, j] * G[j, c];

        convergence = solution.Solutions.OfType<NewtonIteration>().SelectMany(s => s.Unknowns)
            .Select(u => Array.IndexOf(system.Unknowns, u)).Where(j => j >= 0).ToArray();

        if (m > 0)
        {
            finalEvaluator = new FinalSolutionEvaluator(solution);
            finalBindings = finalEvaluator.Symbols.Select(s =>
            {
                int unknown = Array.IndexOf(system.Unknowns, s);
                return unknown >= 0 ? new Binding(Kind.Current, unknown, 0) : Bind(s, system.Unknowns, inputSymbol, timeStep);
            }).ToArray();
            maxDelay = Math.Max(maxDelay, finalBindings.Where(b => b.Kind == Kind.History).Select(b => b.Delay).DefaultIfEmpty(0).Max());
            finalNewton = finalEvaluator.NewtonUnknowns.Select(u => Array.IndexOf(system.Unknowns, u)).ToArray();
            finalInputs = new double[finalBindings.Length + 2 * finalNewton.Length];
            finalResults = new double[finalEvaluator.Assigned.Length];
            finalTargets = finalEvaluator.Assigned.Select(a => Array.IndexOf(system.Unknowns, a)).ToArray();
        }
        yBeforeUpdate = new double[n];

        augmented = Enumerable.Range(0, k).Select(_ => new double[k + 1]).ToArray();

        y = new double[n];
        yLinear = new double[n];
        voc = new double[k];
        linearizedQ = new double[m];

        double[] initial = InitialValues(system);
        history = new double[Math.Max(maxDelay, 1)][];
        for (int d = 0; d < history.Length; d++)
            history[d] = (double[])initial.Clone();
    }

    public void Run(double[] inputSamples, double[][] outputBuffers)
    {
        var sums = new double[outputs.Length];
        for (int n = 0; n < inputSamples.Length; n++)
        {
            double delta = (inputSamples[n] - input) * inverseOversample;
            Array.Clear(sums);

            for (int ov = oversample; ov > 0; ov--)
            {
                time += substep;
                input += delta;
                Step();

                for (int o = 0; o < outputs.Length; o++)
                    if (outputs[o] >= 0)
                        sums[o] += y[outputs[o]];

                inputPrevious = input;
            }

            for (int o = 0; o < outputs.Length; o++)
                outputBuffers[o][n] = sums[o] * inverseOversample;
        }
    }

    private double Read(Binding b) => b.Kind switch
    {
        Kind.Time => time,
        Kind.Input => input,
        Kind.InputHistory => inputPrevious,
        Kind.Current => y[b.Unknown],
        _ => history[b.Delay - 1][b.Unknown],
    };

    private void Step()
    {
        for (int i = 0; i < bindings.Length; i++)
            values[i] = Read(bindings[i]);

        sourceEvaluator?.Evaluate(values, sourceValues);

        for (int i = 0; i < y.Length; i++)
        {
            double value = constant[i];
            for (int j = 0; j < parameterCount; j++)
                value += parameterGain[i, j] * values[j];
            for (int x = 0; x < sourceValues.Length; x++)
                value += sourceGain[i, x] * sourceCoefficients[x] * sourceValues[x];
            yLinear[i] = value;
        }

        if (m == 0)
            Array.Copy(yLinear, y, y.Length);
        else
            SolveNewton();

        if (finalEvaluator != null)
        {
            int position = 0;
            for (int i = 0; i < finalBindings.Length; i++)
                finalInputs[position++] = Read(finalBindings[i]);
            for (int i = 0; i < finalNewton.Length; i++)
                finalInputs[position++] = yBeforeUpdate[finalNewton[i]];
            for (int i = 0; i < finalNewton.Length; i++)
                finalInputs[position++] = y[finalNewton[i]];
            finalEvaluator.Evaluate(finalInputs, finalResults);
            for (int i = 0; i < finalTargets.Length; i++)
                if (finalTargets[i] >= 0)
                    y[finalTargets[i]] = finalResults[i];
        }

        double[] oldest = history[history.Length - 1];
        for (int d = history.Length - 1; d > 0; d--)
            history[d] = history[d - 1];
        Array.Copy(y, oldest, y.Length);
        history[0] = oldest;
    }

    private void SolveNewton()
    {
        double[] previous = history[0];
        Array.Copy(previous, y, y.Length);
        Multiply(Q, previous, w);
        Multiply(Q, yLinear, voc);

        for (int iteration = 1; ; iteration++)
        {
            EvaluateNonlinear();

            // Solve (I - Z·J)·wNext = voc + Z·(q - J·w), the linearization of w = voc + Z·q(w).
            for (int r = 0; r < k; r++)
            {
                double[] row = augmented[r];
                Array.Clear(row);
                row[r] = 1;
                double rhs = voc[r];
                for (int c = 0; c < m; c++)
                {
                    double z = Z[r, c];
                    if (z == 0)
                        continue;
                    double linear = q[c];
                    for (int p = 0; p < k; p++)
                    {
                        row[p] -= z * jacobian[c, p];
                        linear -= jacobian[c, p] * w[p];
                    }
                    rhs += z * linear;
                }
                row[k] = rhs;
            }
            Simulation.Solve(augmented, k, k + 1);

            // q linearized at w and evaluated at the next w, which is what full Newton solves with.
            for (int c = 0; c < m; c++)
            {
                double value = q[c];
                for (int p = 0; p < k; p++)
                    value += jacobian[c, p] * (augmented[p][k] - w[p]);
                linearizedQ[c] = value;
            }

            bool done = true;
            foreach (int j in convergence)
            {
                double next = Combine(j);
                done &= Math.Abs(next - y[j]) < Math.Abs(y[j]) * 1e-4 + 1e-6;
            }

            Array.Copy(y, yBeforeUpdate, y.Length);
            for (int i = 0; i < y.Length; i++)
                y[i] = Combine(i);
            for (int p = 0; p < k; p++)
                w[p] = augmented[p][k];

            if (done || iteration == iterationCap)
            {
                IterationCounts.Add((byte)(done ? iteration : iteration | CapFlag));
                return;
            }
        }
    }

    private double Combine(int i)
    {
        double value = yLinear[i];
        for (int c = 0; c < m; c++)
            value += G[i, c] * linearizedQ[c];
        return value;
    }

    private void EvaluateNonlinear()
    {
        for (int p = 0; p < k; p++)
            if (portSymbols[p] >= 0)
                nonlinearInputs[portSymbols[p]] = w[p];

        nonlinearEvaluator!.Evaluate(nonlinearInputs, nonlinearResults);

        for (int c = 0; c < m; c++)
        {
            double value = nonlinearResults[c];
            for (int p = 0; p < k; p++)
            {
                value -= D[c, p] * w[p];
                jacobian[c, p] = nonlinearResults[m + c * k + p] - D[c, p];
            }
            q[c] = value;
        }
    }

    // E can be singular when an unknown appears only inside nonlinear terms. Writing q = q' + D·w moves
    // D·Q into E, which leaves the equations and the Newton iterates unchanged. D is a sampled Jacobian.
    private DenseLu Factor(ExtractedSystem system, out double[,] matrix)
    {
        matrix = system.E;
        try
        {
            return new DenseLu(matrix);
        }
        catch (SingularSystemException) when (m > 0 && k > 0 && system.Rows == system.Unknowns.Length)
        {
        }

        // Prefer the smallest linearization that works. A large D swamps small currents with cancellation
        // error, so start at w = 0 and only move to larger voltages if E is still singular.
        var random = new Random(4);
        double[] scales = { 0, 0.01, 0.03, 0.1, 0.3, 0.5, 1 };

        for (int sample = 0; sample < scales.Length * 4; sample++)
        {
            double scale = scales[sample / 4];
            for (int p = 0; p < k; p++)
                w[p] = (random.NextDouble() * 2 - 1) * scale;
            Array.Clear(D);
            EvaluateNonlinear();

            bool finite = true;
            foreach (double value in jacobian)
                finite &= !double.IsNaN(value) && !double.IsInfinity(value);
            if (!finite)
                continue;

            double[,] candidate = Shift(system, jacobian);
            try
            {
                var lu = new DenseLu(candidate);
                Array.Copy(jacobian, D, D.Length);
                matrix = candidate;
                return lu;
            }
            catch (SingularSystemException)
            {
            }
        }

        throw new SingularSystemException("E is singular for every sampled linearization.");
    }

    private double[,] Shift(ExtractedSystem system, double[,] d)
    {
        int rows = system.Rows, n = system.Unknowns.Length;
        var shifted = (double[,])system.E.Clone();
        for (int i = 0; i < rows; i++)
            for (int c = 0; c < m; c++)
            {
                double pic = system.P[i, c];
                if (pic == 0)
                    continue;
                for (int p = 0; p < k; p++)
                {
                    if (d[c, p] == 0)
                        continue;
                    for (int j = 0; j < n; j++)
                        shifted[i, j] -= pic * d[c, p] * Q[p, j];
                }
            }
        return shifted;
    }

    // Q is in reduced row echelon form, so each row has a pivot that is the only nonzero in its column.
    private static int[] PortPivots(ExtractedSystem system)
    {
        double[,] matrix = system.Q;
        int k = matrix.GetLength(0), n = matrix.GetLength(1);
        var pivots = new int[k];
        for (int r = 0; r < k; r++)
        {
            pivots[r] = Enumerable.Range(0, n)
                .First(j => matrix[r, j] == 1 && Enumerable.Range(0, k).All(i => i == r || matrix[i, j] == 0));
        }
        return pivots;
    }

    private static void Multiply(double[,] matrix, double[] vector, double[] result)
    {
        for (int r = 0; r < result.Length; r++)
        {
            double value = 0;
            for (int j = 0; j < vector.Length; j++)
                value += matrix[r, j] * vector[j];
            result[r] = value;
        }
    }

    private static double[,] SolveColumns(DenseLu lu, double[,] rightHandSides, int n)
    {
        int rows = rightHandSides.GetLength(0), columns = rightHandSides.GetLength(1);
        var result = new double[n, columns];
        var column = new double[rows];
        for (int c = 0; c < columns; c++)
        {
            for (int i = 0; i < rows; i++)
                column[i] = rightHandSides[i, c];
            double[] solved = lu.Solve(column);
            for (int i = 0; i < n; i++)
                result[i, c] = solved[i];
        }
        return result;
    }

    private static Binding Bind(Expression symbol, Expression[] unknowns, Expression input, Expression h)
    {
        if (symbol.Equals(t))
            return new Binding(Kind.Time, -1, 0);
        if (symbol.Equals(input))
            return new Binding(Kind.Input, -1, 0);
        if (symbol.Equals(input.Evaluate(t, t - h)))
            return new Binding(Kind.InputHistory, -1, 1);

        for (int delay = 1; delay <= MaxHistory; delay++)
            for (int j = 0; j < unknowns.Length; j++)
                if (symbol.Equals(unknowns[j].Evaluate(t, t - delay * h)))
                    return new Binding(Kind.History, j, delay);

        throw new NotSupportedException($"Don't know how to supply '{symbol}'.");
    }

    private static double[] InitialValues(ExtractedSystem system)
    {
        return system.Unknowns.Select(u =>
        {
            Expression value = u.Substitute(t, 0).Evaluate(system.InitialConditions);
            return value is Constant ? (double)value : 0.0;
        }).ToArray();
    }
}
