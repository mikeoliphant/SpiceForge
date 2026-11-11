using System.Linq.Expressions;
using System.Reflection;
using Circuit;
using ComputerAlgebra;
using ComputerAlgebra.LinqCompiler;
using Expression = ComputerAlgebra.Expression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace SpiceForge;

/// <summary>
/// The same schedule as <see cref="CircuitInterpreter"/>, compiled to one LINQ function per timestep.
/// Coefficients are read from runtime arrays so they can be updated without recompiling.
/// </summary>
public sealed class CompiledCircuit
{
    private delegate int StepFunction(double time, double input, double inputPrevious, double[][] history, double[] y);

    private readonly StepFunction step;
    private readonly CircuitInterpreter plan;
    private readonly bool reduce;
    private readonly int[] outputs;
    private readonly double substep;
    private readonly bool newton;
    private readonly double[] y;
    private readonly double[][] history;
    private double time;
    private double input;
    private double inputPrevious;

    /// <summary>One entry per oversampled step: the Newton iteration count, with 0x80 set if the cap was hit. Recorded
    /// only while <see cref="RecordIterations"/> is set.</summary>
    public List<byte> IterationCounts { get; } = new();

    /// <summary>Whether to record <see cref="IterationCounts"/>. On by default for validation; a long-running host
    /// should turn it off, since it grows by one byte per oversampled step.</summary>
    public bool RecordIterations { get; set; } = true;

    private Oversampler oversampler;
    // The step-rate input and outputs of the current block, grown as needed.
    private double[] stepInput = Array.Empty<double>();
    private double[][] stepOutputs;

    /// <summary>How <see cref="Run(double[], double[][], int)"/> converts between the base rate and the step rate. By
    /// default LiveSPICE's (<see cref="LinearOversampler"/>); a replacement must have the same factor the circuit was
    /// built for, since that decides the timestep.</summary>
    public Oversampler Oversampler
    {
        get => oversampler;
        set
        {
            if (value.Factor != oversampler.Factor)
                throw new ArgumentException($"The circuit steps {oversampler.Factor} times per sample, not {value.Factor}.", nameof(value));
            oversampler = value;
        }
    }

    /// <summary>Current values of the runtime parameters (dynamic pots), in the order of ExtractedSystem.RuntimeParameters.
    /// The compiled code reads this array directly, so changes take effect on the next step without recompiling.</summary>
    public double[] ParameterValues { get; }

    /// <summary>For profiling only: compile live coefficients as constants at their current values, so later
    /// parameter changes have no effect.</summary>
    internal static bool InlineLiveForTesting { get; set; }

    /// <summary>For profiling only: the expression tree of the most recently compiled step function.</summary>
    internal static System.Linq.Expressions.LambdaExpression? LastStepForTesting { get; set; }

    public CompiledCircuit(
        ExtractedSystem system,
        Expression inputSymbol,
        IReadOnlyList<Expression> outputSymbols,
        Expression timeStep,
        int oversample,
        int iterationCap,
        TransientSolution solution,
        ISegmentSolver? segmentSolver = null)
    {
        var plan = new CircuitInterpreter(system, inputSymbol, outputSymbols, timeStep, oversample, iterationCap, solution);
        outputs = plan.outputs;
        substep = plan.substep;
        oversampler = new LinearOversampler(oversample);
        stepOutputs = outputs.Select(_ => Array.Empty<double>()).ToArray();
        newton = plan.m > 0;
        if (segmentSolver != null && plan.UnownedTerms > 0)
            throw new NotSupportedException("Some nonlinear terms read no ports, so no segment computes them.");
        y = new double[system.Unknowns.Length];
        history = plan.history;
        ParameterValues = plan.ParameterValues;
        this.plan = plan;
        reduce = plan.reduction != null;
        step = new Emitter(plan, system, inputSymbol, timeStep, iterationCap, solution, segmentSolver).Build();
    }

    public void Run(double[] inputSamples, double[][] outputBuffers) => Run(inputSamples, outputBuffers, inputSamples.Length);

    /// <summary>Runs the first <paramref name="count"/> samples of <paramref name="inputSamples"/>, writing the first
    /// <paramref name="count"/> samples of each output buffer: upsampled by <see cref="Oversampler"/>, stepped, and
    /// downsampled again. It allocates nothing once its buffers have grown to the largest block.</summary>
    public void Run(double[] inputSamples, double[][] outputBuffers, int count)
    {
        int steps = count * oversampler.Factor;
        if (stepInput.Length < steps)
            GrowStepBuffers(steps);
        oversampler.Upsample(inputSamples.AsSpan(0, count), stepInput.AsSpan(0, steps));
        RunSteps(stepInput, stepOutputs, steps);
        for (int o = 0; o < outputs.Length; o++)
            oversampler.Downsample(o, stepOutputs[o].AsSpan(0, steps), outputBuffers[o].AsSpan(0, count));
    }

