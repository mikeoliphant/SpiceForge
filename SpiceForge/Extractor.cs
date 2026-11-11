using Circuit;
using ComputerAlgebra;
using Util;

namespace SpiceForge;

/// <summary>Derives an <see cref="ExtractedSystem"/> from a LiveSPICE circuit analysis.</summary>
public static class Extractor
{
    private const int GradientSamples = 64;
    private const double RankTolerance = 1e-8;

    private static readonly Variable t = Component.t;

    /// <param name="parameters">Runtime parameters in the equations, such as dynamic pots and switches. The DC starting
    /// point uses their DC values; everywhere else they stay symbols, read at runtime.</param>
    public static ExtractedSystem Extract(Analysis analysis, Expression timeStep, IReadOnlyList<RuntimeParameter>? parameters = null)
    {
        parameters ??= Array.Empty<RuntimeParameter>();
        Arrow[] parameterValues = parameters.Select(p => Arrow.New(p.Symbol, p.DcValue)).ToArray();
        SystemOfEquations system = Discretize(analysis, timeStep, parameterValues, out Arrow[] initialConditions);
        Expression[] unknowns = system.Unknowns.ToArray();
        LinearCombination[] equations = system.Equations.ToArray();
        ExtractedSystem extracted = Split(unknowns, equations, initialConditions);
        return new ExtractedSystem
        {
            Unknowns = extracted.Unknowns, Parameters = extracted.Parameters, Nonlinear = extracted.Nonlinear,
            E = extracted.E, H = extracted.H, C = extracted.C, P = extracted.P, Q = extracted.Q, FreeUnknowns = extracted.FreeUnknowns,
            ExactE = extracted.ExactE, ExactH = extracted.ExactH, ExactC = extracted.ExactC, ExactP = extracted.ExactP,
            Sources = extracted.Sources, Equations = extracted.Equations, InitialConditions = extracted.InitialConditions,
            RuntimeParameters = parameters.ToArray(),
        };
    }

    // Follows TransientSolution.Solve up to the point where the system is discretized.
    private static SystemOfEquations Discretize(Analysis analysis, Expression h, Arrow[] parameterValues, out Arrow[] initialConditions)
    {
        List<Equal> mna = analysis.Equations.ToList();
        List<Expression> y = analysis.Unknowns.ToList();

        var globals = new DynamicNamespace();
        globals.Add("T", h);
        globals.Add(ExprFunction.New("d", Call.If((0 <= t) & (t < h), 1, 0), t));
        globals.Add(ExprFunction.New("u", Call.If(t >= 0, 1, 0), t));
        mna = mna.Resolve(analysis).Resolve(globals).OfType<Equal>().ToList();

        List<Expression> dy_dt = y.Where(i => mna.Any(j => j.DependsOn(Call.D(i, t)))).Select(i => Call.D(i, t)).ToList();

        initialConditions = SolveSteadyState(analysis, mna, y, dy_dt, parameterValues);

        var system = new SystemOfEquations(mna.Substitute(SinglePoleSwitch.ExcludeOpen).OfType<Equal>(), dy_dt.Concat(y));
        system.RowReduce(dy_dt);
        system.BackSubstitute(dy_dt);
        IEnumerable<Equal> integrated = system.Solve(dy_dt)
            .NDIntegrate(t, h, IntegrationMethod.BackwardDifferenceFormula2)
            .Select(i => Equal.New(i.Left, i.Right))
            .ToList();
        system.AddRange(integrated);

        if (system.DependsOn(dy_dt))
            throw new InvalidOperationException("Failed to eliminate differentials from system of equations.");
        return system;
    }

    private static Arrow[] SolveSteadyState(Analysis analysis, List<Equal> mna, List<Expression> y, List<Expression> dy_dt, Arrow[] parameterValues)
    {
        var initial = new List<Arrow>();
        var dc = new SystemOfEquations(mna
            .Substitute(dy_dt.Select(i => Arrow.New(i, 0)).Append(Arrow.New(t, 0), Arrow.New(Component.T, 0), SinglePoleSwitch.IncludeOpen))
            .Substitute(analysis.InitialConditions)
            .Substitute(parameterValues)
            .OfType<Equal>(), y.Select(j => j.Substitute(t, 0)));

        foreach (SystemOfEquations partition in dc.Partition())
        {
            try
            {
                initial.AddRange(partition.Equations.Select(j => Equal.New(j, 0)).NSolve(partition.Unknowns.Select(j => Arrow.New(j, 0))));
            }
            catch (Exception)
            {
                // LiveSPICE continues without initial conditions for a partition it can't solve.
            }
        }
        return initial.ToArray();
    }

