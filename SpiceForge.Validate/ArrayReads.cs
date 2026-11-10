using System.Linq.Expressions;

/// <summary>Counts the double-array element reads in a compiled step: with live coefficients, mostly those.</summary>
internal static class ArrayReads
{
    public static int Count(LambdaExpression step)
    {
        var counter = new Counter();
        counter.Visit(step);
        return counter.Reads;
    }

    private sealed class Counter : ExpressionVisitor
    {
        public int Reads;

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType == ExpressionType.ArrayIndex && node.Type == typeof(double))
                Reads++;
            return base.VisitBinary(node);
        }
    }
}
