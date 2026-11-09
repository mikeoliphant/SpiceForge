namespace SpiceForge;

/// <summary>Thrown when a system's E matrix can't be factored.</summary>
public sealed class SingularSystemException : Exception
{
    public SingularSystemException(string message) : base(message) { }
}

/// <summary>LU factorization with row and column equilibration and partial pivoting.</summary>
public sealed class DenseLu
{
    private const double SingularTolerance = 1e-13;

    private readonly int n;
    private readonly double[,] lu;
    private readonly int[] permutation;
    private readonly double[] rowScale;
    private readonly double[] columnScale;

    /// <summary>1-norm condition number of the equilibrated matrix.</summary>
    public double ConditionNumber { get; }

    public DenseLu(double[,] matrix)
    {
        n = matrix.GetLength(0);
        if (n != matrix.GetLength(1))
            throw new SingularSystemException($"{n} equations for {matrix.GetLength(1)} unknowns.");

        lu = (double[,])matrix.Clone();
        rowScale = new double[n];
        columnScale = new double[n];
        permutation = Enumerable.Range(0, n).ToArray();

        for (int i = 0; i < n; i++)
        {
            double max = 0;
            for (int j = 0; j < n; j++)
                max = Math.Max(max, Math.Abs(lu[i, j]));
            if (max == 0)
                throw new SingularSystemException($"Equation {i} has no linear terms.");
            rowScale[i] = 1 / max;
            for (int j = 0; j < n; j++)
                lu[i, j] *= rowScale[i];
        }

        for (int j = 0; j < n; j++)
        {
            double max = 0;
            for (int i = 0; i < n; i++)
                max = Math.Max(max, Math.Abs(lu[i, j]));
            if (max == 0)
                throw new SingularSystemException($"Unknown {j} has no linear terms.");
            columnScale[j] = 1 / max;
            for (int i = 0; i < n; i++)
                lu[i, j] *= columnScale[j];
        }

        double norm = OneNorm(lu);
        Factor();
        ConditionNumber = norm * InverseOneNorm();
    }

    private void Factor()
    {
        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < n; i++)
                if (Math.Abs(lu[i, k]) > Math.Abs(lu[pivot, k]))
                    pivot = i;
            if (Math.Abs(lu[pivot, k]) < SingularTolerance)
                throw new SingularSystemException($"Matrix is singular at column {k}.");

            if (pivot != k)
            {
                for (int j = 0; j < n; j++)
                    (lu[k, j], lu[pivot, j]) = (lu[pivot, j], lu[k, j]);
                (permutation[k], permutation[pivot]) = (permutation[pivot], permutation[k]);
            }

            for (int i = k + 1; i < n; i++)
            {
                lu[i, k] /= lu[k, k];
                for (int j = k + 1; j < n; j++)
                    lu[i, j] -= lu[i, k] * lu[k, j];
            }
        }
    }

    /// <summary>Solves <c>E·x = b</c>.</summary>
    public double[] Solve(double[] b)
    {
        var scaled = new double[n];
        for (int i = 0; i < n; i++)
            scaled[i] = b[i] * rowScale[i];
        SolveScaled(scaled, out double[] x);
        for (int i = 0; i < n; i++)
            x[i] *= columnScale[i];
        return x;
    }

    private void SolveScaled(double[] b, out double[] x)
    {
        x = new double[n];
        for (int i = 0; i < n; i++)
        {
            double value = b[permutation[i]];
            for (int j = 0; j < i; j++)
                value -= lu[i, j] * x[j];
            x[i] = value;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double value = x[i];
            for (int j = i + 1; j < n; j++)
                value -= lu[i, j] * x[j];
            x[i] = value / lu[i, i];
        }
    }

    private double InverseOneNorm()
    {
        double norm = 0;
        for (int j = 0; j < n; j++)
        {
            var unit = new double[n];
            unit[j] = 1;
            SolveScaled(unit, out double[] column);
            norm = Math.Max(norm, column.Sum(Math.Abs));
        }
        return norm;
    }

    private static double OneNorm(double[,] matrix)
    {
        double norm = 0;
        for (int j = 0; j < matrix.GetLength(1); j++)
        {
            double sum = 0;
            for (int i = 0; i < matrix.GetLength(0); i++)
                sum += Math.Abs(matrix[i, j]);
            norm = Math.Max(norm, sum);
        }
        return norm;
    }
}
