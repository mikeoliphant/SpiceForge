using ComputerAlgebra;

namespace SpiceForge;

/// <summary>
/// Handles a singular E, which happens when some unknowns are fixed only through nonlinear terms, such as
/// the node between two diodes in series. A nonsingular block of E gives the other unknowns in terms of the
/// sources, q and the free unknowns z. The rows outside that block become constraints on q alone:
/// <c>CH·p + CC + CS·sources + CP·q(w) = 0</c>. Newton then solves for the ports and z together.
/// Everything here is computed exactly, so the constraints keep the exact cancellations of the circuit.
/// </summary>
internal sealed class SingularSplit
{
    public required int[] Free { get; init; }
    public required double[,] ParameterGain { get; init; }
    public required double[] Constant { get; init; }
    public required double[,] SourceGain { get; init; }
    public required double[,] G { get; init; }
    public required double[,] Z { get; init; }
    /// <summary>How each unknown moves with z.</summary>
    public required double[,] Nz { get; init; }
    /// <summary>How each port moves with z.</summary>
    public required double[,] Qz { get; init; }
    public required double[,] CH { get; init; }
    public required double[] CC { get; init; }
    public required double[,] CS { get; init; }
    public required double[,] CP { get; init; }

    public static SingularSplit Build(ExtractedSystem system)
    {
        int n = system.Unknowns.Length, parameters = system.Parameters.Length, sources = system.Sources.Length;
        int m = system.Nonlinear.Length, k = system.Ports;

        ExactSolver.RankBasis(system.ExactE, out int[] basisRows, out int[] basisColumns);
        int[] free = Enumerable.Range(0, n).Except(basisColumns).ToArray();
        int[] constraintRows = Enumerable.Range(0, system.Rows).Except(basisRows).ToArray();
        int r = basisRows.Length, d = free.Length;
        if (constraintRows.Length != d)
            throw new SingularSystemException($"E has rank {r}, which leaves {constraintRows.Length} constraints for {d} free unknowns.");

        // Right-hand sides, one column each: parameters, the constant, sources, nonlinear terms, then -E·z.
        int constantColumn = parameters, sourceColumn = parameters + 1, nonlinearColumn = sourceColumn + sources, freeColumn = nonlinearColumn + m;
        int width = freeColumn + d;
        Real Right(int row, int column)
        {
            if (column < parameters)
                return system.ExactH[row, column];
            if (column == constantColumn)
                return system.ExactC[row, 0];
            if (column < nonlinearColumn)
                return system.Sources[column - sourceColumn].Row == row ? 1 : 0;
            if (column < freeColumn)
                return system.ExactP[row, column - nonlinearColumn];
            return -system.ExactE[row, free[column - freeColumn]];
        }

        Real[,] block = ExactSolver.Zeros(r, r);
        Real[,] right = ExactSolver.Zeros(r, width);
        for (int i = 0; i < r; i++)
        {
            for (int j = 0; j < r; j++)
                block[i, j] = system.ExactE[basisRows[i], basisColumns[j]];
            for (int c = 0; c < width; c++)
                right[i, c] = Right(basisRows[i], c);
        }
        Real[,] x = ExactSolver.Solve(block, right);

        // The full solution, with the free unknowns' rows taken from z directly.
        Real[,] full = ExactSolver.Zeros(n, width);
        for (int i = 0; i < r; i++)
            for (int c = 0; c < width; c++)
                full[basisColumns[i], c] = x[i, c];
        for (int f = 0; f < d; f++)
            full[free[f], freeColumn + f] = 1;

        // Constraints: each remaining row, minus what the basis rows already account for.
        Real[,] constraints = ExactSolver.Zeros(d, width);
        for (int f = 0; f < d; f++)
        {
            int row = constraintRows[f];
            for (int c = 0; c < width; c++)
            {
                // Source and q columns: the row's right side minus E·y over the basis unknowns. Free columns:
                // the row's z coefficient plus E·y over the basis unknowns, which must cancel to zero.
                bool isFree = c >= freeColumn;
                Real value = isFree ? system.ExactE[row, free[c - freeColumn]] : Right(row, c);
                for (int i = 0; i < r; i++)
                {
                    Real e = system.ExactE[row, basisColumns[i]];
                    if (e != 0)
                        value = isFree ? value + e * x[i, c] : value - e * x[i, c];
                }
                constraints[f, c] = value;
            }
            for (int c = freeColumn; c < width; c++)
                if (constraints[f, c] != 0)
                    throw new SingularSystemException("A constraint depends on the free unknowns.");
        }

        Real[,] Product(Real[,] left, Real[,] rightMatrix, int first, int count)
        {
            int rows = left.GetLength(0), inner = left.GetLength(1);
            Real[,] result = ExactSolver.Zeros(rows, count);
            for (int i = 0; i < rows; i++)
                for (int c = 0; c < count; c++)
                {
                    Real value = 0;
                    for (int j = 0; j < inner; j++)
                        if (left[i, j] != 0 && rightMatrix[j, first + c] != 0)
                            value += left[i, j] * rightMatrix[j, first + c];
                    result[i, c] = value;
                }
            return result;
        }

        Real[,] exactQ = ExactSolver.Zeros(k, n);
        for (int p = 0; p < k; p++)
            for (int j = 0; j < n; j++)
                exactQ[p, j] = system.Q[p, j];

        double[,] Slice(Real[,] matrix, int first, int count, double[] rowScale)
        {
            int rows = matrix.GetLength(0);
            var result = new double[rows, count];
            for (int i = 0; i < rows; i++)
                for (int c = 0; c < count; c++)
                    result[i, c] = (double)(matrix[i, first + c] * rowScale[i]);
            return result;
        }

        double[] unit = Enumerable.Repeat(1.0, n).ToArray();

        // Scale each constraint so its largest nonlinear coefficient is 1. That doesn't change the solution,
        // and it keeps the rows comparable for pivoting.
        var scale = new double[d];
        for (int f = 0; f < d; f++)
        {
            Real max = 0;
            for (int c = 0; c < m; c++)
                if (Real.Abs(constraints[f, nonlinearColumn + c]) > max)
                    max = Real.Abs(constraints[f, nonlinearColumn + c]);
            scale[f] = max != 0 ? (double)((Real)1 / max) : 1;
        }
        Real[,] exactScaled = (Real[,])constraints.Clone();
        for (int f = 0; f < d; f++)
            for (int c = 0; c < width; c++)
                exactScaled[f, c] = constraints[f, c] * (Real)scale[f];
        double[] ones = Enumerable.Repeat(1.0, d).ToArray();

        return new SingularSplit
        {
            Free = free,
            ParameterGain = Slice(full, 0, parameters, unit),
            Constant = Enumerable.Range(0, n).Select(i => (double)full[i, constantColumn]).ToArray(),
            SourceGain = Slice(full, sourceColumn, sources, unit),
            G = Slice(full, nonlinearColumn, m, unit),
            Nz = Slice(full, freeColumn, d, unit),
            Z = Slice(Product(exactQ, full, nonlinearColumn, m), 0, m, Enumerable.Repeat(1.0, k).ToArray()),
            Qz = Slice(Product(exactQ, full, freeColumn, d), 0, d, Enumerable.Repeat(1.0, k).ToArray()),
            CH = Slice(exactScaled, 0, parameters, ones),
            CC = Enumerable.Range(0, d).Select(f => (double)exactScaled[f, constantColumn]).ToArray(),
            CS = Slice(exactScaled, sourceColumn, sources, ones),
            CP = Slice(exactScaled, nonlinearColumn, m, ones),
        };
    }
}
