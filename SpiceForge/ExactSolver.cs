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

    /// <summary>
    /// Finds rows and columns of A that form a nonsingular block of full rank, by elimination with full pivoting.
    /// </summary>
    public static void RankBasis(Real[,] a, out int[] rows, out int[] columns)
    {
        int n = a.GetLength(0), cols = a.GetLength(1);
        var work = (Real[,])a.Clone();
        var rowOrder = Enumerable.Range(0, n).ToArray();
        var colOrder = Enumerable.Range(0, cols).ToArray();
        int rank = 0;
        for (; rank < Math.Min(n, cols); rank++)
        {
            int bestRow = -1, bestCol = -1;
            Real best = 0;
            for (int i = rank; i < n; i++)
                for (int j = rank; j < cols; j++)
                {
                    Real value = Real.Abs(work[rowOrder[i], colOrder[j]]);
                    if (value != 0 && (bestRow < 0 || value > best))
                    {
                        best = value;
                        bestRow = i;
                        bestCol = j;
                    }
                }
            if (bestRow < 0)
                break;

            (rowOrder[rank], rowOrder[bestRow]) = (rowOrder[bestRow], rowOrder[rank]);
            (colOrder[rank], colOrder[bestCol]) = (colOrder[bestCol], colOrder[rank]);
            int pr = rowOrder[rank], pc = colOrder[rank];
            for (int i = rank + 1; i < n; i++)
            {
                int r = rowOrder[i];
                if (work[r, pc] == 0)
                    continue;
                Real factor = work[r, pc] / work[pr, pc];
                for (int j = rank; j < cols; j++)
                {
                    int c = colOrder[j];
                    if (work[pr, c] != 0)
                        work[r, c] -= factor * work[pr, c];
                }
            }
        }

        rows = rowOrder.Take(rank).OrderBy(i => i).ToArray();
        columns = colOrder.Take(rank).OrderBy(j => j).ToArray();
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
