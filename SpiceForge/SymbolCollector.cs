using ComputerAlgebra;

namespace SpiceForge;

/// <summary>Public access to symbol discovery for validation tools.</summary>
public static class SymbolScan
{
    public static IReadOnlyList<Expression> Collect(Expression expression) => SymbolCollector.Collect(expression);
}

/// <summary>Finds the variables an expression reads: free variables and calls of unknown functions such as V[t - h].</summary>
internal sealed class SymbolCollector : ExpressionVisitor<bool>
{
    private readonly HashSet<Expression> seen = new();
    private readonly List<Expression> symbols = new();

    public static IReadOnlyList<Expression> Collect(IEnumerable<Expression> expressions)
    {
        var collector = new SymbolCollector();
        foreach (Expression expression in expressions)
            collector.Visit(expression);
        return collector.symbols;
    }

    public static IReadOnlyList<Expression> Collect(Expression expression) => Collect(new[] { expression });

    private bool Add(Expression symbol)
    {
        if (seen.Add(symbol))
            symbols.Add(symbol);
        return true;
    }

    private bool VisitAll(IEnumerable<Expression> children)
    {
        foreach (Expression child in children)
            Visit(child);
        return true;
    }

    protected override bool VisitUnknown(Expression E) => true;
    protected override bool VisitVariable(Variable V) => Add(V);
    protected override bool VisitSum(Sum A) => VisitAll(A.Terms);
    protected override bool VisitProduct(Product M) => VisitAll(M.Terms);
    protected override bool VisitBinary(Binary B) => VisitAll(new[] { B.Left, B.Right });
    protected override bool VisitUnary(Unary U) => Visit(U.Operand);
    protected override bool VisitSet(Set S) => VisitAll(S.Members);
    protected override bool VisitMatrix(Matrix A) => VisitAll(A);
    protected override bool VisitCall(Call F) => F.Target is UnknownFunction ? Add(F) : VisitAll(F.Arguments);
}

/// <summary>Finds the arguments of every exponential in an expression.</summary>
internal sealed class ExpArguments : ExpressionVisitor<bool>
{
    private readonly List<Expression> arguments = new();

    public static IReadOnlyList<Expression> Collect(Expression expression)
    {
        var collector = new ExpArguments();
        collector.Visit(expression);
        return collector.arguments;
    }

    private bool VisitAll(IEnumerable<Expression> children)
    {
        foreach (Expression child in children)
            Visit(child);
        return true;
    }

    protected override bool VisitUnknown(Expression E) => true;
    protected override bool VisitSum(Sum A) => VisitAll(A.Terms);
    protected override bool VisitProduct(Product M) => VisitAll(M.Terms);
    protected override bool VisitBinary(Binary B) => VisitAll(new[] { B.Left, B.Right });
    protected override bool VisitUnary(Unary U) => Visit(U.Operand);
    protected override bool VisitSet(Set S) => VisitAll(S.Members);
    protected override bool VisitMatrix(Matrix A) => VisitAll(A);
    protected override bool VisitCall(Call F)
    {
        // An exponential inside a logarithm is a softplus, ln(1 + e^x) (the tube models' Ln1Exp): it grows only
        // linearly, so Newton doesn't overshoot on it the way it does on a p-n junction's bare e^x.
        if (F.Target.Name == "Ln")
            return true;
        if (F.Target.Name == "Exp" && F.Arguments.Count() == 1)
            arguments.Add(F.Arguments.First());
        return VisitAll(F.Arguments);
    }
}
