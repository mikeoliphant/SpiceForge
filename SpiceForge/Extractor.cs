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

    public static ExtractedSystem Extract(Analysis analysis, Expression timeStep)
    {
        SystemOfEquations system = Discretize(analysis, timeStep);
        Expression[] unknowns = system.Unknowns.ToArray();
        LinearCombination[] equations = system.Equations.ToArray();
        return Split(unknowns, equations);
    }

    // Follows TransientSolution.Solve up to the point where the system is discretized.
    private static SystemOfEquations Discretize(Analysis analysis, Expression h)
    {
        List<Equal> mna = analysis.Equations.ToList();
        List<Expression> y = analysis.Unknowns.ToList();

        var globals = new DynamicNamespace();
        globals.Add("T", h);
        globals.Add(ExprFunction.New("d", Call.If((0 <= t) & (t < h), 1, 0), t));
        globals.Add(ExprFunction.New("u", Call.If(t >= 0, 1, 0), t));
        mna = mna.Resolve(analysis).Resolve(globals).OfType<Equal>().ToList();

        List<Expression> dy_dt = y.Where(i => mna.Any(j => j.DependsOn(Call.D(i, t)))).Select(i => Call.D(i, t)).ToList();

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

    private static ExtractedSystem Split(Expression[] unknowns, LinearCombination[] equations)
    {
        var column = new Dictionary<Expression, int>();
        for (int j = 0; j < unknowns.Length; j++)
            column[unknowns[j]] = j;

        int rows = equations.Length;
        var E = new double[rows, unknowns.Length];
        var C = new double[rows];
        var nonlinear = new TermTable();
        var parameters = new TermTable();
        var parameterEntries = new List<(int Row, int Column, double Value)>();
        var nonlinearEntries = new List<(int Row, int Column, double Value)>();
        var sources = new List<NonlinearSource>();

        void AddNonlinear(int row, double coefficient, Expression body) =>
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
                    if (TryConstant(entry.Value, out double a))
                    {
                        E[row, j] += a;
                    }
                    else
                    {
                        (double c, Expression body) = SplitCoefficient(entry.Key * entry.Value);
                        AddNonlinear(row, c, body);
                    }
                    continue;
                }

                // The constant bucket holds source terms, and every term that isn't linear in an unknown.
                foreach (Expression term in Sum.TermsOf(entry.Value.Expand()))
                {
                    (double c, Expression body) = SplitCoefficient(term);
                    if (body.EqualsOne())
                        C[row] -= c;
                    else if (column.TryGetValue(body, out int j))
                        E[row, j] += c;
                    else if (body.DependsOn(unknowns))
                        AddNonlinear(row, c, body);
                    else if (IsSymbol(body))
                        parameterEntries.Add((row, parameters.IndexOf(body), -c));
                    else
                        sources.Add(new NonlinearSource(row, -c, body));
                }
            }
        }

        Expression[] parameterList = parameters.ToArray();
        Expression[] nonlinearList = nonlinear.ToArray();

        return new ExtractedSystem
        {
            Unknowns = unknowns,
            Parameters = parameterList,
            Nonlinear = nonlinearList,
            E = E,
            H = ToMatrix(rows, parameterList.Length, parameterEntries),
            C = C,
            P = ToMatrix(rows, nonlinearList.Length, nonlinearEntries),
            Q = InferPorts(unknowns, nonlinearList),
            Sources = sources.ToArray(),
            Equations = equations,
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

    private static bool IsSymbol(Expression x) => x is Variable || (x is Call call && call.Target is UnknownFunction);

    // Writes a term as a numeric coefficient times the product of its other factors.
    private static (double Coefficient, Expression Body) SplitCoefficient(Expression term)
    {
        double coefficient = 1;
        var factors = new List<Expression>();
        foreach (Expression factor in Product.TermsOf(term))
        {
            if (factor is Constant)
                coefficient *= (double)factor;
            else
                factors.Add(factor);
        }
        return (coefficient, Product.New(factors));
    }

    private static bool TryConstant(Expression x, out double value)
    {
        if (x is Constant)
        {
            value = (double)x;
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

    private static double[,] ToMatrix(int rows, int columns, List<(int Row, int Column, double Value)> entries)
    {
        var matrix = new double[rows, columns];
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
