using Circuit;
using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>
/// Runs a circuit from one offline factorization of E, with Newton's method on the k ports only (plus the free unknowns, when E is singular).
/// Oversampling, input interpolation, output averaging, history handling, the initial guess and
/// the stopping rule all follow LiveSPICE's Simulation.
/// </summary>
public sealed class CircuitInterpreter
{
    private const int MaxHistory = 6;
    private const byte CapFlag = 0x80;
    private const double CouplingTolerance = 1e-12;

    internal enum Kind { Time, Input, InputHistory, History, Current }

    internal readonly record struct Binding(Kind Kind, int Unknown, int Delay);

    private static readonly Variable t = Component.t;

    internal readonly Binding[] bindings;
    internal readonly Expression[] Symbols;
    internal readonly double[] values;
    internal readonly int parameterCount;
    internal readonly double[,] parameterGain;
    internal readonly double[] constant;
    internal readonly double[,] sourceGain;
    internal readonly ExpressionEvaluator? sourceEvaluator;
    internal readonly double[] sourceValues;
    internal readonly double[] sourceCoefficients;
    internal readonly int[] outputs;
    internal readonly int oversample;
    internal readonly int iterationCap;
    internal readonly double substep;
    internal readonly double inverseOversample;

    // Port reduction: w = voc + Z·q(w), and y = yLinear + G·q.
    internal readonly int k;
    internal readonly int m;
    internal readonly double[,] Q;
    internal readonly double[,] G;
    internal readonly double[,] Z;
    internal readonly ExpressionEvaluator? nonlinearEvaluator;
    internal readonly double[] nonlinearInputs = Array.Empty<double>();
    internal readonly double[] nonlinearResults = Array.Empty<double>();
    internal readonly int[] portSymbols = Array.Empty<int>();
    internal readonly int[] convergence;
    internal readonly double[,] D;

    // Linear-port reduction (see LinearPortReduction): ports read only by linear terms are eliminated from Newton.
    internal readonly bool[] termLinear = Array.Empty<bool>();
    internal readonly LinearPortReduction? reduction;
    private readonly double[][] reducedRows = Array.Empty<double[]>();
    private readonly double[] reducedOpen = Array.Empty<double>();
    private readonly double[] reducedConstant = Array.Empty<double>();
    private readonly double[] reducedResidual = Array.Empty<double>();
    private readonly double[,] linearJacobian = new double[0, 0];
    // Coupled groups of the reduced Newton system, as positions (reduced ports, then free unknowns), in solve order.
    internal readonly int[][] reducedGroups = Array.Empty<int[]>();
    internal readonly double[][][] reducedGroupRows = Array.Empty<double[][]>();
    private readonly double[] reducedSolution = Array.Empty<double>();

    /// <summary>Ports eliminated from Newton because only linear terms read them, and how often the reduced system
    /// has been recomputed (once per change of the runtime parameters).</summary>
    public int EliminatedPorts => reduction?.L.Length ?? 0;
    public long ReductionUpdates => reduction?.Updates ?? 0;

    /// <summary>For profiling only: take the normal Newton path when there are no linear ports to eliminate, as before
    /// every circuit went through the reduced path.</summary>
    internal static bool NormalPathForTesting { get; set; }


    // Runtime parameters (dynamic pots), by index in ExtractedSystem.RuntimeParameters.
    internal readonly int[] parameterSymbols = Array.Empty<int>();

    /// <summary>Current values of the runtime parameters, in the order of ExtractedSystem.RuntimeParameters. Change
    /// them between Run calls (or from the audio thread between blocks); they take effect on the next step.</summary>
    public double[] ParameterValues { get; }

    /// <summary>Whether every nonlinear term is linear in the ports, so Newton stops after one step.</summary>
    public bool LinearOnly { get; }

    // Singular E: free unknowns z join the Newton unknowns, and d constraint rows join its equations.
    internal readonly int dz;
    internal readonly int K;
    internal readonly int[] free = Array.Empty<int>();
    internal readonly double[,] Nz;
    internal readonly double[,] Qz;
    internal readonly double[,] CH;
    internal readonly double[] CC;
    internal readonly double[,] CS;
    internal readonly double[,] CP;
    internal readonly double[] z;
    internal readonly double[] zNext;
    internal readonly double[] cs;
    internal readonly Binding[] finalBindings = Array.Empty<Binding>();
    internal readonly double[] finalInputs = Array.Empty<double>();
    internal readonly double[] finalResults = Array.Empty<double>();
    internal readonly int[] finalTargets = Array.Empty<int>();
    internal readonly FinalSolutionEvaluator? finalEvaluator;
    internal readonly int[] finalNewton = Array.Empty<int>();
    internal readonly double[] yBeforeUpdate;
    internal readonly double[][] augmented;
    internal readonly bool[,] dependsOn;
    internal readonly double[] stepSolution;
    internal readonly int[][] groups;
    internal readonly double[][][] groupRows;

    internal readonly double[] y;
    internal readonly double[] yLinear;
    internal readonly double[] w;
    internal readonly double[] voc;
    internal readonly double[] q;
    internal readonly double[,] jacobian;
    internal readonly double[] linearizedQ;
    internal readonly double[][] history;
    private double time;
    private double input;
    private double inputPrevious;

    public double ConditionNumber { get; }

    /// <summary>Sizes of the coupled port groups, in solve order.</summary>
    public int[] GroupSizes => (reduction != null ? reducedGroups : groups).Select(g => g.Length).ToArray();

    /// <summary>Strongly connected blocks of ports and states together.</summary>
    public int StateBlocks { get; }

    /// <summary>Largest Z entry treated as zero, relative to its column.</summary>
    public double LargestDroppedCoupling { get; private set; }

    /// <summary>One entry per oversampled step: the Newton iteration count, with 0x80 set if the cap was hit.</summary>
    public List<byte> IterationCounts { get; } = new();