    private static ExtractedSystem Split(Expression[] unknowns, LinearCombination[] equations, Arrow[] initialConditions)
    {
        var column = new Dictionary<Expression, int>();
        for (int j = 0; j < unknowns.Length; j++)
            column[unknowns[j]] = j;

        int rows = equations.Length;
        Real[,] exactE = ExactSolver.Zeros(rows, unknowns.Length);
        Real[,] C = ExactSolver.Zeros(rows, 1);
        var nonlinear = new TermTable();
        var parameters = new TermTable();
        var parameterEntries = new List<(int Row, int Column, Real Value)>();
        var nonlinearEntries = new List<(int Row, int Column, Real Value)>();
        var sources = new List<NonlinearSource>();

        void AddNonlinear(int row, Real coefficient, Expression body) =>
            nonlinearEntries.Add((row, nonlinear.IndexOf(body), -coefficient));

        for (int row = 0; row < rows; row++)
        {
            foreach (KeyValuePair<Expression, Expression> entry in equations[row])
            {
                if (entry.Value.EqualsZero())
                    continue;

                if (!entry.Key.EqualsOne())
                {
                    int j = column[entry.Key];
                    if (TryConstant(entry.Value, out Real a))
                    {
                        exactE[row, j] += a;
                    }
                    else
                    {
                        // A coefficient such as R·(P0 + δ) splits: its constant part goes in E, the rest are terms.
                        foreach (Expression part in Sum.TermsOf(entry.Value.Expand()))
                        {
                            if (TryConstant(part, out Real b))
                            {
                                exactE[row, j] += b;
                                continue;
                            }
                            (Real c, Expression body) = SplitCoefficient(entry.Key * part);
                            AddNonlinear(row, c, body);
                        }
                    }
                    continue;
                }

                // The constant bucket holds source terms, and every term that isn't linear in an unknown.
                foreach (Expression term in Sum.TermsOf(entry.Value.Expand()))
                {
                    (Real c, Expression body) = SplitCoefficient(term);
                    if (body.EqualsOne())
                        C[row, 0] -= c;
                    else if (column.TryGetValue(body, out int j))
                        exactE[row, j] += c;
                    else if (body.DependsOn(unknowns))
                        AddNonlinear(row, c, body);
                    else if (IsSymbol(body))
                        parameterEntries.Add((row, parameters.IndexOf(body), -c));
                    else
                        sources.Add(new NonlinearSource(row, (double)(-c), body));
                }
            }
        }

        Expression[] parameterList = parameters.ToArray();
        Expression[] nonlinearList = nonlinear.ToArray();
        Real[,] exactH = ToMatrix(rows, parameterList.Length, parameterEntries);
        Real[,] exactP = ToMatrix(rows, nonlinearList.Length, nonlinearEntries);
        double[,] E = ExactSolver.ToDouble(exactE);

        double[,] Q = InferPorts(unknowns, nonlinearList);
        DropFreeUnknowns(ref unknowns, ref E, ref exactE, ref Q, rows, out Expression[] free);
        return new ExtractedSystem
        {
            Unknowns = unknowns,
            Parameters = parameterList,
            Nonlinear = nonlinearList,
            E = E,
            H = ExactSolver.ToDouble(exactH),
            ExactE = exactE,
            ExactH = exactH,
            ExactC = C,
            ExactP = exactP,
            C = Enumerable.Range(0, rows).Select(i => (double)C[i, 0]).ToArray(),
            P = ExactSolver.ToDouble(exactP),
            Q = Q,
            FreeUnknowns = free,
            Sources = sources.ToArray(),
            Equations = equations,
            InitialConditions = initialConditions,
        };
    }

