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
    private readonly int oversample;
    private readonly double substep;
    private readonly double inverseOversample;
    private readonly bool newton;
    private readonly double[] y;
    private readonly double[][] history;
    private double time;
    private double input;
    private double inputPrevious;

    /// <summary>One entry per oversampled step: the Newton iteration count, with 0x80 set if the cap was hit.</summary>
    public List<byte> IterationCounts { get; } = new();

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
        bool matchLiveSpice = false,
        ISegmentSolver? segmentSolver = null)
    {
        if (segmentSolver != null && matchLiveSpice)
            throw new ArgumentException("Black boxes can't reproduce LiveSPICE's post-Newton evaluation.", nameof(segmentSolver));
        var plan = new CircuitInterpreter(system, inputSymbol, outputSymbols, timeStep, oversample, iterationCap, solution, matchLiveSpice: matchLiveSpice);
        outputs = plan.outputs;
        this.oversample = oversample;
        substep = plan.substep;
        inverseOversample = plan.inverseOversample;
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

    public void Run(double[] inputSamples, double[][] outputBuffers)
    {
        // Runtime parameters only change between calls, so the reduced coefficients only need checking here.
        if (reduce)
            plan.UpdateReduction();
        var sums = new double[outputs.Length];
        for (int n = 0; n < inputSamples.Length; n++)
        {
            double delta = (inputSamples[n] - input) * inverseOversample;
            Array.Clear(sums);

            for (int ov = oversample; ov > 0; ov--)
            {
                time += substep;
                input += delta;
                int result = step(time, input, inputPrevious, history, y);
                if (newton)
                    IterationCounts.Add((byte)result);

                double[] oldest = history[history.Length - 1];
                for (int d = history.Length - 1; d > 0; d--)
                    history[d] = history[d - 1];
                Array.Copy(y, oldest, y.Length);
                history[0] = oldest;

                for (int o = 0; o < outputs.Length; o++)
                    if (outputs[o] >= 0)
                        sums[o] += y[outputs[o]];

                inputPrevious = input;
            }

            for (int o = 0; o < outputs.Length; o++)
                outputBuffers[o][n] = sums[o] * inverseOversample;
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
        private Arrow[] keptArrows = Array.Empty<Arrow>();
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
            else if (plan.reduction != null)
            {
                EmitReduced(sources);
            }
            else if (segmentSolver != null)
            {
                EmitBlackBox(sources);
            }
            else
            {
                EmitNewton(sources);
            }

            var lambda = code.Build<StepFunction>();
            LastStepForTesting = lambda;
            return lambda.Compile();
        }

        // Finds the unknowns anything reads: outputs and state, then whatever computing those needs.
        private void ComputeRequired()
        {
            int n = system.Unknowns.Length;
            Arrow[] arrows = solution.Solutions.OfType<LinearSolutions>().SelectMany(s => s.Solutions).ToArray();
            var arrowFor = new Dictionary<int, Arrow>();
            foreach (Arrow arrow in arrows)
                arrowFor[Array.IndexOf(system.Unknowns, arrow.Left)] = arrow;

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
                // and unknowns LiveSPICE's solution doesn't have are computed only if something reads them.
                LinearPortReduction? reduction = plan.reduction;
                foreach (int j in plan.free)
                    Require(j);
                for (int i = 0; i < n; i++)
                    if (reduction == null && !arrowFor.ContainsKey(i))
                    {
                        Require(i);
                        rows.Add(i);
                    }
                foreach (int r in reduction?.N ?? Enumerable.Range(0, plan.k).ToArray())
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
                if (plan.m == 0 || !plan.MatchLiveSpice)
                    rows.Add(i);

                if (rows.Contains(i))
                    for (int j = 0; j < plan.parameterCount; j++)
                        if (parameterGain[i][j] != 0 && plan.bindings[j].Kind == CircuitInterpreter.Kind.History)
                            Require(plan.bindings[j].Unknown);

                if (plan.m > 0 && plan.MatchLiveSpice && arrowFor.TryGetValue(i, out Arrow? arrow))
                {
                    foreach (Expression symbol in SymbolScan.Collect(arrow.Right))
                    {
                        int unknown = Array.IndexOf(system.Unknowns, symbol);
                        if (unknown >= 0)
                        {
                            Require(unknown);
                            continue;
                        }
                        CircuitInterpreter.Binding binding = CircuitInterpreter.Bind(symbol, system.Unknowns, inputSymbol, timeStep);
                        if (binding.Kind == CircuitInterpreter.Kind.History)
                            Require(binding.Unknown);
                    }
                }
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
                            if (plan.m == 0 || !plan.MatchLiveSpice)
                                rows.Add(plan.bindings[j].Unknown);
                        }
            }

            linearRows = rows;
            keptArrows = plan.MatchLiveSpice ? arrows.Where(a => required.Contains(Array.IndexOf(system.Unknowns, a.Left))).ToArray() : Array.Empty<Arrow>();
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

        // Black-box mode, as CircuitInterpreter.SolveWithSegments: each segment's solver gives its ports, free unknowns
        // and nonlinear-term values in solve order, and every unknown anything reads follows from y = yLinear + G·q + Nz·z.
        private void EmitBlackBox(ParameterExpression[] sources)
        {
            int n = system.Unknowns.Length, k = plan.k, m = plan.m, dz = plan.dz;

            var linearNeeded = new SortedSet<int>(required);
            for (int r = 0; r < k; r++)
                for (int j = 0; j < n; j++)
                    if (Q[r][j] != 0)
                        linearNeeded.Add(j);
            var yLinear = new Dictionary<int, ParameterExpression>();
            foreach (int i in linearNeeded)
                yLinear[i] = Local(LinearPart(i, sources));

            var voc = new LinqExpr[k];
            for (int r = 0; r < k; r++)
            {
                LinqExpr open = LinqExpr.Constant(0.0);
                for (int j = 0; j < n; j++)
                    if (Q[r][j] != 0)
                        open = LinqExpr.Add(open, LinqExpr.Multiply(Cell(Q, r, j), yLinear[j]));
                voc[r] = Local(open);
            }

            var cs = new LinqExpr[dz];
            for (int f = 0; f < dz; f++)
            {
                LinqExpr value = LinqExpr.Constant(plan.CC[f]);
                for (int j = 0; j < plan.parameterCount; j++)
                    if (CH[f][j] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(CH, f, j), code[plan.Symbols[j]]));
                for (int x = 0; x < sources.Length; x++)
                    if (CS[f][x] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(
                            LinqExpr.Multiply(Cell(CS, f, x), LinqExpr.Constant(plan.sourceCoefficients[x])), sources[x]));
                cs[f] = Local(value);
            }

            var w = new LinqExpr?[k];
            var z = new LinqExpr?[dz];
            var q = new LinqExpr?[m];
            foreach (Segment segment in plan.Segments)
            {
                var inputs = new double[segment.InputCount];
                var outputs = new double[segment.OutputCount];
                ConstantExpression inputArray = LinqExpr.Constant(inputs), outputArray = LinqExpr.Constant(outputs);

                int position = 0;
                foreach (int p in segment.Ports)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), voc[p]));
                foreach (int f in segment.Constraints)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), cs[f]));
                foreach (int p in segment.Upstream)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)), w[p]!));
                foreach (int j in segment.Parameters)
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(inputArray, LinqExpr.Constant(position++)),
                        LinqExpr.ArrayIndex(LinqExpr.Constant(plan.ParameterValues), LinqExpr.Constant(j))));

                code.Add(LinqExpr.Call(SegmentCall, LinqExpr.Constant(segmentSolver, typeof(ISegmentSolver)), LinqExpr.Constant(segment), inputArray, outputArray));

                position = 0;
                foreach (int p in segment.Ports)
                    w[p] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
                foreach (int f in segment.Free)
                    z[f] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
                foreach (int c in segment.NonlinearTerms)
                    q[c] = Local(LinqExpr.ArrayIndex(outputArray, LinqExpr.Constant(position++)));
            }

            foreach (int i in required.OrderBy(i => i))
            {
                LinqExpr value = yLinear[i];
                for (int c = 0; c < m; c++)
                    if (G[i][c] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(G, i, c), q[c]!));
                for (int f = 0; f < dz; f++)
                    if (Nz[i][f] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(Nz, i, f), z[f]!));
                code.Add(LinqExpr.Assign(Element(y, i), value));
            }
            code.Return(LinqExpr.Constant(0));
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
            // ports read. The rest are computed after the loop, where they're used, as EmitNewton does. Computing them
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
                        previous = LinqExpr.Add(previous, LinqExpr.Multiply(Cell(Q, N[i], j), History(1, j)));
                w[i] = Local(previous);
            }
            var current = tracked.ToDictionary(i => i, i => Local(History(1, i)));
            ParameterExpression[] z = plan.free.Select(j => Local(History(1, j))).ToArray();

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

                for (int i = 0; i < kn; i++)
                {
                    ParameterExpression row = code.Decl<double[]>("row" + i);
                    code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(i))));
                    for (int j = 0; j <= size; j++)
                        code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(j)), LinqExpr.Constant(j == i ? 1.0 : 0.0)));
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
                            IndexExpression target = LinqExpr.ArrayAccess(row, LinqExpr.Constant(j));
                            code.Add(LinqExpr.Assign(target, LinqExpr.Subtract(target, LinqExpr.Multiply(zc, slope))));
                            linearPart = LinqExpr.Subtract(linearPart, LinqExpr.Multiply(slope, w[j]));
                        }
                        rhs = LinqExpr.Add(rhs, LinqExpr.Multiply(zc, linearPart));
                    }
                    for (int g = 0; g < dz; g++)
                        if (r.QzrUsed[i][g])
                            code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(kn + g)), LinqExpr.Negate(Live(r.Qzr, i, g))));
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(size)), rhs));
                }
                for (int f = 0; f < dz; f++)
                {
                    ParameterExpression row = code.Decl<double[]>("constraint" + f);
                    code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(kn + f))));
                    for (int j = 0; j <= size; j++)
                        code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(j)), LinqExpr.Constant(0.0)));
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
                            IndexExpression target = LinqExpr.ArrayAccess(row, LinqExpr.Constant(j));
                            code.Add(LinqExpr.Assign(target, LinqExpr.Add(target, LinqExpr.Multiply(weight, slope))));
                            linearPart = LinqExpr.Subtract(linearPart, LinqExpr.Multiply(slope, w[j]));
                        }
                        rhs = LinqExpr.Subtract(rhs, LinqExpr.Multiply(weight, linearPart));
                    }
                    for (int g = 0; g < dz; g++)
                        if (r.CzrUsed[f][g])
                            code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(kn + g)), Live(r.Czr, f, g)));
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(size)), rhs));
                }

                if (size > 0)
                    code.Add(LinqExpr.Call(SolveStep, augmentedArray, LinqExpr.Constant(stepSolution),
                        LinqExpr.Constant(groups), LinqExpr.Constant(groupRows), LinqExpr.Constant(size)));

                ParameterExpression[] next = Enumerable.Range(0, kn)
                    .Select(j => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(j)))).ToArray();
                ParameterExpression[] zNext = Enumerable.Range(0, dz)
                    .Select(g => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(kn + g)))).ToArray();

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

                code.Add(LinqExpr.IfThen(done, LinqExpr.Block(LinqExpr.Assign(result, iteration), exit)));
                code.Add(LinqExpr.IfThen(LinqExpr.Equal(iteration, LinqExpr.Constant(iterationCap)),
                    LinqExpr.Block(LinqExpr.Assign(result, LinqExpr.Or(iteration, LinqExpr.Constant(0x80))), exit)));
            }, LinqExpr.Constant(true));

            // Every unknown anything reads, from the final linearized terms and free unknowns. The tracked ones were
            // already rebuilt from exactly those in the last iteration.
            foreach (int i in required.Union(tracked).OrderBy(i => i))
                code.Add(LinqExpr.Assign(Element(y, i), current.TryGetValue(i, out ParameterExpression? done) ? done : Rebuild(i, finalQ, z)));
            code.Return(result);
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

        private void EmitNewton(ParameterExpression[] sources)
        {
            int n = system.Unknowns.Length, k = plan.k, m = plan.m, dz = plan.dz, K = plan.K;
            // A Newton system with no equations only holds a free unknown at its previous value.
            List<NewtonIteration> newtonSets = solution.Solutions.OfType<NewtonIteration>().Where(s => s.Equations.Any()).ToList();
            if (plan.MatchLiveSpice && newtonSets.Count != 1)
                throw new NotSupportedException("Circuits with several LiveSPICE Newton systems aren't supported.");

            var assigned = new HashSet<Expression>(solution.Solutions.OfType<LinearSolutions>().SelectMany(s => s.Solutions).Select(a => a.Left));
            int[] active = Enumerable.Range(0, n).Where(i => !assigned.Contains(system.Unknowns[i])).ToArray();

            // yLinear is only needed for the unknowns Newton updates and the unknowns the ports read.
            var linearNeeded = new SortedSet<int>(active);
            for (int r = 0; r < k; r++)
                for (int j = 0; j < n; j++)
                    if (Q[r][j] != 0)
                        linearNeeded.Add(j);
            var yLinear = new Dictionary<int, ParameterExpression>();
            foreach (int i in linearNeeded)
                yLinear[i] = Local(LinearPart(i, sources));

            var w = new ParameterExpression[k];
            var voc = new ParameterExpression[k];
            for (int r = 0; r < k; r++)
            {
                LinqExpr previous = LinqExpr.Constant(0.0), open = LinqExpr.Constant(0.0);
                for (int j = 0; j < n; j++)
                {
                    if (Q[r][j] == 0)
                        continue;
                    previous = LinqExpr.Add(previous, LinqExpr.Multiply(Cell(Q, r, j), History(1, j)));
                    open = LinqExpr.Add(open, LinqExpr.Multiply(Cell(Q, r, j), yLinear[j]));
                }
                w[r] = Local(previous);
                voc[r] = Local(open);
            }

            var current = active.ToDictionary(i => i, i => Local(History(1, i)));

            // Free unknowns and constraints, when E is singular (see SingularSplit).
            ParameterExpression[] z = plan.free.Select(j => Local(History(1, j))).ToArray();
            var cs = new ParameterExpression[dz];
            for (int f = 0; f < dz; f++)
            {
                LinqExpr value = LinqExpr.Constant(plan.CC[f]);
                for (int j = 0; j < plan.parameterCount; j++)
                    if (CH[f][j] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(CH, f, j), code[plan.Symbols[j]]));
                for (int x = 0; x < sources.Length; x++)
                    if (CS[f][x] != 0)
                        value = LinqExpr.Add(value, LinqExpr.Multiply(
                            LinqExpr.Multiply(Cell(CS, f, x), LinqExpr.Constant(plan.sourceCoefficients[x])), sources[x]));
                cs[f] = Local(value);
            }

            var augmented = Enumerable.Range(0, K).Select(_ => new double[CircuitInterpreter.RowLength(K)]).ToArray();
            var stepSolution = new double[K];
            ConstantExpression augmentedArray = LinqExpr.Constant(augmented);

            int[] pivots = CircuitInterpreter.PortPivots(system);
            Expression[] pivotSymbols = pivots.Select(j => system.Unknowns[j]).ToArray();

            var before = active.ToDictionary(i => i, i => code.Decl<double>("before" + i));
            ParameterExpression iteration = code.DeclInit<int>("iteration", 0);
            var finalLinearized = new ParameterExpression[m];
            ParameterExpression result = code.DeclInit<int>("result", 0);

            code.DoWhile((LinqExpr exit) =>
            {
                code.Add(LinqExpr.PreIncrementAssign(iteration));

                // The nonlinear terms see each port through its pivot unknown, with other unknowns at zero.
                for (int p = 0; p < k; p++)
                    code.Map(Scope.Local, pivotSymbols[p], w[p]);
                foreach (Expression symbol in system.Nonlinear.SelectMany(SymbolScan.Collect).Distinct())
                    if (!pivotSymbols.Contains(symbol))
                        code.Map(Scope.Local, symbol, ParameterRead(symbol) ?? LinqExpr.Constant(0.0));

                var q = new ParameterExpression[m];
                var jacobian = new LinqExpr?[m, k];
                for (int c = 0; c < m; c++)
                {
                    Dictionary<Expression, Expression> gradient = system.Nonlinear[c].Gradient(pivotSymbols);
                    LinqExpr value = ToDouble(code.Compile(system.Nonlinear[c]));
                    for (int p = 0; p < k; p++)
                    {
                        if (D[c][p] != 0)
                            value = LinqExpr.Subtract(value, LinqExpr.Multiply(Cell(D, c, p), w[p]));

                        if (plan.dependsOn[c, p] || D[c][p] != 0)
                        {
                            LinqExpr partial = plan.dependsOn[c, p] ? ToDouble(code.Compile(gradient[pivotSymbols[p]])) : LinqExpr.Constant(0.0);
                            if (D[c][p] != 0)
                                partial = LinqExpr.Subtract(partial, Cell(D, c, p));
                            jacobian[c, p] = Local(partial);
                        }
                    }
                    q[c] = Local(value);
                }

                for (int r = 0; r < k; r++)
                {
                    ParameterExpression row = code.Decl<double[]>("row" + r);
                    code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(r))));
                    for (int p = 0; p <= K; p++)
                        code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(p)), LinqExpr.Constant(p == r ? 1.0 : 0.0)));

                    LinqExpr rhs = voc[r];
                    for (int c = 0; c < m; c++)
                    {
                        if (Z[r][c] == 0)
                            continue;
                        LinqExpr linear = q[c];
                        for (int p = 0; p < k; p++)
                        {
                            if (jacobian[c, p] is not LinqExpr partial)
                                continue;
                            IndexExpression target = LinqExpr.ArrayAccess(row, LinqExpr.Constant(p));
                            code.Add(LinqExpr.Assign(target, LinqExpr.Subtract(target, LinqExpr.Multiply(Cell(Z, r, c), partial))));
                            linear = LinqExpr.Subtract(linear, LinqExpr.Multiply(partial, w[p]));
                        }
                        rhs = LinqExpr.Add(rhs, LinqExpr.Multiply(Cell(Z, r, c), linear));
                    }
                    for (int f = 0; f < dz; f++)
                        if (Qz[r][f] != 0)
                            code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(k + f)), LinqExpr.Constant(-Qz[r][f])));
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(K)), rhs));
                }

                for (int f = 0; f < dz; f++)
                {
                    ParameterExpression row = code.Decl<double[]>("constraint" + f);
                    code.Add(LinqExpr.Assign(row, LinqExpr.ArrayIndex(augmentedArray, LinqExpr.Constant(k + f))));
                    for (int p = 0; p <= K; p++)
                        code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(p)), LinqExpr.Constant(0.0)));

                    LinqExpr rhs = LinqExpr.Negate(cs[f]);
                    for (int c = 0; c < m; c++)
                    {
                        if (CP[f][c] == 0)
                            continue;
                        LinqExpr linear = q[c];
                        for (int p = 0; p < k; p++)
                        {
                            if (jacobian[c, p] is not LinqExpr partial)
                                continue;
                            IndexExpression target = LinqExpr.ArrayAccess(row, LinqExpr.Constant(p));
                            code.Add(LinqExpr.Assign(target, LinqExpr.Add(target, LinqExpr.Multiply(Cell(CP, f, c), partial))));
                            linear = LinqExpr.Subtract(linear, LinqExpr.Multiply(partial, w[p]));
                        }
                        rhs = LinqExpr.Subtract(rhs, LinqExpr.Multiply(Cell(CP, f, c), linear));
                    }
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(K)), rhs));
                }
                code.Add(LinqExpr.Call(SolveStep, augmentedArray, LinqExpr.Constant(stepSolution),
                    LinqExpr.Constant(plan.groups), LinqExpr.Constant(plan.groupRows), LinqExpr.Constant(K)));

                ParameterExpression[] zNext = Enumerable.Range(0, dz)
                    .Select(f => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(k + f))))
                    .ToArray();

                ParameterExpression[] next = Enumerable.Range(0, k)
                    .Select(p => Local(LinqExpr.ArrayIndex(LinqExpr.Constant(stepSolution), LinqExpr.Constant(p))))
                    .ToArray();

                var linearized = new ParameterExpression[m];
                for (int c = 0; c < m; c++)
                {
                    LinqExpr value = q[c];
                    for (int p = 0; p < k; p++)
                        if (jacobian[c, p] is LinqExpr partial)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(partial, LinqExpr.Subtract(next[p], w[p])));
                    linearized[c] = Local(value);
                    finalLinearized[c] = linearized[c];
                }

                var nextY = new Dictionary<int, ParameterExpression>();
                foreach (int i in active)
                {
                    LinqExpr value = yLinear[i];
                    for (int c = 0; c < m; c++)
                        if (G[i][c] != 0)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(G, i, c), linearized[c]));
                    for (int f = 0; f < dz; f++)
                        if (Nz[i][f] != 0)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(Nz, i, f), zNext[f]));
                    nextY[i] = Local(value);
                }

                ParameterExpression done = code.DeclInit<bool>("done", true);
                if (plan.LinearOnly)
                    code.Add(LinqExpr.Assign(done, LinqExpr.Constant(true)));
                foreach (int j in plan.convergence)
                {
                    code.Add(LinqExpr.AndAssign(done, LinqExpr.LessThan(
                        LinqExpr.Call(Abs, LinqExpr.Subtract(nextY[j], current[j])),
                        LinqExpr.Add(LinqExpr.Multiply(LinqExpr.Call(Abs, current[j]), LinqExpr.Constant(1e-4)), LinqExpr.Constant(1e-6)))));
                }

                foreach (int i in active)
                {
                    code.Add(LinqExpr.Assign(before[i], current[i]));
                    code.Add(LinqExpr.Assign(current[i], nextY[i]));
                }
                for (int p = 0; p < k; p++)
                    code.Add(LinqExpr.Assign(w[p], next[p]));
                for (int f = 0; f < dz; f++)
                    code.Add(LinqExpr.Assign(z[f], zNext[f]));

                code.Add(LinqExpr.IfThen(done, LinqExpr.Block(LinqExpr.Assign(result, iteration), exit)));
                code.Add(LinqExpr.IfThen(LinqExpr.Equal(iteration, LinqExpr.Constant(iterationCap)),
                    LinqExpr.Block(LinqExpr.Assign(result, LinqExpr.Or(iteration, LinqExpr.Constant(0x80))), exit)));
            }, LinqExpr.Constant(true));

            foreach (int i in active)
                code.Add(LinqExpr.Assign(Element(y, i), current[i]));

            if (plan.MatchLiveSpice)
            {
                EmitFinalSolutions(newtonSets[0], before);
            }
            else
            {
                // Every other unknown anything reads follows from the solve: y = yLinear + G·q + Nz·z.
                var activeSet = new HashSet<int>(active);
                foreach (int i in required.Where(i => !activeSet.Contains(i)).OrderBy(i => i))
                {
                    LinqExpr value = LinearPart(i, sources);
                    for (int c = 0; c < m; c++)
                        if (G[i][c] != 0)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(G, i, c), finalLinearized[c]));
                    for (int f = 0; f < dz; f++)
                        if (Nz[i][f] != 0)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(Nz, i, f), z[f]));
                    code.Add(LinqExpr.Assign(Element(y, i), value));
                }
            }
            code.Return(result);
        }

        // Unknowns outside the Newton system, as Simulation computes them. Its subexpression cache carries
        // values from the last Newton iteration, from before the final update, into these expressions. Only
        // the cached values these expressions actually read need computing.
        private void EmitFinalSolutions(NewtonIteration newtonSet, Dictionary<int, ParameterExpression> before)
        {
            code.SyncPoint();

            Arrow[] arrows = keptArrows;
            HashSet<Expression> cached = CachedByNewton(newtonSet);
            var stale = new List<Expression>();
            var seen = new HashSet<Expression>();
            foreach (Arrow arrow in arrows)
                FindCached(arrow.Right, cached, stale, seen);

            Expression[] newtonUnknowns = newtonSet.Unknowns.ToArray();
            var excluded = new HashSet<Expression>(newtonUnknowns.Concat(arrows.Select(a => a.Left)));
            foreach (Expression symbol in arrows.Select(a => a.Right).Concat(stale).SelectMany(SymbolScan.Collect).Distinct())
            {
                if (excluded.Contains(symbol) || code.LookUp(symbol) != null)
                    continue;
                int unknown = Array.IndexOf(system.Unknowns, symbol);
                CircuitInterpreter.Binding binding = unknown >= 0
                    ? new CircuitInterpreter.Binding(CircuitInterpreter.Kind.Current, unknown, 0)
                    : CircuitInterpreter.Bind(symbol, system.Unknowns, inputSymbol, timeStep);
                code.DeclInit(symbol, Read(binding));
            }

            foreach (Expression unknown in newtonUnknowns)
            {
                int index = Array.IndexOf(system.Unknowns, unknown);
                code.DeclInit(unknown, index >= 0 && before.TryGetValue(index, out ParameterExpression? value) ? value : LinqExpr.Constant(0.0));
            }

            foreach (Expression expression in stale)
                code.Compile(expression);

            foreach (Expression unknown in newtonUnknowns)
            {
                int index = Array.IndexOf(system.Unknowns, unknown);
                if (index >= 0)
                    code.Add(LinqExpr.Assign(code[unknown], Element(y, index)));
            }

            foreach (Arrow arrow in arrows)
            {
                code.DeclInit(arrow.Left, ToDouble(code.Compile(arrow.Right)));
                int target = Array.IndexOf(system.Unknowns, arrow.Left);
                if (target >= 0)
                    code.Add(LinqExpr.Assign(Element(y, target), code[arrow.Left]));
            }
        }

        // Compiles the Newton system with a throwaway CodeGen to learn exactly which subexpressions Simulation caches.
        private static HashSet<Expression> CachedByNewton(NewtonIteration newtonSet)
        {
            var scratch = new CodeGen();
            var expressions = new List<Expression>();
            Expression[] deltas = newtonSet.UnknownDeltas.ToArray();
            foreach (LinearCombination equation in newtonSet.Equations)
            {
                foreach (Expression delta in deltas)
                    expressions.Add(equation[delta]);
                expressions.Add(equation[1]);
            }
            if (newtonSet.KnownDeltas != null)
                expressions.AddRange(newtonSet.KnownDeltas.Select(a => a.Right));

            var declared = new HashSet<Expression>(newtonSet.Deltas);
            foreach (Expression delta in declared)
                scratch.DeclInit(delta, LinqExpr.Constant(0.0));
            foreach (Expression symbol in expressions.SelectMany(SymbolScan.Collect).Distinct())
                if (declared.Add(symbol))
                    scratch.DeclInit(symbol, LinqExpr.Constant(0.0));

            foreach (Expression expression in expressions)
                scratch.Compile(expression);

            var field = typeof(CodeGen).GetField("intermediates", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var intermediates = (Dictionary<Expression, LinqExpr>)field.GetValue(scratch)!;
            return new HashSet<Expression>(intermediates.Keys);
        }

        // Walks an expression the way CompileExpression does, stopping at anything found in the cache.
        private static void FindCached(Expression e, HashSet<Expression> cached, List<Expression> found, HashSet<Expression> seen)
        {
            if (cached.Contains(e))
            {
                if (seen.Add(e))
                    found.Add(e);
                return;
            }

            void Visit(Expression x) => FindCached(x, cached, found, seen);

            switch (e)
            {
                case Sum sum:
                    bool first = true;
                    foreach (Expression term in sum.Terms)
                    {
                        Visit(!first && IsNegative(term) ? -term : term);
                        first = false;
                    }
                    break;
                case Product product:
                    foreach (Expression term in product.Terms.Where(t => !t.Equals(-1)))
                        Visit(term);
                    break;
                case Call call:
                    foreach (Expression argument in call.Arguments)
                        Visit(argument);
                    break;
                case Power power:
                    if (power.Right.Equals(0.5) || power.Right.Equals(-0.5))
                    {
                        Visit(Call.Sqrt(power.Left));
                        break;
                    }
                    Visit(power.Left);
                    if (!power.Right.Equals(2) && !power.Right.Equals(-1) && !power.Right.Equals(-2))
                        Visit(power.Right);
                    break;
                case Binary binary:
                    Visit(binary.Left);
                    Visit(binary.Right);
                    break;
                case Unary unary:
                    Visit(unary.Operand);
                    break;
            }
        }

        private static bool IsNegative(Expression x) =>
            Product.TermsOf(x).FirstOrDefault(i => i is Constant) is Constant c && c.Value < 0;

        private LinqExpr Read(CircuitInterpreter.Binding b) => b.Kind switch
        {
            CircuitInterpreter.Kind.Time => time,
            CircuitInterpreter.Kind.Input => input,
            CircuitInterpreter.Kind.InputHistory => inputPrevious,
            CircuitInterpreter.Kind.Current => Element(y, b.Unknown),
            _ => History(b.Delay, b.Unknown),
        };

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