    /// <param name="solution">LiveSPICE's solution of the circuit. It decides which unknowns the stopping rule tests.</param>
    /// <param name="matchLiveSpice">Reproduce LiveSPICE's output sample for sample, including its use of stale cached
    /// values after the Newton loop (see <see cref="MatchLiveSpice"/>). Off by default.</param>
    public CircuitInterpreter(
        ExtractedSystem system,
        Expression inputSymbol,
        IReadOnlyList<Expression> outputSymbols,
        Expression timeStep,
        int oversample,
        int iterationCap,
        TransientSolution solution,
        bool segment = true,
        bool matchLiveSpice = false)
    {
        MatchLiveSpice = matchLiveSpice;
        if (matchLiveSpice && system.RuntimeParameters.Length > 0)
            throw new ArgumentException("Runtime parameters can't be combined with matching LiveSPICE.", nameof(matchLiveSpice));
        ParameterValues = system.RuntimeParameters.Select(p => p.InitialValue).ToArray();
        this.oversample = oversample;
        this.iterationCap = iterationCap;
        inverseOversample = 1.0 / oversample;
        substep = (double)(timeStep * oversample) / oversample;

        k = system.Ports;
        m = system.Nonlinear.Length;
        Q = system.Q;
        D = new double[m, k];
        dependsOn = new bool[m, k];
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
            int termIndex = 0;
            foreach (Expression term in system.Nonlinear)
            {
                Dictionary<Expression, Expression> gradient = term.Gradient(pivots);
                expressions.AddRange(pivots.Select(p => gradient[p]));
                for (int p = 0; p < k; p++)
                    dependsOn[termIndex, p] = !gradient[pivots[p]].EqualsZero();
                termIndex++;
            }

            Expression[] nonlinearSymbols = expressions.SelectMany(SymbolScan.Collect).Concat(pivots)
                .Concat(system.RuntimeParameters.Select(r => (Expression)r.Symbol)).Distinct().ToArray();
            nonlinearEvaluator = new ExpressionEvaluator(nonlinearSymbols, expressions);
            nonlinearInputs = new double[nonlinearSymbols.Length];
            nonlinearResults = new double[expressions.Count];
            portSymbols = pivots.Select(p => Array.IndexOf(nonlinearSymbols, p)).ToArray();
            parameterSymbols = system.RuntimeParameters.Select(r => Array.IndexOf(nonlinearSymbols, (Expression)r.Symbol)).ToArray();

            // When every term is linear in the ports (only pot legs, say), one Newton step is exact.
            LinearOnly = expressions.Skip(m).All(partial => !partial.DependsOn(pivots));

            // A term is linear when its derivatives don't depend on the ports and it is zero at zero, so q = J·w with
            // J depending only on runtime parameters (pot legs, P·i).
            var runtimeSymbols = new HashSet<Expression>(system.RuntimeParameters.Select(r => (Expression)r.Symbol));
            termLinear = new bool[m];
            for (int c = 0; c < m; c++)
            {
                bool constantSlope = Enumerable.Range(0, k).All(p => !expressions[m + c * k + p].DependsOn(pivots));
                var zeros = SymbolScan.Collect(system.Nonlinear[c]).Where(s => !runtimeSymbols.Contains(s)).Select(s => Arrow.New(s, 0));
                termLinear[c] = constantSlope && system.Nonlinear[c].Evaluate(zeros).EqualsZero();
            }
        }

        DenseLu? lu = null;
        SingularSplit? split = null;
        try
        {
            lu = new DenseLu(system.E);
        }
        catch (SingularSystemException) when (m > 0 && k > 0 && system.Rows == system.Unknowns.Length)
        {
            split = SingularSplit.Build(system);
        }
        ConditionNumber = lu?.ConditionNumber ?? double.NaN;

        int n = system.Unknowns.Length;
        int rows = system.Rows;
        Expression[] symbols = system.Parameters
            .Concat(system.Sources.SelectMany(s => SymbolScan.Collect(s.Body)))
            .Distinct().ToArray();

        Symbols = symbols;
        bindings = symbols.Select(s => Bind(s, system.Unknowns, inputSymbol, timeStep)).ToArray();
        values = new double[symbols.Length];
        int maxDelay = bindings.Where(b => b.Kind == Kind.History).Select(b => b.Delay).DefaultIfEmpty(0).Max();

        // Parameters come first in the symbol list; symbols only read by sources follow them.
        parameterCount = system.Parameters.Length;
        parameterGain = split?.ParameterGain ?? SolveColumns(lu!, system.H, n);
        constant = split?.Constant ?? lu!.Solve(system.C);

        var sourceRows = new double[rows, system.Sources.Length];
        for (int x = 0; x < system.Sources.Length; x++)
            sourceRows[system.Sources[x].Row, x] = 1;
        sourceGain = split?.SourceGain ?? SolveColumns(lu!, sourceRows, n);
        sourceCoefficients = system.Sources.Select(s => s.Coefficient).ToArray();
        sourceValues = new double[system.Sources.Length];
        if (system.Sources.Length > 0)
            sourceEvaluator = new ExpressionEvaluator(symbols, system.Sources.Select(s => s.Body).ToArray());

        outputs = outputSymbols.Select(o => Array.IndexOf(system.Unknowns, o)).ToArray();

        if (split != null)
        {
            G = split.G;
            Z = split.Z;
        }
        else
        {
            G = SolveColumns(lu!, system.P, n);
            Z = new double[k, m];
            for (int r = 0; r < k; r++)
                for (int c = 0; c < m; c++)
                    for (int j = 0; j < n; j++)
                        Z[r, c] += Q[r, j] * G[j, c];
        }

        dz = split?.Free.Length ?? 0;
        K = k + dz;
        free = split?.Free ?? Array.Empty<int>();
        Nz = split?.Nz ?? new double[n, 0];
        Qz = split?.Qz ?? new double[k, 0];
        CH = split?.CH ?? new double[0, parameterCount];
        CC = split?.CC ?? Array.Empty<double>();
        CS = split?.CS ?? new double[0, system.Sources.Length];
        CP = split?.CP ?? new double[0, m];
        z = new double[dz];
        zNext = new double[dz];
        cs = new double[dz];

        // Entries of Z that exact arithmetic would make zero come out as roundoff, and are cleared for the port groups.
        // With nothing to eliminate, the reduction's coefficients are Z itself, so they're cleared before it is built,
        // as the normal Newton path always did. With pot legs to fold in, the reduction is built from Z as it is: a
        // cleared entry can still matter once folded (a high-gain circuit amplifies it), and clearing them broke
        // circuits such as the Rockerverb and the Fender 5e3.
        bool clear = segment && dz == 0;
        reduction = matchLiveSpice || m == 0 ? null : BuildReduction(clear);
        if (reduction != null)
        {
            int size = reduction.N.Length + dz;
            reducedRows = Enumerable.Range(0, size).Select(_ => new double[RowLength(size)]).ToArray();
            reducedOpen = new double[reduction.N.Length];
            reducedConstant = new double[dz];
            reducedResidual = new double[reduction.L.Length];
            linearJacobian = new double[reduction.TL.Length, reduction.L.Length];

            // Learn which coefficients can be nonzero, at the initial values and a couple of other settings.
            double[] initialValues = (double[])ParameterValues.Clone();
            var random = new Random(5);
            for (int sample = 0; sample < 3; sample++)
            {
                for (int j = 0; j < ParameterValues.Length; j++)
                    ParameterValues[j] = sample < 2 ? -0.4 + 0.8 * random.NextDouble() : initialValues[j];
                reduction.Update(ParameterValues, LinearJacobian);
            }
            reduction.Freeze(ParameterValues, LinearJacobian);
        }
        if (clear && !coupledCleared)
            ClearRoundoffCoupling();
        if (reduction != null)
        {
            int size = reduction.N.Length + dz;
            reducedGroups = dz > 0 || !segment ? (size > 0 ? new[] { Enumerable.Range(0, size).ToArray() } : Array.Empty<int[]>())
                : DependencyGraph.StronglyConnected(reduction.N.Length, ReducedDependencies).Select(g => g.OrderBy(p => p).ToArray()).ToArray();
            reducedGroupRows = reducedGroups.Select(g => g.Select(_ => new double[RowLength(g.Length)]).ToArray()).ToArray();
            reducedSolution = new double[size];
        }