    private void GrowStepBuffers(int steps)
    {
        stepInput = new double[steps];
        for (int o = 0; o < stepOutputs.Length; o++)
            stepOutputs[o] = new double[steps];
    }

    /// <summary>Takes <paramref name="count"/> timesteps, one per value of <paramref name="inputSteps"/>, writing each
    /// output's value at every step (0 for an output that isn't an unknown). It allocates nothing.</summary>
    public void RunSteps(double[] inputSteps, double[][] outputSteps, int count)
    {
        // Runtime parameters only change between calls, so the reduced coefficients only need checking here.
        if (reduce)
            plan.UpdateReduction();
        for (int n = 0; n < count; n++)
        {
            time += substep;
            input = inputSteps[n];
            int result = step(time, input, inputPrevious, history, y);
            if (newton && RecordIterations)
                IterationCounts.Add((byte)result);

            double[] oldest = history[history.Length - 1];
            for (int d = history.Length - 1; d > 0; d--)
                history[d] = history[d - 1];
            Array.Copy(y, oldest, y.Length);
            history[0] = oldest;

            for (int o = 0; o < outputs.Length; o++)
                outputSteps[o][n] = outputs[o] >= 0 ? y[outputs[o]] : 0;

            inputPrevious = input;
        }
    }

    private sealed class Emitter
    {
        private static readonly MethodInfo SolveStep = typeof(CircuitInterpreter).GetMethod(
            nameof(CircuitInterpreter.SolveLinearStep), BindingFlags.Static | BindingFlags.NonPublic)!;
        private static readonly MethodInfo Abs = typeof(Math).GetMethod(nameof(Math.Abs), new[] { typeof(double) })!;

        private readonly CircuitInterpreter plan;
        private readonly ExtractedSystem system;
        private readonly Expression inputSymbol;
        private readonly Expression timeStep;
        private readonly int iterationCap;
        private readonly TransientSolution solution;
        private readonly ISegmentSolver? segmentSolver;
        private readonly CodeGen code = new();
        private readonly double[][] parameterGain, sourceGain, G, Z, Q, D, Nz, Qz, CH, CS, CP;
        private ParameterExpression time = null!, input = null!, inputPrevious = null!, history = null!, y = null!;
        private ParameterExpression[] historyRows = Array.Empty<ParameterExpression>();
        private readonly HashSet<int> required = new();
        private SortedSet<int> linearRows = new();
        private readonly int[] outputs;

        public Emitter(CircuitInterpreter plan, ExtractedSystem system, Expression inputSymbol, Expression timeStep, int iterationCap, TransientSolution solution, ISegmentSolver? segmentSolver)
        {
            this.segmentSolver = segmentSolver;
            this.plan = plan;
            outputs = plan.outputs;
            this.system = system;
            this.inputSymbol = inputSymbol;
            this.timeStep = timeStep;
            this.iterationCap = iterationCap;
            this.solution = solution;
            parameterGain = Jagged(plan.parameterGain);
            sourceGain = Jagged(plan.sourceGain);
            G = Jagged(plan.G);
            Z = Jagged(plan.Z);
            Q = Jagged(plan.Q);
            D = Jagged(plan.D);
            Nz = Jagged(plan.Nz);
            Qz = Jagged(plan.Qz);
            CH = Jagged(plan.CH);
            CS = Jagged(plan.CS);
            CP = Jagged(plan.CP);
        }

        public StepFunction Build()
        {
            time = code.Decl<double>(Scope.Parameter, "time");
            input = code.Decl<double>(Scope.Parameter, "input");
            inputPrevious = code.Decl<double>(Scope.Parameter, "inputPrevious");
            history = code.Decl<double[][]>(Scope.Parameter, "history");
            y = code.Decl<double[]>(Scope.Parameter, "y");

            historyRows = new ParameterExpression[plan.history.Length];
            for (int d = 0; d < historyRows.Length; d++)
            {
                historyRows[d] = code.Decl<double[]>("history" + d);
                code.Add(LinqExpr.Assign(historyRows[d], LinqExpr.ArrayIndex(history, LinqExpr.Constant(d))));
            }

            ComputeRequired();

            // Only read the parameters the remaining equations use.
            var sourceSymbols = new HashSet<Expression>(system.Sources.SelectMany(s => SymbolScan.Collect(s.Body)));
            for (int i = 0; i < plan.Symbols.Length; i++)
            {
                bool used = i >= plan.parameterCount || sourceSymbols.Contains(plan.Symbols[i])
                    || linearRows.Any(row => parameterGain[row][i] != 0)
                    || (i < plan.parameterCount && Enumerable.Range(0, plan.dz).Any(f => CH[f][i] != 0));
                if (!used)
                    continue;
                // History values are read from their arrays where they're used, not loaded into locals. The step has
                // nearly as many of them as x64 has XMM registers, and as locals they're all live through the linear
                // parts, leaving the JIT to spill each running sum to one stack slot on every term. A multiply can
                // read its operand straight from memory, so this costs nothing.
                if (plan.bindings[i].Kind == CircuitInterpreter.Kind.History)
                    code.Map(Scope.Local, plan.Symbols[i], Read(plan.bindings[i]));
                else
                    code.DeclInit(plan.Symbols[i], Read(plan.bindings[i]));
            }

            ParameterExpression[] sources = system.Sources
                .Select(s => Local(ToDouble(code.Compile(s.Body))))
                .ToArray();

            int n = system.Unknowns.Length;
            if (plan.m == 0)
            {
                foreach (int i in required)
                    code.Add(LinqExpr.Assign(Element(y, i), LinearPart(i, sources)));
                code.Return(LinqExpr.Constant(0));
            }
            else
            {
                EmitReduced(sources);
            }

            var lambda = code.Build<StepFunction>();
            LastStepForTesting = lambda;
            return lambda.Compile();
        }