    // The ports are a basis of the span of the gradients of q, so q depends on y only through Q·y.
    // Sampling over a range of magnitudes visits the branches of piecewise models.
    private static double[,] InferPorts(Expression[] unknowns, Expression[] nonlinear)
    {
        var column = new Dictionary<Expression, int>();
        for (int j = 0; j < unknowns.Length; j++)
            column[unknowns[j]] = j;

        var partials = new List<(int Term, int Column, Expression Value)>();
        for (int k = 0; k < nonlinear.Length; k++)
        {
            List<Expression> dependencies = unknowns.Where(u => nonlinear[k].DependsOn(u)).ToList();
            foreach (KeyValuePair<Expression, Expression> partial in nonlinear[k].Gradient(dependencies))
                partials.Add((k, column[partial.Key], partial.Value));
        }

        var symbols = SymbolCollector.Collect(partials.Select(p => p.Value).Concat(unknowns)).ToArray();
        var evaluator = new ExpressionEvaluator(symbols, partials.Select(p => p.Value).ToArray());
        var values = new double[symbols.Length];
        var results = new double[partials.Count];
        var random = new Random(1);
        var basis = new RowBasis(unknowns.Length, RankTolerance);
        double[] scales = { 0.01, 0.1, 1, 10 };

        for (int sample = 0; sample < GradientSamples; sample++)
        {
            double scale = scales[sample % scales.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = (random.NextDouble() * 2 - 1) * scale;
            evaluator.Evaluate(values, results);

            var gradients = new double[nonlinear.Length, unknowns.Length];
            for (int i = 0; i < partials.Count; i++)
                gradients[partials[i].Term, partials[i].Column] = results[i];

            for (int k = 0; k < nonlinear.Length; k++)
            {
                var row = new double[unknowns.Length];
                for (int j = 0; j < row.Length; j++)
                    row[j] = gradients[k, j];
                basis.Add(row);
            }
        }

        return basis.ToMatrix();
    }

    // An unknown that appears in no equation, such as an unused switch control, can take any value. LiveSPICE
    // leaves it at its previous value, and nothing reads it, so it is dropped when there are more unknowns than rows.
    private static void DropFreeUnknowns(ref Expression[] unknowns, ref double[,] E, ref Real[,] exactE, ref double[,] Q, int rows, out Expression[] free)
    {
        int n = unknowns.Length, k = Q.GetLength(0);
        double[,] e = E, q = Q;
        int[] keep = Enumerable.Range(0, n)
            .Where(j => rows >= n || Enumerable.Range(0, rows).Any(i => e[i, j] != 0) || Enumerable.Range(0, k).Any(r => q[r, j] != 0))
            .ToArray();
        Expression[] all = unknowns;
        free = Enumerable.Range(0, n).Except(keep).Select(j => all[j]).ToArray();
        if (free.Length == 0)
            return;

        var newE = new double[rows, keep.Length];
        var newQ = new double[k, keep.Length];
        var newExact = new Real[rows, keep.Length];
        for (int c = 0; c < keep.Length; c++)
        {
            for (int i = 0; i < rows; i++)
            {
                newE[i, c] = E[i, keep[c]];
                newExact[i, c] = exactE[i, keep[c]];
            }
            for (int r = 0; r < k; r++)
                newQ[r, c] = Q[r, keep[c]];
        }
        unknowns = keep.Select(j => all[j]).ToArray();
        E = newE;
        exactE = newExact;
        Q = newQ;
    }

    private static bool IsSymbol(Expression x) => x is Variable || (x is Call call && call.Target is UnknownFunction);

    // Writes a term as a numeric coefficient times the product of its other factors.
    private static (Real Coefficient, Expression Body) SplitCoefficient(Expression term)
    {
        Real coefficient = 1;
        var factors = new List<Expression>();
        foreach (Expression factor in Product.TermsOf(term))
        {
            if (factor is Constant constant)
                coefficient *= constant.Value;
            else
                factors.Add(factor);
        }
        return (coefficient, Product.New(factors));
    }

    private static bool TryConstant(Expression x, out Real value)
    {
        if (x is Constant constant)
        {
            value = constant.Value;
            return true;
        }
        if (SymbolCollector.Collect(x).Count == 0)
        {
            var results = new double[1];
            new ExpressionEvaluator(Array.Empty<Expression>(), new[] { x }).Evaluate(Array.Empty<double>(), results);
            value = results[0];
            return true;
        }
        value = 0;
        return false;
    }

    private static Real[,] ToMatrix(int rows, int columns, List<(int Row, int Column, Real Value)> entries)
    {
        Real[,] matrix = ExactSolver.Zeros(rows, columns);
        foreach (var (row, column, value) in entries)
            matrix[row, column] += value;
        return matrix;
    }

    private sealed class TermTable
    {
        private readonly Dictionary<Expression, int> indices = new();
        private readonly List<Expression> terms = new();

        public int IndexOf(Expression term)
        {
            if (!indices.TryGetValue(term, out int index))
            {
                index = terms.Count;
                indices[term] = index;
                terms.Add(term);
            }
            return index;
        }

        public Expression[] ToArray() => terms.ToArray();
    }
}
