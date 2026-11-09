using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>
/// Runs a circuit with no nonlinear terms using one offline factorization of E. Oversampling,
/// input interpolation, output averaging and history handling follow LiveSPICE's Simulation.
/// </summary>
public sealed class LinearInterpreter
{
    private const int MaxHistory = 6;

    private enum Kind { Time, Input, InputHistory, History }

    private readonly record struct Binding(Kind Kind, int Unknown, int Delay);

    private static readonly Variable t = Component_t();

    private readonly Binding[] bindings;
    private readonly double[] values;
    private readonly double[,] parameterGain;
    private readonly double[] constant;
    private readonly double[,] sourceGain;
    private readonly ExpressionEvaluator? sourceEvaluator;
    private readonly double[] sourceValues;
    private readonly double[] sourceCoefficients;
    private readonly int[] outputs;
    private readonly int oversample;
    private readonly double substep;
    private readonly double inverseOversample;
    private readonly int maxDelay;

    private double[] y;
    private double[][] history;
    private double time;
    private double input;
    private double inputPrevious;

    public double ConditionNumber { get; }

    public LinearInterpreter(ExtractedSystem system, Expression inputSymbol, IReadOnlyList<Expression> outputSymbols, Expression timeStep, int oversample)
    {
        if (system.Nonlinear.Length != 0)
            throw new ArgumentException("System has nonlinear terms.", nameof(system));

        this.oversample = oversample;
        inverseOversample = 1.0 / oversample;
        substep = (double)(timeStep * oversample) / oversample;

        var lu = new DenseLu(system.E);
        ConditionNumber = lu.ConditionNumber;

        int n = system.Unknowns.Length;
        Expression[] symbols = system.Parameters
            .Concat(system.Sources.SelectMany(s => SymbolScan.Collect(s.Body)))
            .Distinct().ToArray();

        bindings = symbols.Select(s => Bind(s, system.Unknowns, inputSymbol, timeStep)).ToArray();
        values = new double[symbols.Length];
        maxDelay = bindings.Where(b => b.Kind == Kind.History).Select(b => b.Delay).DefaultIfEmpty(0).Max();

        // y = (E^-1 H) p + E^-1 C + (E^-1 S) sources, where p reads the same order as system.Parameters.
        parameterGain = new double[n, symbols.Length];
        for (int j = 0; j < system.Parameters.Length; j++)
        {
            var column = new double[system.Rows];
            for (int i = 0; i < system.Rows; i++)
                column[i] = system.H[i, j];
            StoreColumn(parameterGain, j, lu.Solve(column));
        }

        constant = lu.Solve(system.C);

        sourceGain = new double[n, system.Sources.Length];
        for (int x = 0; x < system.Sources.Length; x++)
        {
            var column = new double[system.Rows];
            column[system.Sources[x].Row] = 1;
            StoreColumn(sourceGain, x, lu.Solve(column));
        }
        sourceCoefficients = system.Sources.Select(s => s.Coefficient).ToArray();
        sourceValues = new double[system.Sources.Length];
        if (system.Sources.Length > 0)
            sourceEvaluator = new ExpressionEvaluator(symbols, system.Sources.Select(s => s.Body).ToArray());

        outputs = outputSymbols.Select(o => Array.IndexOf(system.Unknowns, o)).ToArray();

        y = new double[n];
        double[] initial = InitialValues(system);
        history = new double[Math.Max(maxDelay, 1)][];
        for (int k = 0; k < history.Length; k++)
            history[k] = (double[])initial.Clone();
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

    private void Step()
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            Binding b = bindings[i];
            values[i] = b.Kind switch
            {
                Kind.Time => time,
                Kind.Input => input,
                Kind.InputHistory => inputPrevious,
                _ => history[b.Delay - 1][b.Unknown],
            };
        }

        sourceEvaluator?.Evaluate(values, sourceValues);

        for (int i = 0; i < y.Length; i++)
        {
            double value = constant[i];
            for (int j = 0; j < values.Length; j++)
                value += parameterGain[i, j] * values[j];
            for (int x = 0; x < sourceValues.Length; x++)
                value += sourceGain[i, x] * sourceCoefficients[x] * sourceValues[x];
            y[i] = value;
        }

        double[] oldest = history[history.Length - 1];
        for (int k = history.Length - 1; k > 0; k--)
            history[k] = history[k - 1];
        Array.Copy(y, oldest, y.Length);
        history[0] = oldest;
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

    private static void StoreColumn(double[,] matrix, int column, double[] values)
    {
        for (int i = 0; i < values.Length; i++)
            matrix[i, column] = values[i];
    }

    private static Variable Component_t() => global::Circuit.Component.t;
}