        // Finds the unknowns anything reads: outputs and state, then whatever computing those needs.
        private void ComputeRequired()
        {
            int n = system.Unknowns.Length;
            var rows = new SortedSet<int>();
            var work = new Stack<int>();
            void Require(int unknown)
            {
                if (unknown >= 0 && required.Add(unknown))
                    work.Push(unknown);
            }

            foreach (int output in outputs)
                Require(output);

            if (plan.m > 0)
            {
                // The reduced step iterates only the ports nonlinear terms read; the eliminated ones (pot legs, say)
                // are computed only if something reads them.
                foreach (int j in plan.free)
                    Require(j);
                foreach (int r in plan.reduction!.N)
                    for (int j = 0; j < n; j++)
                        if (Q[r][j] != 0)
                        {
                            Require(j);
                            rows.Add(j);
                        }
            }

            while (work.Count > 0)
            {
                int i = work.Pop();
                rows.Add(i);

                if (rows.Contains(i))
                    for (int j = 0; j < plan.parameterCount; j++)
                        if (parameterGain[i][j] != 0 && plan.bindings[j].Kind == CircuitInterpreter.Kind.History)
                            Require(plan.bindings[j].Unknown);

            }

            // Rows added late can bring in more state.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (int i in rows.ToArray())
                    for (int j = 0; j < plan.parameterCount; j++)
                        if (parameterGain[i][j] != 0 && plan.bindings[j].Kind == CircuitInterpreter.Kind.History && required.Add(plan.bindings[j].Unknown))
                        {
                            changed = true;
                            rows.Add(plan.bindings[j].Unknown);
                        }
            }

            linearRows = rows;
        }

