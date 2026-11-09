namespace SpiceForge;

/// <summary>Maintains a reduced row echelon basis of the row vectors added to it.</summary>
internal sealed class RowBasis
{
    private readonly int columns;
    private readonly double tolerance;
    private readonly List<double[]> rows = new();
    private readonly List<int> pivots = new();

    public RowBasis(int columns, double tolerance)
    {
        this.columns = columns;
        this.tolerance = tolerance;
    }

    /// <summary>Adds a row. Rows with non-finite or zero entries add nothing.</summary>
    public void Add(double[] row)
    {
        double scale = row.Max(Math.Abs);
        if (scale == 0 || double.IsNaN(scale) || double.IsInfinity(scale) || row.Any(double.IsNaN))
            return;

        var reduced = new double[columns];
        for (int j = 0; j < columns; j++)
            reduced[j] = row[j] / scale;

        for (int r = 0; r < rows.Count; r++)
        {
            double factor = reduced[pivots[r]];
            if (factor == 0)
                continue;
            for (int j = 0; j < columns; j++)
                reduced[j] -= factor * rows[r][j];
            reduced[pivots[r]] = 0;
        }

        int pivot = 0;
        for (int j = 1; j < columns; j++)
            if (Math.Abs(reduced[j]) > Math.Abs(reduced[pivot]))
                pivot = j;
        if (Math.Abs(reduced[pivot]) <= tolerance)
            return;

        double inverse = 1 / reduced[pivot];
        for (int j = 0; j < columns; j++)
            reduced[j] = Math.Abs(reduced[j]) <= tolerance ? 0 : reduced[j] * inverse;
        reduced[pivot] = 1;

        for (int r = 0; r < rows.Count; r++)
        {
            double factor = rows[r][pivot];
            if (factor == 0)
                continue;
            for (int j = 0; j < columns; j++)
                rows[r][j] -= factor * reduced[j];
            rows[r][pivot] = 0;
        }

        rows.Add(reduced);
        pivots.Add(pivot);
    }

    public double[,] ToMatrix()
    {
        var matrix = new double[rows.Count, columns];
        for (int r = 0; r < rows.Count; r++)
            for (int j = 0; j < columns; j++)
                matrix[r, j] = rows[r][j];
        return matrix;
    }
}
