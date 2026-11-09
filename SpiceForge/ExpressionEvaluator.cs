using System.Linq.Expressions;
using ComputerAlgebra;
using ComputerAlgebra.LinqCompiler;
using Expression = ComputerAlgebra.Expression;
using LinqExpr = System.Linq.Expressions.Expression;

namespace SpiceForge;

/// <summary>Compiles expressions with the LiveSPICE compiler, reading symbols from an array.</summary>
public sealed class ExpressionEvaluator
{
    private readonly Action<double[], double[]> evaluate;

    public ExpressionEvaluator(IReadOnlyList<Expression> symbols, IReadOnlyList<Expression> expressions)
    {
        var code = new CodeGen();
        ParameterExpression values = code.Decl<double[]>(Scope.Parameter, "values");
        ParameterExpression results = code.Decl<double[]>(Scope.Parameter, "results");

        for (int i = 0; i < symbols.Count; i++)
            code.DeclInit(symbols[i], LinqExpr.ArrayAccess(values, LinqExpr.Constant(i)));

        for (int i = 0; i < expressions.Count; i++)
        {
            LinqExpr value = code.Compile(expressions[i]);
            if (value.Type != typeof(double))
                value = LinqExpr.Convert(value, typeof(double));
            code.Add(LinqExpr.Assign(LinqExpr.ArrayAccess(results, LinqExpr.Constant(i)), value));
        }

        evaluate = code.Build<Action<double[], double[]>>().Compile();
    }

    public void Evaluate(double[] values, double[] results) => evaluate(values, results);
}