        private LinqExpr LinearPart(int i, ParameterExpression[] sources)
        {
            LinqExpr value = LinqExpr.Constant(plan.constant[i]);
            for (int j = 0; j < plan.parameterCount; j++)
                if (parameterGain[i][j] != 0)
                    value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(parameterGain, i, j), code[plan.Symbols[j]]));
            for (int x = 0; x < sources.Length; x++)
                if (sourceGain[i][x] != 0)
                    value = LinqExpr.Add(value, LinqExpr.Multiply(
                        LinqExpr.Multiply(Cell(sourceGain, i, x), LinqExpr.Constant(plan.sourceCoefficients[x])),
                        sources[x]));
            return value;
        }

        // A runtime parameter reads the live parameter array; anything else is null.
        private LinqExpr? ParameterRead(Expression symbol)
        {
            int index = Array.FindIndex(system.RuntimeParameters, r => r.Symbol.Equals(symbol));
            return index < 0 ? null : Live(plan.ParameterValues, index);
        }

        private static readonly MethodInfo UpdateReduction = typeof(CircuitInterpreter).GetMethod(
            nameof(CircuitInterpreter.UpdateReduction), BindingFlags.Instance | BindingFlags.NonPublic)!;

        // An array constant in a compiled lambda is a closure lookup and a cast on every use, so each live array, and each
        // row of it, is loaded into a local once per step and reused. Rows are cached on first use: call Hoist for the ones
        // used inside the Newton loop before emitting it, so their locals are assigned outside it.
        private readonly Dictionary<object, ParameterExpression> liveArrays = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(object, int), ParameterExpression> liveRows = new();

        private ParameterExpression LiveArray<T>(T array) where T : class
        {
            if (!liveArrays.TryGetValue(array, out ParameterExpression? local))
            {
                local = code.Decl<T>("live" + liveArrays.Count);
                code.Add(LinqExpr.Assign(local, LinqExpr.Constant(array)));
                liveArrays[array] = local;
            }
            return local;
        }

        private ParameterExpression LiveRow(double[][] matrix, int row)
        {
            if (!liveRows.TryGetValue((matrix, row), out ParameterExpression? local))
            {
                local = code.Decl<double[]>("liveRow" + liveRows.Count);
                code.Add(LinqExpr.Assign(local, LinqExpr.ArrayIndex(LiveArray(matrix), LinqExpr.Constant(row))));
                liveRows[(matrix, row)] = local;
            }
            return local;
        }

        private void Hoist(double[][] matrix)
        {
            if (InlineLiveForTesting) return;
            for (int row = 0; row < matrix.Length; row++)
                if (plan.reduction!.RowVaries(matrix, row))
                    LiveRow(matrix, row);
        }

        // A coefficient of the reduction: read from its live array if it depends on the parameters, and compiled as a
        // constant if it doesn't (all of them, in a circuit without dynamic pots).
        private LinqExpr Live(double[][] matrix, int row, int column) =>
            InlineLiveForTesting || !plan.reduction!.Varies(matrix, row, column)
                ? LinqExpr.Constant(matrix[row][column])
                : LinqExpr.ArrayIndex(LiveRow(matrix, row), LinqExpr.Constant(column));

        private LinqExpr Live(double[] vector, int index) => InlineLiveForTesting
            ? LinqExpr.Constant(vector[index])
            : LinqExpr.ArrayIndex(LiveArray(vector), LinqExpr.Constant(index));

        // The reduced Newton solve, as CircuitInterpreter.SolveReduced: Newton on the ports nonlinear terms read and the
        // free unknowns, with the linear ports folded into the coefficients whenever a parameter changes. The step has
        // the same structure as the static circuit's; unknowns a linear term reaches read their coefficients (only the
        // ones that can be nonzero) from live arrays, and everything else keeps its inlined constants.
        private void EmitReduced(ParameterExpression[] sources)
        {
            LinearPortReduction r = plan.reduction!;
            int n = system.Unknowns.Length, m = plan.m, dz = plan.dz;
            int[] N = r.N, TN = r.TN;
            int kn = N.Length, size = kn + dz;
            int[] tracked = plan.convergence.Distinct().ToArray();

            // Before the loop, only the linear parts the loop reads: the unknowns the stopping rule tracks and those the
            // ports read. The rest are computed after the loop, where they're used. Computing them
            // all up front keeps every one live across the loop, which costs register spills on every term.
            var needed = new SortedSet<int>(tracked);
            foreach (int p in N)
                for (int j = 0; j < n; j++)
                    if (Q[p][j] != 0)
                        needed.Add(j);
            var linear = new Dictionary<int, ParameterExpression>();
            foreach (int i in needed)
                linear[i] = Local(r.Affected[i] ? LiveLinearPart(i, sources, r) : LinearPart(i, sources));
            LinqExpr LinearOf(int i) => linear.TryGetValue(i, out ParameterExpression? local) ? local
                : r.Affected[i] ? LiveLinearPart(i, sources, r) : LinearPart(i, sources);

            var open = new ParameterExpression[kn];
            for (int i = 0; i < kn; i++)
            {
                LinqExpr sum = LinqExpr.Constant(0.0);
                for (int j = 0; j < n; j++)
                    if (Q[N[i]][j] != 0)
                        sum = LinqExpr.Add(sum, LinqExpr.Multiply(Cell(Q, N[i], j), linear[j]));
                open[i] = Local(sum);
            }

            var constant = new ParameterExpression[dz];
            for (int f = 0; f < dz; f++)
            {
                LinqExpr value = Live(r.ConstraintConstant, f);
                for (int j = 0; j < plan.parameterCount; j++)
                    if (r.ConstraintGainUsed[f][j])
                        value = LinqExpr.Add(value, LinqExpr.Multiply(Live(r.ConstraintGain, f, j), code[plan.Symbols[j]]));
                for (int x = 0; x < sources.Length; x++)
                    if (r.ConstraintSourceUsed[f][x])
                        value = LinqExpr.Add(value, LinqExpr.Multiply(
                            LinqExpr.Multiply(Live(r.ConstraintSource, f, x), LinqExpr.Constant(plan.sourceCoefficients[x])), sources[x]));
                constant[f] = Local(value);
            }

            // An unknown from the solve: y = yEff + Geff·qN + Nzeff·z.
            LinqExpr Rebuild(int i, LinqExpr?[] terms, IReadOnlyList<LinqExpr> frees)
            {
                LinqExpr value = LinearOf(i);
                for (int c = 0; c < TN.Length; c++)
                {
                    LinqExpr? weight = r.Affected[i]
                        ? (r.TermGainUsed[i][c] ? Live(r.TermGain, i, c) : null)
                        : (G[i][TN[c]] != 0 ? Cell(G, i, TN[c]) : null);
                    if (weight != null)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(weight, terms[c]!));
                }
                for (int g = 0; g < dz; g++)
                {
                    LinqExpr? weight = r.Affected[i]
                        ? (r.FreeGainUsed[i][g] ? Live(r.FreeGain, i, g) : null)
                        : (Nz[i][g] != 0 ? Cell(Nz, i, g) : null);
                    if (weight != null)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(weight, frees[g]));
                }
                return value;
            }

            if (segmentSolver != null)
            {
                EmitReducedBlackBox(open, constant, Rebuild);
                return;
            }

            var w = new ParameterExpression[kn];
            for (int i = 0; i < kn; i++)
            {
                LinqExpr previous = LinqExpr.Constant(0.0);
                for (int j = 0; j < n; j++)
                    if (Q[N[i]][j] != 0)
                        previous = LinqExpr.Add(previous, LinqExpr.Multiply(Cell(Q, N[i], j), Guess(j)));
                w[i] = Local(previous);
            }
            var current = tracked.ToDictionary(i => i, i => Local(Guess(i)));
            ParameterExpression[] z = plan.free.Select(j => Local(Guess(j))).ToArray();

            // The same coupled groups as the interpreter's reduced solve, so both solve each step identically.
            var augmented = Enumerable.Range(0, size).Select(_ => new double[CircuitInterpreter.RowLength(size)]).ToArray();
            var stepSolution = new double[size];
            int[][] groups = plan.reducedGroups;
            var groupRows = groups.Select(g => g.Select(_ => new double[CircuitInterpreter.RowLength(g.Length)]).ToArray()).ToArray();
            ConstantExpression augmentedArray = LinqExpr.Constant(augmented);

            int[] pivots = CircuitInterpreter.PortPivots(system);
            Expression[] pivotSymbols = pivots.Select(j => system.Unknowns[j]).ToArray();

            // Rows the Newton loop and the final rebuild read, loaded before the loop.
            Hoist(r.Zr);
            Hoist(r.Qzr);
            Hoist(r.CPr);
            Hoist(r.Czr);
            foreach (int i in tracked.Union(required).Where(i => r.Affected[i]))
            {
                if (r.RowVaries(r.TermGain, i))
                    LiveRow(r.TermGain, i);
                if (r.RowVaries(r.FreeGain, i))
                    LiveRow(r.FreeGain, i);
            }
            if (system.RuntimeParameters.Length > 0)
                LiveArray(plan.ParameterValues);

            ParameterExpression iteration = code.DeclInit<int>("iteration", 0);
            ParameterExpression result = code.DeclInit<int>("result", 0);
            bool extrapolate = plan.ExtrapolateGuess && plan.history.Length > 1;
            // Small systems are solved in locals, unrolled, instead of by a call on arrays (see CircuitInterpreter.SolveSmall).
            bool inline = size > 0 && size <= CircuitInterpreter.InlineSolveLimit;
            ParameterExpression[,]? cells = null;
            if (inline)
            {
                cells = new ParameterExpression[size, size + 1];
                for (int i = 0; i < size; i++)
                    for (int j = 0; j <= size; j++)
                        cells[i, j] = code.Decl<double>($"a{i}_{j}");
            }
            ParameterExpression spent = code.DeclInit<int>("spent", 0);
            ParameterExpression restarted = code.DeclInit<bool>("restarted", !extrapolate);
            var finalQ = new LinqExpr?[TN.Length];

            code.DoWhile((LinqExpr exit) =>
            {
                code.Add(LinqExpr.PreIncrementAssign(iteration));

                // Nonlinear terms see each nonlinear port through its pivot unknown, everything else at zero.
                for (int i = 0; i < kn; i++)
                    code.Map(Scope.Local, pivotSymbols[N[i]], w[i]);
                foreach (Expression symbol in TN.SelectMany(c => SymbolScan.Collect(system.Nonlinear[c])).Distinct())
                    if (!N.Any(p => pivotSymbols[p].Equals(symbol)))
                        code.Map(Scope.Local, symbol, ParameterRead(symbol) ?? LinqExpr.Constant(0.0));

                var q = new ParameterExpression[TN.Length];
                var jacobian = new LinqExpr?[TN.Length, kn];
                for (int c = 0; c < TN.Length; c++)
                {
                    Expression term = system.Nonlinear[TN[c]];
                    Dictionary<Expression, Expression> gradient = term.Gradient(N.Select(p => pivotSymbols[p]));
                    q[c] = Local(ToDouble(code.Compile(term)));
                    for (int j = 0; j < kn; j++)
                        if (plan.dependsOn[TN[c], N[j]])
                            jacobian[c, j] = Local(ToDouble(code.Compile(gradient[pivotSymbols[N[j]]])));
                }

                LinqExpr AugmentedCell(ParameterExpression? row, int i, int j) => inline ? cells![i, j] : LinqExpr.ArrayAccess(row!, LinqExpr.Constant(j));
                for (int i = 0; i < kn; i++)
                {
                    ParameterExpression? row = null;
                    if (!inline)
                    {
                        row = code.Decl<double[]>("row" + i);
                        code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(i))));
                    }
                    for (int j = 0; j <= size; j++)
                        code.Add(LinqExpr.Assign(AugmentedCell(row, i, j), LinqExpr.Constant(j == i ? 1.0 : 0.0)));
                    LinqExpr rhs = open[i];
                    for (int c = 0; c < TN.Length; c++)
                    {
                        if (!r.ZrUsed[i][c])
                            continue;
                        LinqExpr zc = Live(r.Zr, i, c);
                        LinqExpr linearPart = q[c];
                        for (int j = 0; j < kn; j++)
                        {
                            if (jacobian[c, j] is not LinqExpr slope)
                                continue;
                            LinqExpr target = AugmentedCell(row, i, j);
                            code.Add(LinqExpr.Assign(target, LinqExpr.Subtract(target, LinqExpr.Multiply(zc, slope))));
                            linearPart = LinqExpr.Subtract(linearPart, LinqExpr.Multiply(slope, w[j]));
                        }
                        rhs = LinqExpr.Add(rhs, LinqExpr.Multiply(zc, linearPart));
                    }
                    for (int g = 0; g < dz; g++)
                        if (r.QzrUsed[i][g])
                            code.Add(LinqExpr.Assign(AugmentedCell(row, i, kn + g), LinqExpr.Negate(Live(r.Qzr, i, g))));
                    code.Add(LinqExpr.Assign(AugmentedCell(row, i, size), rhs));
                }
                for (int f = 0; f < dz; f++)
                {
                    ParameterExpression? row = null;
                    if (!inline)
                    {
                        row = code.Decl<double[]>("constraint" + f);
                        code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(kn + f))));
                    }
                    for (int j = 0; j <= size; j++)
                        code.Add(LinqExpr.Assign(AugmentedCell(row, kn + f, j), LinqExpr.Constant(0.0)));
                    LinqExpr rhs = LinqExpr.Negate(constant[f]);
                    for (int c = 0; c < TN.Length; c++)
                    {
                        if (!r.CPrUsed[f][c])
                            continue;
                        LinqExpr weight = Live(r.CPr, f, c);
                        LinqExpr linearPart = q[c];
                        for (int j = 0; j < kn; j++)
                        {
                            if (jacobian[c, j] is not LinqExpr slope)
                                continue;
                            LinqExpr target = AugmentedCell(row, kn + f, j);
                            code.Add(LinqExpr.Assign(target, LinqExpr.Add(target, LinqExpr.Multiply(weight, slope))));
                            linearPart = LinqExpr.Subtract(linearPart, LinqExpr.Multiply(slope, w[j]));
                        }
                        rhs = LinqExpr.Subtract(rhs, LinqExpr.Multiply(weight, linearPart));
                    }
                    for (int g = 0; g < dz; g++)
                        if (r.CzrUsed[f][g])
                            code.Add(LinqExpr.Assign(AugmentedCell(row, kn + f, kn + g), Live(r.Czr, f, g)));
                    code.Add(LinqExpr.Assign(AugmentedCell(row, kn + f, size), rhs));
                }

                ParameterExpression[] next, zNext;
                if (inline)
                {
                    ParameterExpression[] x = EmitSolveSmall(cells!, size);
                    next = x.Take(kn).ToArray();
                    zNext = x.Skip(kn).ToArray();
                }
                else
                {
                    if (size > 0)
                        code.Add(LinqExpr.Call(SolveStep, augmentedArray, LinqExpr.Constant(stepSolution),
                            LinqExpr.Constant(groups), LinqExpr.Constant(groupRows), LinqExpr.Constant(size)));
                    next = Enumerable.Range(0, kn)
                        .Select(j => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(j)))).ToArray();
                    zNext = Enumerable.Range(0, dz)
                        .Select(g => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(kn + g)))).ToArray();
                }

                var linearized = new LinqExpr?[TN.Length];
                for (int c = 0; c < TN.Length; c++)
                {
                    LinqExpr value = q[c];
                    for (int j = 0; j < kn; j++)
                        if (jacobian[c, j] is LinqExpr slope)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(slope, LinqExpr.Subtract(next[j], w[j])));
                    linearized[c] = Local(value);
                    finalQ[c] = linearized[c];
                }

                ParameterExpression done = code.DeclInit<bool>("done", true);
                foreach (int j in tracked)
                {
                    ParameterExpression nextY = Local(Rebuild(j, linearized, zNext));
                    code.Add(LinqExpr.AndAssign(done, LinqExpr.LessThan(
                        LinqExpr.Call(Abs, LinqExpr.Subtract(nextY, current[j])),
                        LinqExpr.Add(LinqExpr.Multiply(LinqExpr.Call(Abs, current[j]), LinqExpr.Constant(plan.RelativeTolerance)), LinqExpr.Constant(plan.AbsoluteTolerance)))));
                    code.Add(LinqExpr.Assign(current[j], nextY));
                }
                if (TN.Length == 0)
                    code.Add(LinqExpr.Assign(done, LinqExpr.Constant(true)));

                for (int j = 0; j < kn; j++)
                    code.Add(LinqExpr.Assign(w[j], next[j]));
                for (int g = 0; g < dz; g++)
                    code.Add(LinqExpr.Assign(z[g], zNext[g]));

                // The iterations over both starts, as CircuitInterpreter.SolveReduced counts them.
                LinqExpr total = LinqExpr.Call(typeof(Math).GetMethod(nameof(Math.Min), new[] { typeof(int), typeof(int) })!,
                    LinqExpr.Add(spent, iteration), LinqExpr.Constant(0x7F));
                code.Add(LinqExpr.IfThen(done, LinqExpr.Block(LinqExpr.Assign(result, total), exit)));
                code.Add(LinqExpr.IfThen(LinqExpr.Equal(iteration, LinqExpr.Constant(iterationCap)),
                    LinqExpr.Block(LinqExpr.Assign(result, LinqExpr.Or(total, LinqExpr.Constant(0x80))), exit)));

                // Not converged soon from the extrapolated guess: start again, once, from the previous step.
                if (extrapolate)
                {
                    var restart = new List<LinqExpr>
                    {
                        LinqExpr.Assign(restarted, LinqExpr.Constant(true)),
                        LinqExpr.Assign(spent, iteration),
                        LinqExpr.Assign(iteration, LinqExpr.Constant(0)),
                    };
                    for (int i = 0; i < kn; i++)
                    {
                        LinqExpr previousPort = LinqExpr.Constant(0.0);
                        for (int j = 0; j < n; j++)
                            if (Q[N[i]][j] != 0)
                                previousPort = LinqExpr.Add(previousPort, LinqExpr.Multiply(Cell(Q, N[i], j), History(1, j)));
                        restart.Add(LinqExpr.Assign(w[i], previousPort));
                    }
                    foreach (int j in tracked)
                        restart.Add(LinqExpr.Assign(current[j], History(1, j)));
                    for (int g = 0; g < dz; g++)
                        restart.Add(LinqExpr.Assign(z[g], History(1, plan.free[g])));
                    code.Add(LinqExpr.IfThen(
                        LinqExpr.AndAlso(LinqExpr.Not(restarted), LinqExpr.Equal(iteration, LinqExpr.Constant(CircuitInterpreter.RestartAfter))),
                        LinqExpr.Block(restart)));
                }
            }, LinqExpr.Constant(true));

            // Every unknown anything reads, from the final linearized terms and free unknowns. The tracked ones were
            // already rebuilt from exactly those in the last iteration.
            foreach (int i in required.Union(tracked).OrderBy(i => i))
                code.Add(LinqExpr.Assign(Element(y, i), current.TryGetValue(i, out ParameterExpression? done) ? done : Rebuild(i, finalQ, z)));
            code.Return(result);
        }

        // CircuitInterpreter.SolveSmall, unrolled on locals: the same operations in the same order.
        private ParameterExpression[] EmitSolveSmall(ParameterExpression[,] a, int size)
        {
            ParameterExpression swap = code.Decl<double>("swap");
            for (int j = 0; j < size; j++)
            {
                for (int i = j + 1; i < size; i++)
                {
                    var exchange = new List<LinqExpr>();
                    for (int k = j; k <= size; k++)
                    {
                        exchange.Add(LinqExpr.Assign(swap, a[j, k]));
                        exchange.Add(LinqExpr.Assign(a[j, k], a[i, k]));
                        exchange.Add(LinqExpr.Assign(a[i, k], swap));
                    }
                    code.Add(LinqExpr.IfThen(LinqExpr.GreaterThan(LinqExpr.Call(Abs, a[i, j]), LinqExpr.Call(Abs, a[j, j])), LinqExpr.Block(exchange)));
                }
                for (int i = j + 1; i < size; i++)
                {
                    ParameterExpression factor = Local(LinqExpr.Divide(a[i, j], a[j, j]));
                    for (int k = j + 1; k <= size; k++)
                        code.Add(LinqExpr.Assign(a[i, k], LinqExpr.Subtract(a[i, k], LinqExpr.Multiply(factor, a[j, k]))));
                }
            }
            var x = new ParameterExpression[size];
            for (int j = size - 1; j >= 0; j--)
            {
                LinqExpr value = a[j, size];
                for (int k = j + 1; k < size; k++)
                    value = LinqExpr.Subtract(value, LinqExpr.Multiply(a[j, k], x[k]));
                x[j] = Local(LinqExpr.Divide(value, a[j, j]));
            }
            return x;
        }

        // Black-box mode on the reduced path, as CircuitInterpreter.SolveWithSegments: each segment's solver gives its
        // ports, free unknowns and nonlinear-term values in solve order, from the reduced inputs (open-circuit values of
        // the reduced ports, constraint constants, upstream ports and the pots that change its coefficients), and every
        // unknown anything reads follows from y = yEff + Geff·qN + Nzeff·z.
        private void EmitReducedBlackBox(ParameterExpression[] open, ParameterExpression[] constant, Func<int, LinqExpr?[], IReadOnlyList<LinqExpr>, LinqExpr> rebuild)
        {
            LinearPortReduction r = plan.reduction!;
            int[] N = r.N, TN = r.TN;
            int dz = plan.dz;
            var w = new LinqExpr?[plan.k];
            var z = new LinqExpr[dz];
            var q = new LinqExpr?[TN.Length];
            if (system.RuntimeParameters.Length > 0)
                LiveArray(plan.ParameterValues);
            foreach (Segment segment in plan.Segments)
            {
                var inputs = new double[segment.InputCount];
                var outputs = new double[segment.OutputCount];
                ConstantExpression inputArray = LinqExpr.Constant(inputs), outputArray = LinqExpr.Constant(outputs);

                int position = 0;
                foreach (int p in segment.Ports)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), open[Array.IndexOf(N, p)]));
                foreach (int f in segment.Constraints)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), constant[f]));
                foreach (int p in segment.Upstream)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), w[p]!));
                foreach (int j in segment.Parameters)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), Live(plan.ParameterValues, j)));

                code.Add(LinqExpr.Call(SegmentCall, LinqExpr.Constant(segmentSolver, typeof(ISegmentSolver)), LinqExpr.Constant(segment), inputArray, outputArray));

                position = 0;
                foreach (int p in segment.Ports)
                    w[p] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
                foreach (int f in segment.Free)
                    z[f] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
                foreach (int c in segment.NonlinearTerms)
                    q[Array.IndexOf(TN, c)] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
            }

            foreach (int i in required.OrderBy(i => i))
                code.Add(LinqExpr.Assign(Element(y, i), rebuild(i, q, z)));
            code.Return(LinqExpr.Constant(0));
        }

        // The linear part of an unknown a linear term reaches, reading its (possibly nonzero) coefficients live.
        private LinqExpr LiveLinearPart(int i, ParameterExpression[] sources, LinearPortReduction r)
        {
            LinqExpr value = Live(r.Constant, i);
            for (int j = 0; j < plan.parameterCount; j++)
                if (r.GainUsed[i][j])
                    value = LinqExpr.Add(value, LinqExpr.Multiply(Live(r.Gain, i, j), code[plan.Symbols[j]]));
            for (int x = 0; x < sources.Length; x++)
                if (r.SourceUsed[i][x])
                    value = LinqExpr.Add(value, LinqExpr.Multiply(
                        LinqExpr.Multiply(Live(r.Source, i, x), LinqExpr.Constant(plan.sourceCoefficients[x])), sources[x]));
            return value;
        }

        private static readonly MethodInfo SegmentCall = typeof(Emitter).GetMethod(nameof(CallSegment), BindingFlags.Static | BindingFlags.NonPublic)!;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        private static void CallSegment(ISegmentSolver solver, Segment segment, double[] inputs, double[] outputs) => solver.Solve(segment, inputs, outputs);

        private LinqExpr Read(CircuitInterpreter.Binding b) => b.Kind switch
        {
            CircuitInterpreter.Kind.Time => time,
            CircuitInterpreter.Kind.Input => input,
            CircuitInterpreter.Kind.InputHistory => inputPrevious,
            CircuitInterpreter.Kind.Current => Element(y, b.Unknown),
            _ => History(b.Delay, b.Unknown),
        };

        // Newton's starting value for an unknown, as CircuitInterpreter.SolveReduced (see ExtrapolateGuess).
        private LinqExpr Guess(int unknown) => plan.ExtrapolateGuess && plan.history.Length > 1
            ? LinqExpr.Subtract(LinqExpr.Multiply(LinqExpr.Constant(2.0), History(1, unknown)), History(2, unknown))
            : History(1, unknown);

        private LinqExpr History(int delay, int unknown) =>
            LinqExpr.ArrayIndex(historyRows[delay - 1], LinqExpr.Constant(unknown));

        private static IndexExpression Element(ParameterExpression array, int index) =>
            LinqExpr.ArrayAccess(array, LinqExpr.Constant(index));

        // Coefficients are inlined. Dynamic pots will need the ones they affect read from arrays again.
        private static LinqExpr Cell(double[][] matrix, int row, int column) => LinqExpr.Constant(matrix[row][column]);

        private ParameterExpression Local(LinqExpr value)
        {
            ParameterExpression local = code.Decl<double>(code.AnonymousName());
            code.Add(LinqExpr.Assign(local, value));
            return local;
        }

        private static LinqExpr ToDouble(LinqExpr value) =>
            value.Type == typeof(double) ? value : LinqExpr.Convert(value, typeof(double));

        private static double[][] Jagged(double[,] matrix)
        {
            int rows = matrix.GetLength(0), columns = matrix.GetLength(1);
            var result = new double[rows][];
            for (int i = 0; i < rows; i++)
            {
                result[i] = new double[columns];
                for (int j = 0; j < columns; j++)
                    result[i][j] = matrix[i, j];
            }
            return result;
        }
    }
}
