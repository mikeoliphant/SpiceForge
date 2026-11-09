using System.Linq.Expressions;
using Circuit;
using ComputerAlgebra;
using ComputerAlgebra.LinqCompiler;
using Expression = ComputerAlgebra.Expression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace SpiceForge;

/// <summary>
/// Computes the unknowns LiveSPICE solves outside its Newton systems, the way its compiled code does.
/// CodeGen caches subexpressions and Simulation never clears that cache, so the expressions it compiles
/// after a Newton loop reuse values from the loop's last iteration, from before the final update.
/// This compiles the same expressions in the same order, through the same cache.
/// </summary>
public sealed class FinalSolutionEvaluator
{
    private readonly Action<double[], double[]> evaluate;

    /// <summary>Symbols to supply first in the input array, followed by the Newton unknowns' values.</summary>
    public Expression[] Symbols { get; }

    /// <summary>Newton unknowns, supplied before the final update and then after it.</summary>
    public Expression[] NewtonUnknowns { get; }

    /// <summary>The unknown assigned to each entry of the results.</summary>
    public Expression[] Assigned { get; }

    public FinalSolutionEvaluator(TransientSolution solution)
    {
        List<SolutionSet> sets = solution.Solutions.ToList();
        List<NewtonIteration> newton = sets.OfType<NewtonIteration>().ToList();

        NewtonUnknowns = newton.SelectMany(s => s.Unknowns).Distinct().ToArray();
        Assigned = sets.OfType<LinearSolutions>().SelectMany(s => s.Solutions).Select(a => a.Left).ToArray();

        var excluded = new HashSet<Expression>(NewtonUnknowns.Concat(Assigned).Concat(newton.SelectMany(s => s.Deltas)));
        var referenced = new List<Expression>();
        foreach (SolutionSet set in sets)
        {
            if (set is LinearSolutions linear)
            {
                referenced.AddRange(linear.Solutions.Select(a => a.Right));
            }
            else if (set is NewtonIteration system)
            {
                foreach (LinearCombination equation in system.Equations)
                    referenced.AddRange(equation.Select(term => term.Value));
                if (system.KnownDeltas != null)
                    referenced.AddRange(system.KnownDeltas.Select(a => a.Right));
            }
        }
        Symbols = referenced.SelectMany(SymbolScan.Collect).Distinct().Where(s => !excluded.Contains(s)).ToArray();

        var code = new CodeGen();
        ParameterExpression values = code.Decl<double[]>(Scope.Parameter, "values");
        ParameterExpression results = code.Decl<double[]>(Scope.Parameter, "results");

        int index = 0;
        foreach (Expression symbol in Symbols)
            code.DeclInit(symbol, LinqExpr.ArrayAccess(values, LinqExpr.Constant(index++)));
        foreach (Expression unknown in NewtonUnknowns)
            code.DeclInit(unknown, LinqExpr.ArrayAccess(values, LinqExpr.Constant(index++)));
        int finalValues = index;

        foreach (Expression delta in newton.SelectMany(s => s.Deltas))
            code.DeclInit(delta, LinqExpr.Constant(0.0));

        foreach (SolutionSet set in sets)
        {
            if (set is LinearSolutions linear)
            {
                foreach (Arrow arrow in linear.Solutions)
                    code.DeclInit(arrow.Left, ToDouble(code.Compile(arrow.Right)));
            }
            else if (set is NewtonIteration system)
            {
                // Evaluated at the values from before the last update, as in the final Newton iteration.
                Expression[] deltas = system.UnknownDeltas.ToArray();
                foreach (LinearCombination equation in system.Equations)
                {
                    foreach (Expression delta in deltas)
                        code.Compile(equation[delta]);
                    code.Compile(equation[1]);
                }
                if (system.KnownDeltas != null)
                    foreach (Arrow arrow in system.KnownDeltas)
                        code.DeclInit(arrow.Left, ToDouble(code.Compile(arrow.Right)));

                foreach (Expression unknown in system.Unknowns)
                {
                    int position = finalValues + Array.IndexOf(NewtonUnknowns, unknown);
                    code.Add(LinqExpr.Assign(code[unknown], LinqExpr.ArrayAccess(values, LinqExpr.Constant(position))));
                }
            }
        }

        for (int i = 0; i < Assigned.Length; i++)
            code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(results, LinqExpr.Constant(i)), code[Assigned[i]]));

        evaluate = code.Build<Action<double[], double[]>>().Compile();
    }

    public void Evaluate(double[] values, double[] results) => evaluate(values, results);

    private static LinqExpr ToDouble(LinqExpr value) =>
        value.Type == typeof(double) ? value : LinqExpr.Convert(value, typeof(double));
}
