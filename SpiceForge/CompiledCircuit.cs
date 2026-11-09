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

    public CompiledCircuit(
        ExtractedSystem system,
        Expression inputSymbol,
        IReadOnlyList<Expression> outputSymbols,
        Expression timeStep,
        int oversample,
        int iterationCap,
        TransientSolution solution)
    {
        var plan = new CircuitInterpreter(system, inputSymbol, outputSymbols, timeStep, oversample, iterationCap, solution);
        outputs = plan.outputs;
        this.oversample = oversample;
        substep = plan.substep;
        inverseOversample = plan.inverseOversample;
        newton = plan.m > 0;
        y = new double[system.Unknowns.Length];
        history = plan.history;
        step = new Emitter(plan, system, inputSymbol, timeStep, iterationCap, solution).Build();
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
        private readonly CodeGen code = new();
        private readonly double[][] parameterGain, sourceGain, G, Z, Q, D;
        private ParameterExpression time = null!, input = null!, inputPrevious = null!, history = null!, y = null!;
        private ParameterExpression[] historyRows = Array.Empty<ParameterExpression>();
        private readonly HashSet<int> required = new();
        private SortedSet<int> linearRows = new();
        private Arrow[] keptArrows = Array.Empty<Arrow>();
        private readonly int[] outputs;

        public Emitter(CircuitInterpreter plan, ExtractedSystem system, Expression inputSymbol, Expression timeStep, int iterationCap, TransientSolution solution)
        {
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
                    || linearRows.Any(row => parameterGain[row][i] != 0);
                if (used)
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
                EmitNewton(sources);
            }

            return code.Build<StepFunction>().Compile();
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
                for (int i = 0; i < n; i++)
                    if (!arrowFor.ContainsKey(i))
                    {
                        Require(i);
                        rows.Add(i);
                    }
                for (int r = 0; r < plan.k; r++)
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
                if (plan.m == 0)
                    rows.Add(i);

                if (rows.Contains(i))
                    for (int j = 0; j < plan.parameterCount; j++)
                        if (parameterGain[i][j] != 0 && plan.bindings[j].Kind == CircuitInterpreter.Kind.History)
                            Require(plan.bindings[j].Unknown);

                if (plan.m > 0 && arrowFor.TryGetValue(i, out Arrow? arrow))
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
                            if (plan.m == 0)
                                rows.Add(plan.bindings[j].Unknown);
                        }
            }

            linearRows = rows;
            keptArrows = arrows.Where(a => required.Contains(Array.IndexOf(system.Unknowns, a.Left))).ToArray();
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

        private void EmitNewton(ParameterExpression[] sources)
        {
            int n = system.Unknowns.Length, k = plan.k, m = plan.m;
            // A Newton system with no equations only holds a free unknown at its previous value.
            List<NewtonIteration> newtonSets = solution.Solutions.OfType<NewtonIteration>().Where(s => s.Equations.Any()).ToList();
            if (newtonSets.Count != 1)
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

            var augmented = Enumerable.Range(0, k).Select(_ => new double[CircuitInterpreter.RowLength(k)]).ToArray();
            var stepSolution = new double[k];
            ConstantExpression augmentedArray = LinqExpr.Constant(augmented);

            int[] pivots = CircuitInterpreter.PortPivots(system);
            Expression[] pivotSymbols = pivots.Select(j => system.Unknowns[j]).ToArray();

            var before = active.ToDictionary(i => i, i => code.Decl<double>("before" + i));
            ParameterExpression iteration = code.DeclInit<int>("iteration", 0);
            ParameterExpression result = code.DeclInit<int>("result", 0);

            code.DoWhile((LinqExpr exit) =>
            {
                code.Add(LinqExpr.PreIncrementAssign(iteration));

                // The nonlinear terms see each port through its pivot unknown, with other unknowns at zero.
                for (int p = 0; p < k; p++)
                    code.Map(Scope.Local, pivotSymbols[p], w[p]);
                foreach (Expression symbol in system.Nonlinear.SelectMany(SymbolScan.Collect).Distinct())
                    if (!pivotSymbols.Contains(symbol))
                        code.Map(Scope.Local, symbol, LinqExpr.Constant(0.0));

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
                    for (int p = 0; p <= k; p++)
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
                    code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(row, LinqExpr.Constant(k)), rhs));
                }

                code.Add(LinqExpr.Call(SolveStep, augmentedArray, LinqExpr.Constant(stepSolution),
                    LinqExpr.Constant(plan.groups), LinqExpr.Constant(plan.groupRows), LinqExpr.Constant(k)));

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
                }

                var nextY = new Dictionary<int, ParameterExpression>();
                foreach (int i in active)
                {
                    LinqExpr value = yLinear[i];
                    for (int c = 0; c < m; c++)
                        if (G[i][c] != 0)
                            value = LinqExpr.Add(value, LinqExpr.Multiply(Cell(G, i, c), linearized[c]));
                    nextY[i] = Local(value);
                }

                ParameterExpression done = code.DeclInit<bool>("done", true);
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

                code.Add(LinqExpr.IfThen(done, LinqExpr.Block(LinqExpr.Assign(result, iteration), exit)));
                code.Add(LinqExpr.IfThen(LinqExpr.Equal(iteration, LinqExpr.Constant(iterationCap)),
                    LinqExpr.Block(LinqExpr.Assign(result, LinqExpr.Or(iteration, LinqExpr.Constant(0x80))), exit)));
            }, LinqExpr.Constant(true));

            foreach (int i in active)
                code.Add(LinqExpr.Assign(Element(y, i), current[i]));

            EmitFinalSolutions(newtonSets[0], before);
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