        // The constraint rows couple everything, so a split system is solved as one group.
        groups = dz > 0 ? new[] { Enumerable.Range(0, K).ToArray() } : BuildGroups(segment);
        stepSolution = new double[K];
        groupRows = groups.Select(g => g.Select(_ => new double[RowLength(g.Length)]).ToArray()).ToArray();
        StateBlocks = CountStateBlocks();
        Segments = reduction != null ? BuildReducedSegments(system) : BuildSegments(system);

        convergence = solution.Solutions.OfType<NewtonIteration>().SelectMany(s => s.Unknowns)
            .Select(u => Array.IndexOf(system.Unknowns, u)).Where(j => j >= 0).ToArray();

        if (m > 0 && matchLiveSpice)
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

        augmented = Enumerable.Range(0, K).Select(_ => new double[RowLength(K)]).ToArray();

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
        // Runtime parameters only change between calls, so the reduced coefficients only need checking here.
        reduction?.Update(ParameterValues, LinearJacobian);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
        RunTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
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

        for (int f = 0; f < dz; f++)
        {
            double value = CC[f];
            for (int j = 0; j < parameterCount; j++)
                value += CH[f, j] * values[j];
            for (int x = 0; x < sourceValues.Length; x++)
                value += CS[f, x] * sourceCoefficients[x] * sourceValues[x];
            cs[f] = value;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (m == 0)
            Array.Copy(yLinear, y, y.Length);
        else if (SegmentSolver != null)
            SolveWithSegments();
        else if (reduction != null)
            SolveReduced();
        else
        {
            SolveNewton();
        }
        SolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;

        if (finalEvaluator != null && SegmentSolver == null)
        {
            int position = 0;
            for (int i = 0; i < finalBindings.Length; i++)
                finalInputs[position++] = Read(finalBindings[i]);
            for (int i = 0; i < finalNewton.Length; i++)
                finalInputs[position++] = finalNewton[i] >= 0 ? yBeforeUpdate[finalNewton[i]] : 0;
            for (int i = 0; i < finalNewton.Length; i++)
                finalInputs[position++] = finalNewton[i] >= 0 ? y[finalNewton[i]] : 0;
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
        for (int f = 0; f < dz; f++)
            z[f] = previous[free[f]];

        for (int iteration = 1; ; iteration++)
        {
            EvaluateNonlinear();

            // Solve (I - Z·J)·wNext - Qz·zNext = voc + Z·(q - J·w), the linearization of w = voc + Z·q(w) + Qz·z,
            // together with CP·J·wNext = -cs - CP·(q - J·w), the linearization of the constraints cs + CP·q(w) = 0.
            for (int r = 0; r < k; r++)
            {
                double[] row = augmented[r];
                Array.Clear(row);
                row[r] = 1;
                double rhs = voc[r];
                for (int c = 0; c < m; c++)
                {
                    double zc = Z[r, c];
                    if (zc == 0)
                        continue;
                    double linear = q[c];
                    for (int p = 0; p < k; p++)
                    {
                        row[p] -= zc * jacobian[c, p];
                        linear -= jacobian[c, p] * w[p];
                    }
                    rhs += zc * linear;
                }
                for (int f = 0; f < dz; f++)
                    row[k + f] = -Qz[r, f];
                row[K] = rhs;
            }
            for (int f = 0; f < dz; f++)
            {
                double[] row = augmented[k + f];
                Array.Clear(row);
                double rhs = -cs[f];
                for (int c = 0; c < m; c++)
                {
                    double weight = CP[f, c];
                    if (weight == 0)
                        continue;
                    double linear = q[c];
                    for (int p = 0; p < k; p++)
                    {
                        row[p] += weight * jacobian[c, p];
                        linear -= jacobian[c, p] * w[p];
                    }
                    rhs -= weight * linear;
                }
                row[K] = rhs;
            }
            SolveLinearStep(augmented, stepSolution, groups, groupRows, K);
            for (int f = 0; f < dz; f++)
                zNext[f] = stepSolution[k + f];

            // q linearized at w and evaluated at the next w, which is what full Newton solves with.
            for (int c = 0; c < m; c++)
            {
                double value = q[c];
                for (int p = 0; p < k; p++)
                    value += jacobian[c, p] * (stepSolution[p] - w[p]);
                linearizedQ[c] = value;
            }

            bool done = true;
            foreach (int j in convergence)
            {
                double next = Combine(j);
                done &= Math.Abs(next - y[j]) < Math.Abs(y[j]) * RelativeTolerance + AbsoluteTolerance;
            }

            done |= LinearOnly;
            Array.Copy(y, yBeforeUpdate, y.Length);
            for (int i = 0; i < y.Length; i++)
                y[i] = Combine(i);
            for (int p = 0; p < k; p++)
                w[p] = stepSolution[p];
            for (int f = 0; f < dz; f++)
                z[f] = zNext[f];

            if (done || iteration == iterationCap)
            {
                IterationCounts.Add((byte)(done ? iteration : iteration | CapFlag));
                if (SegmentObserver != null)
                    ObserveSegments(iteration, !done);
                return;
            }
        }
    }

    // Solves (I - Z·J)·x = b, one coupled port group at a time in dependency order. This is the same
    // linear solve as the joint one, so Newton's iterates and its stopping decisions don't change.
    // LiveSPICE's own elimination, in the SIMD form it uses when the hardware supports it. That form needs padded rows.
    private static readonly Action<double[][], int, int> Eliminate = System.Numerics.Vector.IsHardwareAccelerated
        ? (Action<double[][], int, int>)typeof(Simulation).GetMethod("SolveVector", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate(typeof(Action<double[][], int, int>))
        : Simulation.Solve;

    internal static int RowLength(int size) => size + 1 + (System.Numerics.Vector.IsHardwareAccelerated ? System.Numerics.Vector<double>.Count - 1 : 0);

    // Compiled steps call this every Newton iteration. Without AggressiveOptimization it starts at tier 0 and stays there
    // until tiering catches up, which made short runs through the compiled reduced path 25% slower on the TS-9.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal static void SolveLinearStep(double[][] augmented, double[] stepSolution, int[][] groups, double[][][] groupRows, int k)
    {
        if (groups.Length <= 1)
        {
            Eliminate(augmented, k, k + 1);
            for (int p = 0; p < k; p++)
                stepSolution[p] = augmented[p][k];
            return;
        }

        for (int g = 0; g < groups.Length; g++)
        {
            int[] group = groups[g];
            double[][] rows = groupRows[g];
            for (int r = 0; r < group.Length; r++)
            {
                double[] source = augmented[group[r]];
                double[] row = rows[r];
                double rhs = source[k];
                for (int earlier = 0; earlier < g; earlier++)
                    foreach (int p in groups[earlier])
                        rhs -= source[p] * stepSolution[p];
                for (int c = 0; c < group.Length; c++)
                    row[c] = source[group[c]];
                row[group.Length] = rhs;
            }

            Eliminate(rows, group.Length, group.Length + 1);
            for (int r = 0; r < group.Length; r++)
                stepSolution[group[r]] = rows[r][group.Length];
        }
    }

    private int[][] BuildGroups(bool segment)
    {
        if (k == 0)
            return Array.Empty<int[]>();
        if (!segment)
            return new[] { Enumerable.Range(0, k).ToArray() };
        return DependencyGraph.StronglyConnected(k, PortDependencies).Select(g => g.OrderBy(p => p).ToArray()).ToArray();
    }

    private bool coupledCleared;

    // Clears each Z entry that is roundoff relative to the largest in its column.
    private void ClearRoundoffCoupling()
    {
        coupledCleared = true;
        for (int c = 0; c < m; c++)
        {
            double max = 0;
            for (int r = 0; r < k; r++)
                max = Math.Max(max, Math.Abs(Z[r, c]));
            for (int r = 0; r < k; r++)
            {
                if (Z[r, c] != 0 && Math.Abs(Z[r, c]) <= max * CouplingTolerance)
                {
                    LargestDroppedCoupling = Math.Max(LargestDroppedCoupling, Math.Abs(Z[r, c]) / max);
                    Z[r, c] = 0;
                }
            }
        }
    }

    private IEnumerable<int> PortDependencies(int r)
    {
        for (int p = 0; p < k; p++)
            for (int c = 0; c < m; c++)
                if (Z[r, c] != 0 && dependsOn[c, p])
                {
                    yield return p;
                    break;
                }
    }

    // Ports come first, then one node per unknown with history. Reported only; delays are treated as edges.
    private int CountStateBlocks()
    {
        int[] states = bindings.Take(parameterCount).Where(b => b.Kind == Kind.History).Select(b => b.Unknown).Distinct().ToArray();
        int n = parameterGain.GetLength(0);

        bool Reads(int unknown, int state)
        {
            for (int j = 0; j < parameterCount; j++)
                if (bindings[j].Kind == Kind.History && bindings[j].Unknown == state && Significant(parameterGain, unknown, j))
                    return true;
            return false;
        }

        bool PortReads(int r, int state)
        {
            for (int j = 0; j < parameterCount; j++)
            {
                if (bindings[j].Kind != Kind.History || bindings[j].Unknown != state)
                    continue;
                double value = 0, scale = 0;
                for (int i = 0; i < n; i++)
                {
                    value += Q[r, i] * parameterGain[i, j];
                    scale += Math.Abs(Q[r, i] * parameterGain[i, j]);
                }
                if (Math.Abs(value) > scale * CouplingTolerance)
                    return true;
            }
            return false;
        }

        IEnumerable<int> Dependencies(int node)
        {
            if (node < k)
            {
                foreach (int p in PortDependencies(node))
                    yield return p;
                for (int s = 0; s < states.Length; s++)
                    if (PortReads(node, states[s]))
                        yield return k + s;
                yield break;
            }

            int unknown = states[node - k];
            for (int s = 0; s < states.Length; s++)
                if (Reads(unknown, states[s]))
                    yield return k + s;
            for (int p = 0; p < k; p++)
                for (int c = 0; c < m; c++)
                    if (dependsOn[c, p] && Significant(G, unknown, c))
                    {
                        yield return p;
                        break;
                    }
        }

        return DependencyGraph.StronglyConnected(k + states.Length, Dependencies).Count;
    }

    private static bool Significant(double[,] matrix, int row, int column)
    {
        double max = 0;
        for (int i = 0; i < matrix.GetLength(0); i++)
            max = Math.Max(max, Math.Abs(matrix[i, column]));
        return Math.Abs(matrix[row, column]) > max * CouplingTolerance;
    }

    /// <summary>Nonlinear terms no segment computes, because they read no ports. Black-box mode needs this to be zero.</summary>
    public int UnownedTerms { get; private set; }

    /// <summary>Candidate black boxes, in solve order.</summary>
    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>When set, receives every segment's inputs and outputs after each Newton solve.</summary>
    public ISegmentObserver? SegmentObserver { get; set; }

    /// <summary>When set, replaces Newton's method: each segment's outputs come from this solver instead.</summary>
    public ISegmentSolver? SegmentSolver { get; set; }

    /// <summary>Time spent solving for the unknowns (Newton or segment solvers), and in total, in Stopwatch ticks.</summary>
    public long SolveTicks { get; private set; }
    public long RunTicks { get; private set; }

    private int step;
    private double[][] segmentInputs = Array.Empty<double[]>();
    private double[][] segmentOutputs = Array.Empty<double[]>();

    private IReadOnlyList<Segment> BuildSegments(ExtractedSystem system)
    {
        var portNames = new string[k];
        for (int r = 0; r < k; r++)
            portNames[r] = string.Join(" + ", Enumerable.Range(0, system.Unknowns.Length)
                .Where(j => Q[r, j] != 0)
                .Select(j => Q[r, j] == 1 ? system.Unknowns[j].ToString() : $"{Q[r, j]:G6}*{system.Unknowns[j]}"));

        var segments = new List<Segment>();
        var solved = new HashSet<int>();
        foreach (int[] group in groups)
        {
            int[] ports = group.Where(i => i < k).ToArray();
            int[] freeIndices = group.Where(i => i >= k).Select(i => i - k).ToArray();
            // Each nonlinear term belongs to the last segment whose ports it reads.
            int[] terms = Enumerable.Range(0, m).Where(c => ports.Any(p => dependsOn[c, p]) && !LaterReads(c, group)).ToArray();

            // Upstream ports: those of earlier segments that this segment's equations read through Z and q.
            var upstream = new SortedSet<int>();
            foreach (int r in ports)
                for (int c = 0; c < m; c++)
                    if (Z[r, c] != 0)
                        for (int p = 0; p < k; p++)
                            if (dependsOn[c, p] && solved.Contains(p))
                                upstream.Add(p);
            foreach (int f in freeIndices)
                for (int c = 0; c < m; c++)
                    if (CP[f, c] != 0)
                        for (int p = 0; p < k; p++)
                            if (dependsOn[c, p] && solved.Contains(p))
                                upstream.Add(p);

            string FreeName(int f) => system.Unknowns[free[f]].ToString();
            segments.Add(new Segment
            {
                Index = segments.Count,
                Ports = ports,
                Free = freeIndices,
                Constraints = freeIndices,
                Upstream = upstream.ToArray(),
                Parameters = Enumerable.Range(0, system.RuntimeParameters.Length)
                    .Where(j => terms.Any(c => system.Nonlinear[c].DependsOn(system.RuntimeParameters[j].Symbol))).ToArray(),
                NonlinearTerms = terms,
                InputNames = ports.Select(p => $"open-circuit {portNames[p]}")
                    .Concat(freeIndices.Select(f => $"constraint {f} constant (free unknown {FreeName(f)})"))
                    .Concat(upstream.Select(p => $"upstream port {portNames[p]}"))
                    .Concat(Enumerable.Range(0, system.RuntimeParameters.Length)
                        .Where(j => terms.Any(c => system.Nonlinear[c].DependsOn(system.RuntimeParameters[j].Symbol)))
                        .Select(j => $"parameter {system.RuntimeParameters[j].Name}"))
                    .ToArray(),
                OutputNames = ports.Select(p => $"port {portNames[p]}")
                    .Concat(freeIndices.Select(f => $"free unknown {FreeName(f)}"))
                    .Concat(terms.Select(c => $"nonlinear term {c}"))
                    .ToArray(),
            });
            foreach (int p in ports)
                solved.Add(p);
        }

        bool LaterReads(int c, int[] group)
        {
            int index = Array.IndexOf(groups, group);
            for (int g = index + 1; g < groups.Length; g++)
                foreach (int p in groups[g])
                    if (p < k && dependsOn[c, p])
                        return true;
            return false;
        }

        UnownedTerms = m - segments.Sum(s => s.NonlinearTerms.Length);
        segmentInputs = segments.Select(s => new double[s.InputCount]).ToArray();
        segmentOutputs = segments.Select(s => new double[s.OutputCount]).ToArray();
        return segments;
    }

    // Reduced port position i depends on position j when one of its live Newton coefficients multiplies a term that
    // reads port N[j].
    private IEnumerable<int> ReducedDependencies(int i)
    {
        LinearPortReduction r = reduction!;
        for (int j = 0; j < r.N.Length; j++)
            for (int c = 0; c < r.TN.Length; c++)
                if (r.ZrUsed[i][c] && dependsOn[r.TN[c], r.N[j]])
                {
                    yield return j;
                    break;
                }
    }

    /// <summary>How far a pot's change must move a segment's Newton coefficients, relative to their largest entry, to
    /// count as one of its inputs. Below this it's roundoff.</summary>
    private const double ParameterChangeTolerance = 1e-12;

    // Segments on the reduced system: one per coupled group of reduced ports (and free unknowns), with global port and
    // term indices so they read and write the same arrays as the full system's. A pot is an input of a segment when
    // moving it changes the segment's Newton coefficients.
    private IReadOnlyList<Segment> BuildReducedSegments(ExtractedSystem system)
    {
        LinearPortReduction r = reduction!;
        int[] N = r.N, TN = r.TN;
        int kn = N.Length;
        var portNames = new string[k];
        for (int p = 0; p < k; p++)
            portNames[p] = string.Join(" + ", Enumerable.Range(0, system.Unknowns.Length)
                .Where(j => Q[p, j] != 0)
                .Select(j => Q[p, j] == 1 ? system.Unknowns[j].ToString() : $"{Q[p, j]:G6}*{system.Unknowns[j]}"));

        // Each pot's largest change to each Newton coefficient row, relative to that array's largest entry, moving it
        // towards either end of its travel with the others where they are.
        double[] current = (double[])ParameterValues.Clone();
        static double[][] Copy(double[][] m) => m.Select(row => (double[])row.Clone()).ToArray();
        static double Largest(double[][] m) => m.SelectMany(row => row).Select(Math.Abs).DefaultIfEmpty(0).Max();
        static double RowChange(double[] now, double[] then, double scale)
        {
            double worst = 0;
            for (int c = 0; c < now.Length; c++)
                worst = Math.Max(worst, Math.Abs(now[c] - then[c]));
            return scale > 0 ? worst / scale : 0;
        }
        double[][] zr0 = Copy(r.Zr), qzr0 = Copy(r.Qzr), cpr0 = Copy(r.CPr), czr0 = Copy(r.Czr);
        double zrScale = Largest(zr0), qzrScale = Largest(qzr0), cprScale = Largest(cpr0), czrScale = Largest(czr0);
        var portChange = new double[ParameterValues.Length, kn];
        var constraintChange = new double[ParameterValues.Length, dz];
        for (int j = 0; j < ParameterValues.Length; j++)
        {
            foreach (double end in new[] { -0.45, 0.45 })
            {
                current.CopyTo(ParameterValues, 0);
                ParameterValues[j] = end;
                r.Update(ParameterValues, LinearJacobian);
                for (int i = 0; i < kn; i++)
                    portChange[j, i] = Math.Max(portChange[j, i],
                        Math.Max(RowChange(r.Zr[i], zr0[i], zrScale), RowChange(r.Qzr[i], qzr0[i], qzrScale)));
                for (int f = 0; f < dz; f++)
                    constraintChange[j, f] = Math.Max(constraintChange[j, f],
                        Math.Max(RowChange(r.CPr[f], cpr0[f], cprScale), RowChange(r.Czr[f], czr0[f], czrScale)));
            }
        }
        current.CopyTo(ParameterValues, 0);
        r.Update(ParameterValues, LinearJacobian);

        var segments = new List<Segment>();
        var solved = new HashSet<int>();
        for (int g = 0; g < reducedGroups.Length; g++)
        {
            int[] group = reducedGroups[g];
            int[] positions = group.Where(i => i < kn).ToArray();
            int[] ports = positions.Select(i => N[i]).ToArray();
            int[] freeIndices = group.Where(i => i >= kn).Select(i => i - kn).ToArray();
            // Each nonlinear term belongs to the last segment whose ports it reads.
            int[] terms = TN.Where(c => ports.Any(p => dependsOn[c, p]) && !LaterReads(c, g)).ToArray();

            // Upstream ports: those of earlier segments that this segment's rows read through their terms.
            var upstream = new SortedSet<int>();
            foreach (int i in positions)
                for (int c = 0; c < TN.Length; c++)
                    if (r.ZrUsed[i][c])
                        foreach (int p in N)
                            if (dependsOn[TN[c], p] && solved.Contains(p))
                                upstream.Add(p);
            foreach (int f in freeIndices)
                for (int c = 0; c < TN.Length; c++)
                    if (r.CPrUsed[f][c])
                        foreach (int p in N)
                            if (dependsOn[TN[c], p] && solved.Contains(p))
                                upstream.Add(p);

            var parameters = new List<int>();
            var sensitivity = new List<double>();
            for (int j = 0; j < ParameterValues.Length; j++)
            {
                double change = 0;
                foreach (int i in positions)
                    change = Math.Max(change, portChange[j, i]);
                foreach (int f in freeIndices)
                    change = Math.Max(change, constraintChange[j, f]);
                bool readDirectly = terms.Any(c => system.Nonlinear[c].DependsOn(system.RuntimeParameters[j].Symbol));
                if (change > ParameterChangeTolerance || readDirectly)
                {
                    parameters.Add(j);
                    sensitivity.Add(readDirectly ? 1 : change);
                }
            }

            string FreeName(int f) => system.Unknowns[free[f]].ToString();
            segments.Add(new Segment
            {
                Index = segments.Count,
                Ports = ports,
                Free = freeIndices,
                Constraints = freeIndices,
                Upstream = upstream.ToArray(),
                Parameters = parameters.ToArray(),
                ParameterSensitivity = sensitivity.ToArray(),
                NonlinearTerms = terms,
                InputNames = ports.Select(p => $"open-circuit {portNames[p]}")
                    .Concat(freeIndices.Select(f => $"constraint {f} constant (free unknown {FreeName(f)})"))
                    .Concat(upstream.Select(p => $"upstream port {portNames[p]}"))
                    .Concat(parameters.Select(j => $"parameter {system.RuntimeParameters[j].Name}"))
                    .ToArray(),
                OutputNames = ports.Select(p => $"port {portNames[p]}")
                    .Concat(freeIndices.Select(f => $"free unknown {FreeName(f)}"))
                    .Concat(terms.Select(c => $"nonlinear term {c}"))
                    .ToArray(),
            });
            foreach (int p in ports)
                solved.Add(p);
        }

        bool LaterReads(int c, int g)
        {
            for (int later = g + 1; later < reducedGroups.Length; later++)
                foreach (int i in reducedGroups[later])
                    if (i < kn && dependsOn[c, N[i]])
                        return true;
            return false;
        }

        UnownedTerms = TN.Length - segments.Sum(s => s.NonlinearTerms.Length);
        segmentInputs = segments.Select(s => new double[s.InputCount]).ToArray();
        segmentOutputs = segments.Select(s => new double[s.OutputCount]).ToArray();
        return segments;
    }

    private void FillInputs(Segment segment, double[] inputs)
    {
        int position = 0;
        foreach (int p in segment.Ports)
            inputs[position++] = voc[p];
        foreach (int f in segment.Constraints)
            inputs[position++] = reduction != null ? reducedConstant[f] : cs[f];
        foreach (int p in segment.Upstream)
            inputs[position++] = w[p];
        foreach (int j in segment.Parameters)
            inputs[position++] = ParameterValues[j];
    }

    private void ObserveSegments(int iterations, bool capped)
    {
        foreach (Segment segment in Segments)
        {
            double[] inputs = segmentInputs[segment.Index], outputs = segmentOutputs[segment.Index];
            FillInputs(segment, inputs);
            int position = 0;
            foreach (int p in segment.Ports)
                outputs[position++] = w[p];
            foreach (int f in segment.Free)
                outputs[position++] = z[f];
            foreach (int c in segment.NonlinearTerms)
                outputs[position++] = linearizedQ[c];
            SegmentObserver!.Observe(step, segment, inputs, outputs, iterations, capped);
        }
        step++;
    }

    // Black-box mode: each segment's solver gives its ports, free unknowns and nonlinear-term values, in solve order,
    // and every unknown then follows from y = yLinear + G·q + Nz·z. LiveSPICE's post-Newton evaluation doesn't apply.
    private void SolveWithSegments()
    {
        if (UnownedTerms > 0)
            throw new NotSupportedException("Some nonlinear terms read no ports, so no segment computes them.");
        if (reduction != null)
            PrepareReduced();
        else
            Multiply(Q, yLinear, voc);
        foreach (Segment segment in Segments)
        {
            double[] inputs = segmentInputs[segment.Index], outputs = segmentOutputs[segment.Index];
            FillInputs(segment, inputs);
            SegmentSolver!.Solve(segment, inputs, outputs);
            int position = 0;
            foreach (int p in segment.Ports)
                w[p] = outputs[position++];
            foreach (int f in segment.Free)
                z[f] = outputs[position++];
            foreach (int c in segment.NonlinearTerms)
                linearizedQ[c] = outputs[position++];
        }
        Array.Copy(z, zNext, dz);
        for (int i = 0; i < y.Length; i++)
            y[i] = reduction != null ? CombineReduced(i) : Combine(i);
        IterationCounts.Add(0);
        step++;
    }

    /// <summary>
    /// By default every unknown follows from the solve, y = yLinear + G·q + Nz·z, with q the nonlinear-term values
    /// Newton settled on. That is also how black-box mode works. (Rebuilding from q, not from the ports through q(w),
    /// matters: in high-gain circuits G is large, and q(w) at a converged but inexact w is not accurate enough.)
    /// When set, the unknowns outside LiveSPICE's Newton system are instead computed the way LiveSPICE's compiled code
    /// does, reusing subexpressions cached before the last Newton update. That reproduces LiveSPICE's output sample for
    /// sample, and its error: against a tightly converged reference, it is up to tens of dB less accurate on some
    /// circuits. See LiveSPICE pull request 284.
    /// </summary>
    public bool MatchLiveSpice { get; }

    /// <summary>Newton's stopping rule: |dv| < |v|·RelativeTolerance + AbsoluteTolerance. The defaults are LiveSPICE's.</summary>
    public double RelativeTolerance { get; set; } = 1e-4;
    public double AbsoluteTolerance { get; set; } = 1e-6;


    /// <summary>
    /// Solves one segment on its own, to tight tolerance, for the given inputs (as laid out in
    /// <see cref="Segment.InputNames"/>), starting Newton from <paramref name="guess"/> (laid out as its outputs).
    /// Writes the outputs, with the nonlinear-term values evaluated exactly at the solution. Returns whether it
    /// converged. This uses the interpreter's working state, so don't call it while the interpreter is running.
    /// </summary>
    public bool SolveSegment(Segment segment, ReadOnlySpan<double> inputs, Span<double> outputs, ReadOnlySpan<double> guess,
        double tolerance = 1e-12, int maxIterations = 100)
    {
        int ports = segment.Ports.Length, frees = segment.Free.Length, size = ports + frees;
        var open = new double[ports];
        var constant = new double[frees];
        int position = 0;
        for (int i = 0; i < ports; i++)
            open[i] = inputs[position++];
        for (int f = 0; f < frees; f++)
            constant[f] = inputs[position++];
        foreach (int p in segment.Upstream)
            w[p] = inputs[position++];
        foreach (int j in segment.Parameters)
            ParameterValues[j] = inputs[position++];
        reduction?.Update(ParameterValues, LinearJacobian);
        int[] terms = reduction?.TN ?? Enumerable.Range(0, m).ToArray();
        int[] rowOf = segment.Ports.Select(p => reduction != null ? Array.IndexOf(reduction.N, p) : p).ToArray();

        for (int i = 0; i < ports; i++)
            w[segment.Ports[i]] = guess[i];
        var freeValues = new double[frees];
        for (int f = 0; f < frees; f++)
            freeValues[f] = guess[ports + f];

        var rows = Enumerable.Range(0, size).Select(_ => new double[size + 1]).ToArray();
        bool converged = false;
        for (int iteration = 0; iteration < maxIterations && !converged; iteration++)
        {
            EvaluateNonlinear();
            for (int i = 0; i < ports; i++)
            {
                int r = segment.Ports[i];
                double[] row = rows[i];
                Array.Clear(row);
                double residual = w[r] - open[i];
                row[i] = 1;
                // On the reduced path the coefficients are the folded ones, for the reduced terms.
                for (int t = 0; t < terms.Length; t++)
                {
                    int c = terms[t];
                    double zc = reduction != null ? reduction.Zr[rowOf[i]][t] : Z[r, c];
                    if (zc == 0)
                        continue;
                    residual -= zc * q[c];
                    for (int j = 0; j < ports; j++)
                        row[j] -= zc * jacobian[c, segment.Ports[j]];
                }
                for (int f = 0; f < frees; f++)
                {
                    double coupling = reduction != null ? reduction.Qzr[rowOf[i]][segment.Free[f]] : Qz[r, segment.Free[f]];
                    residual -= coupling * freeValues[f];
                    row[ports + f] = -coupling;
                }
                row[size] = -residual;
            }
            for (int f = 0; f < frees; f++)
            {
                int constraint = segment.Constraints[f];
                double[] row = rows[ports + f];
                Array.Clear(row);
                double residual = constant[f];
                for (int t = 0; t < terms.Length; t++)
                {
                    int c = terms[t];
                    double weight = reduction != null ? reduction.CPr[constraint][t] : CP[constraint, c];
                    if (weight == 0)
                        continue;
                    residual += weight * q[c];
                    for (int j = 0; j < ports; j++)
                        row[j] += weight * jacobian[c, segment.Ports[j]];
                }
                // The reduced constraints can also read the free unknowns directly (Czr).
                if (reduction != null)
                    for (int h = 0; h < frees; h++)
                    {
                        double coupling = reduction.Czr[constraint][segment.Free[h]];
                        residual += coupling * freeValues[h];
                        row[ports + h] += coupling;
                    }
                row[size] = -residual;
            }

            Simulation.Solve(rows, size, size + 1);
            converged = true;
            for (int j = 0; j < size; j++)
            {
                double delta = rows[j][size];
                if (double.IsNaN(delta) || double.IsInfinity(delta))
                    return false;
                if (j < ports)
                {
                    int p = segment.Ports[j];
                    converged &= Math.Abs(delta) <= tolerance * (1 + Math.Abs(w[p]));
                    w[p] += delta;
                }
                else
                {
                    converged &= Math.Abs(delta) <= tolerance * (1 + Math.Abs(freeValues[j - ports]));
                    freeValues[j - ports] += delta;
                }
            }
        }

        EvaluateNonlinear();
        position = 0;
        for (int i = 0; i < ports; i++)
            outputs[position++] = w[segment.Ports[i]];
        for (int f = 0; f < frees; f++)
            outputs[position++] = freeValues[f];
        foreach (int c in segment.NonlinearTerms)
            outputs[position++] = q[c];
        return converged;
    }
    private LinearPortReduction? BuildReduction(bool clearIfNothingToEliminate)
    {
        bool[] linear = (bool[])termLinear.Clone();
        bool changed = true;
        bool[] inN = new bool[k];
        while (changed)
        {
            changed = false;
            Array.Clear(inN);
            for (int c = 0; c < m; c++)
                if (!linear[c])
                    for (int p = 0; p < k; p++)
                        inN[p] |= dependsOn[c, p];
            // A linear term that reads a nonlinear port stays with the nonlinear terms.
            for (int c = 0; c < m; c++)
                if (linear[c] && Enumerable.Range(0, k).Any(p => dependsOn[c, p] && inN[p]))
                {
                    linear[c] = false;
                    changed = true;
                }
        }

        int[] n = Enumerable.Range(0, k).Where(p => inN[p]).ToArray();
        int[] l = Enumerable.Range(0, k).Where(p => !inN[p]).ToArray();
        if (l.Length == 0 && NormalPathForTesting)
            return null;
        if (l.Length == 0 && clearIfNothingToEliminate)
            ClearRoundoffCoupling();
        int[] tn = Enumerable.Range(0, m).Where(c => !linear[c]).ToArray();
        int[] tl = Enumerable.Range(0, m).Where(c => linear[c]).ToArray();
        // Its own copy of Z: clearing Z's roundoff entries later, for the full system's port groups, mustn't change the
        // coefficients it folds whenever a pot moves.
        return new LinearPortReduction(n, l, tn, tl, (double[,])Z.Clone(), Qz, CP, Q, parameterGain, constant, sourceGain, G, Nz, CH, CC, CS);
    }

    /// <summary>Recomputes the reduced system if the runtime parameters changed. Compiled code calls this once per step.</summary>
    internal void UpdateReduction() => reduction!.Update(ParameterValues, LinearJacobian);

    private double[,] LinearJacobian()
    {
        EvaluateNonlinear();
        for (int t = 0; t < reduction!.TL.Length; t++)
            for (int j = 0; j < reduction.L.Length; j++)
                linearJacobian[t, j] = jacobian[reduction.TL[t], reduction.L[j]];
        return linearJacobian;
    }

    // Newton on the ports nonlinear terms read (and the free unknowns), with the linear ports folded into the
    // coefficients (see LinearPortReduction). Run recomputes those coefficients when a parameter has changed.
    // The reduced system's inputs for this step: the unknowns' linear parts with the eliminated ports folded in (for
    // the unknowns they reach), the constraint constants, and the ports' open-circuit values.
    private void PrepareReduced()
    {
        LinearPortReduction r = reduction!;
        for (int i = 0; i < y.Length; i++)
        {
            if (!r.Affected[i])
                continue;
            double value = r.Constant[i];
            for (int j = 0; j < parameterCount; j++)
                value += r.Gain[i][j] * values[j];
            for (int x = 0; x < sourceValues.Length; x++)
                value += r.Source[i][x] * sourceCoefficients[x] * sourceValues[x];
            yLinear[i] = value;
        }
        for (int f = 0; f < dz; f++)
        {
            double value = r.ConstraintConstant[f];
            for (int j = 0; j < parameterCount; j++)
                value += r.ConstraintGain[f][j] * values[j];
            for (int x = 0; x < sourceValues.Length; x++)
                value += r.ConstraintSource[f][x] * sourceCoefficients[x] * sourceValues[x];
            reducedConstant[f] = value;
        }
        Multiply(Q, yLinear, voc);
    }

    private void SolveReduced()
    {
        LinearPortReduction r = reduction!;
        int[] N = r.N, TN = r.TN;
        int kn = N.Length, size = kn + dz;
        PrepareReduced();

        double[] previous = history[0];
        Array.Copy(previous, y, y.Length);
        Multiply(Q, previous, w);
        for (int f = 0; f < dz; f++)
            z[f] = previous[free[f]];

        for (int iteration = 1; ; iteration++)
        {
            if (TN.Length > 0)
                EvaluateNonlinear();

            for (int i = 0; i < kn; i++)
            {
                double[] row = reducedRows[i];
                Array.Clear(row);
                row[i] = 1;
                double rhs = voc[N[i]];
                for (int c = 0; c < TN.Length; c++)
                {
                    double zc = r.Zr[i][c];
                    if (zc == 0)
                        continue;
                    int term = TN[c];
                    double linear = q[term];
                    for (int j = 0; j < kn; j++)
                    {
                        double slope = jacobian[term, N[j]];
                        row[j] -= zc * slope;
                        linear -= slope * w[N[j]];
                    }
                    rhs += zc * linear;
                }
                for (int g = 0; g < dz; g++)
                    row[kn + g] = -r.Qzr[i][g];
                row[size] = rhs;
            }
            for (int f = 0; f < dz; f++)
            {
                double[] row = reducedRows[kn + f];
                Array.Clear(row);
                double rhs = -reducedConstant[f];
                for (int c = 0; c < TN.Length; c++)
                {
                    double weight = r.CPr[f][c];
                    if (weight == 0)
                        continue;
                    int term = TN[c];
                    double linear = q[term];
                    for (int j = 0; j < kn; j++)
                    {
                        double slope = jacobian[term, N[j]];
                        row[j] += weight * slope;
                        linear -= slope * w[N[j]];
                    }
                    rhs -= weight * linear;
                }
                for (int g = 0; g < dz; g++)
                    row[kn + g] = r.Czr[f][g];
                row[size] = rhs;
            }
            if (size > 0)
                SolveLinearStep(reducedRows, reducedSolution, reducedGroups, reducedGroupRows, size);

            for (int c = 0; c < TN.Length; c++)
            {
                int term = TN[c];
                double value = q[term];
                for (int j = 0; j < kn; j++)
                    value += jacobian[term, N[j]] * (reducedSolution[j] - w[N[j]]);
                linearizedQ[term] = value;
            }
            for (int g = 0; g < dz; g++)
                zNext[g] = reducedSolution[kn + g];

            bool done = true;
            foreach (int j in convergence)
            {
                double next = CombineReduced(j);
                done &= Math.Abs(next - y[j]) < Math.Abs(y[j]) * RelativeTolerance + AbsoluteTolerance;
            }
            if (TN.Length == 0)
                done = true;

            Array.Copy(y, yBeforeUpdate, y.Length);
            for (int i = 0; i < y.Length; i++)
                y[i] = CombineReduced(i);
            for (int j = 0; j < kn; j++)
                w[N[j]] = reducedSolution[j];
            for (int g = 0; g < dz; g++)
                z[g] = zNext[g];

            if (done || iteration == iterationCap)
            {
                IterationCounts.Add((byte)(done ? iteration : iteration | CapFlag));
                if (SegmentObserver != null)
                    ObserveSegments(iteration, !done);
                return;
            }
        }
    }

    // An unknown from the reduced solve: y = yEff + Geff·qN + Nzeff·z, with yEff already in yLinear.
    private double CombineReduced(int i)
    {
        LinearPortReduction r = reduction!;
        double value = yLinear[i];
        if (r.Affected[i])
        {
            for (int c = 0; c < r.TN.Length; c++)
                value += r.TermGain[i][c] * linearizedQ[r.TN[c]];
            for (int g = 0; g < dz; g++)
                value += r.FreeGain[i][g] * zNext[g];
        }
        else
        {
            for (int c = 0; c < r.TN.Length; c++)
                value += G[i, r.TN[c]] * linearizedQ[r.TN[c]];
            for (int g = 0; g < dz; g++)
                value += Nz[i, g] * zNext[g];
        }
        return value;
    }

    private double Combine(int i)
    {
        double value = yLinear[i];
        for (int c = 0; c < m; c++)
            value += G[i, c] * linearizedQ[c];
        for (int f = 0; f < dz; f++)
            value += Nz[i, f] * zNext[f];
        return value;
    }

    private void EvaluateNonlinear()
    {
        for (int p = 0; p < k; p++)
            if (portSymbols[p] >= 0)
                nonlinearInputs[portSymbols[p]] = w[p];

        for (int j = 0; j < parameterSymbols.Length; j++)
            nonlinearInputs[parameterSymbols[j]] = ParameterValues[j];

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

    // Q is in reduced row echelon form, so each row has a pivot that is the only nonzero in its column.
    internal static int[] PortPivots(ExtractedSystem system)
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

    internal static Binding Bind(Expression symbol, Expression[] unknowns, Expression input, Expression h)
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
