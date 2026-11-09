using ComputerAlgebra;

namespace SpiceForge;

/// <summary>Solves linear systems exactly in rational arithmetic.</summary>
public static class ExactSolver
{
    /// <summary>Returns X such that A·X = B.</summary>
    public static Real[,] Solve(Real[,] a, Real[,] b)
    {
        int n = a.GetLength(0), columns = b.GetLength(1);
        var rows = new Real[n][];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new Real[n + columns];
            for (int j = 0; j < n; j++)
                rows[i][j] = a[i, j];
            for (int j = 0; j < columns; j++)
                rows[i][n + j] = b[i, j];
        }

        for (int k = 0; k < n; k++)
        {
            int pivot = -1;
            for (int i = k; i < n; i++)
                if (rows[i][k] != 0 && (pivot < 0 || Real.Abs(rows[i][k]) > Real.Abs(rows[pivot][k])))
                    pivot = i;
            if (pivot < 0)
                throw new SingularSystemException($"Matrix is exactly singular at column {k}.");

            (rows[k], rows[pivot]) = (rows[pivot], rows[k]);
            Real inverse = (Real)1 / rows[k][k];
            for (int j = k; j < n + columns; j++)
                if (rows[k][j] != 0)
                    rows[k][j] *= inverse;

            for (int i = 0; i < n; i++)
            {
                if (i == k || rows[i][k] == 0)
                    continue;
                Real factor = rows[i][k];
                for (int j = k; j < n + columns; j++)
                    if (rows[k][j] != 0)
                        rows[i][j] -= factor * rows[k][j];
            }
        }

        var x = new Real[n, columns];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < columns; j++)
                x[i, j] = rows[i][n + j];
        return x;
    }

    public static Real[,] Zeros(int rows, int columns)
    {
        var matrix = new Real[rows, columns];
        for (int i = 0; i < rows; i++)
            for (int j = 0; j < columns; j++)
                matrix[i, j] = 0;
        return matrix;
    }

    public static double[,] ToDouble(Real[,] matrix)
    {
        var result = new double[matrix.GetLength(0), matrix.GetLength(1)];
        for (int i = 0; i < matrix.GetLength(0); i++)
            for (int j = 0; j < matrix.GetLength(1); j++)
                result[i, j] = (double)matrix[i, j];
        return result;
    }
}
