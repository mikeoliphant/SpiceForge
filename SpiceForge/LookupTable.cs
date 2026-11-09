namespace SpiceForge;

/// <summary>
/// A black box for a low-dimensional segment: a grid of exact solutions over the affine subspace its inputs
/// occupy, interpolated linearly (one dimension) or bilinearly (two). The subspace and its range come from sample
/// inputs; the grid values come from solving the segment's own equations at each point.
/// </summary>
public sealed class LookupTable
{
    private readonly double[] mean;
    private readonly double[][] axes;
    private readonly double[] low;
    private readonly double[] step;
    private readonly int[] size;
    private readonly double[][] values;
    private readonly int outputs;

    public Segment Segment { get; }
    public int Dimensions => axes.Length;
    public int Resolution => size.Length == 0 ? 0 : size[0];

    /// <summary>Largest distance from a sample input to the subspace, relative to the inputs' scale.</summary>
    public double ProjectionResidual { get; }

    /// <summary>Grid points whose Newton solve did not converge; they hold their nearest neighbour's value.</summary>
    public int FailedPoints { get; }

    /// <summary>Lookups that fell outside the table, and were clamped to its edge.</summary>
    public long OutOfRange { get; private set; }

    private LookupTable(Segment segment, double[] mean, double[][] axes, double[] low, double[] step, int[] size,
        double[][] values, double residual, int failed)
    {
        Segment = segment;
        this.mean = mean;
        this.axes = axes;
        this.low = low;
        this.step = step;
        this.size = size;
        this.values = values;
        outputs = segment.OutputCount;
        ProjectionResidual = residual;
        FailedPoints = failed;
    }

    /// <summary>
    /// Builds a table for a segment, or returns null with the reason when its inputs need more than
    /// <paramref name="maxDimensions"/> dimensions to stay within <paramref name="tolerance"/> of the samples.
    /// </summary>
    /// <param name="solver">An interpreter for the circuit, used only to solve the segment at grid points.</param>
    /// <param name="sampleInputs">Representative inputs, for example recorded from a run on typical audio.</param>
    /// <param name="sampleOutputs">The outputs at those inputs, used as starting guesses.</param>
    public static LookupTable? Build(CircuitInterpreter solver, Segment segment, IReadOnlyList<double[]> sampleInputs,
        IReadOnlyList<double[]> sampleOutputs, int resolution, out string reason, int maxDimensions = 2,
        double tolerance = 1e-9, double margin = 0.1)
    {
        int n = segment.InputCount;
        Pca pca = Pca.Of(sampleInputs, n);

        // The inputs' scale, for judging the projection residual.
        double scale = 0;
        foreach (double[] x in sampleInputs)
            for (int i = 0; i < n; i++)
                scale = Math.Max(scale, Math.Abs(x[i] - pca.Mean[i]));
        scale = Math.Max(scale, 1e-300);

        int dimensions = -1;
        double residual = double.PositiveInfinity;
        for (int d = 0; d <= Math.Min(maxDimensions, n); d++)
        {
            residual = Residual(sampleInputs, pca.Mean, pca.Vectors.Take(d).ToArray()) / scale;
            if (residual <= tolerance)
            {
                dimensions = d;
                break;
            }
        }
        if (dimensions < 0)
        {
            reason = $"its inputs need more than {maxDimensions} dimension(s): with {maxDimensions}, they are still {residual:E1} of their scale from the samples.";
            return null;
        }

        double[][] axes = pca.Vectors.Take(dimensions).ToArray();
        var low = new double[dimensions];
        var high = new double[dimensions];
        for (int a = 0; a < dimensions; a++)
        {
            low[a] = double.PositiveInfinity;
            high[a] = double.NegativeInfinity;
            foreach (double[] x in sampleInputs)
            {
                double s = Project(x, pca.Mean, axes[a]);
                low[a] = Math.Min(low[a], s);
                high[a] = Math.Max(high[a], s);
            }
            double span = Math.Max(high[a] - low[a], 1e-12);
            low[a] -= span * margin;
            high[a] += span * margin;
        }

        int[] size = Enumerable.Repeat(dimensions == 0 ? 1 : resolution, dimensions).ToArray();
        double[] step = Enumerable.Range(0, dimensions).Select(a => (high[a] - low[a]) / (size[a] - 1)).ToArray();
        int points = size.Aggregate(1, (p, s) => p * s);
        var values = new double[points][];

        // Visit grid points in an order where each has a solved neighbour, starting from the one nearest the
        // samples' centre, with that sample's output as the first guess.
        var order = GridOrder(size, low, step);
        double[] firstGuess = sampleOutputs[Nearest(sampleInputs, pca.Mean, axes, order.Count > 0 ? PointCoordinates(order[0].Point, size, low, step) : Array.Empty<double>())];
        int failed = 0;
        var input = new double[n];
        foreach (var (point, neighbour) in order)
        {
            double[] coordinates = PointCoordinates(point, size, low, step);
            for (int i = 0; i < n; i++)
            {
                double x = pca.Mean[i];
                for (int a = 0; a < dimensions; a++)
                    x += axes[a][i] * coordinates[a];
                input[i] = x;
            }

            double[] guess = neighbour < 0 ? firstGuess : values[neighbour];
            var output = new double[segment.OutputCount];
            if (!solver.SolveSegment(segment, input, output, guess))
            {
                failed++;
                Array.Copy(guess, output, output.Length);
            }
            values[point] = output;
        }

        reason = "";
        return new LookupTable(segment, pca.Mean, axes, low, step, size, values, residual, failed);
    }

    /// <summary>Interpolates the segment's outputs at these inputs.</summary>
    public void Lookup(ReadOnlySpan<double> inputs, Span<double> result)
    {
        int d = axes.Length;
        if (d == 0)
        {
            values[0].CopyTo(result);
            return;
        }

        Span<int> cell = stackalloc int[2];
        Span<double> fraction = stackalloc double[2];
        for (int a = 0; a < d; a++)
        {
            double s = 0;
            double[] axis = axes[a];
            for (int i = 0; i < axis.Length; i++)
                s += axis[i] * (inputs[i] - mean[i]);
            double position = (s - low[a]) / step[a];
            if (position < 0 || position > size[a] - 1)
            {
                OutOfRange++;
                position = Math.Clamp(position, 0, size[a] - 1);
            }
            int index = Math.Min((int)position, size[a] - 2);
            cell[a] = index;
            fraction[a] = position - index;
        }

        if (d == 1)
        {
            double[] a0 = values[cell[0]], a1 = values[cell[0] + 1];
            double f = fraction[0];
            for (int o = 0; o < outputs; o++)
                result[o] = a0[o] + (a1[o] - a0[o]) * f;
            return;
        }

        int row = size[1];
        double[] v00 = values[cell[0] * row + cell[1]], v01 = values[cell[0] * row + cell[1] + 1];
        double[] v10 = values[(cell[0] + 1) * row + cell[1]], v11 = values[(cell[0] + 1) * row + cell[1] + 1];
        double fx = fraction[0], fy = fraction[1];
        for (int o = 0; o < outputs; o++)
        {
            double top = v00[o] + (v01[o] - v00[o]) * fy;
            double bottom = v10[o] + (v11[o] - v10[o]) * fy;
            result[o] = top + (bottom - top) * fx;
        }
    }

    private static double Project(double[] x, double[] mean, double[] axis)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++)
            s += axis[i] * (x[i] - mean[i]);
        return s;
    }

    private static double Residual(IReadOnlyList<double[]> samples, double[] mean, double[][] axes)
    {
        double worst = 0;
        int n = mean.Length;
        var coordinates = new double[axes.Length];
        foreach (double[] x in samples)
        {
            for (int a = 0; a < axes.Length; a++)
                coordinates[a] = Project(x, mean, axes[a]);
            for (int i = 0; i < n; i++)
            {
                double reconstructed = mean[i];
                for (int a = 0; a < axes.Length; a++)
                    reconstructed += axes[a][i] * coordinates[a];
                worst = Math.Max(worst, Math.Abs(x[i] - reconstructed));
            }
        }
        return worst;
    }

    private static double[] PointCoordinates(int point, int[] size, double[] low, double[] step)
    {
        var coordinates = new double[size.Length];
        for (int a = size.Length - 1; a >= 0; a--)
        {
            coordinates[a] = low[a] + step[a] * (point % size[a]);
            point /= size[a];
        }
        return coordinates;
    }

    private static int Nearest(IReadOnlyList<double[]> samples, double[] mean, double[][] axes, double[] coordinates)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int k = 0; k < samples.Count; k++)
        {
            double distance = 0;
            for (int a = 0; a < axes.Length; a++)
            {
                double difference = Project(samples[k], mean, axes[a]) - coordinates[a];
                distance += difference * difference;
            }
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = k;
            }
        }
        return best;
    }

    // Breadth-first from the grid point nearest the origin of the subspace, so each point after the first has an
    // already-solved neighbour to start Newton from.
    private static List<(int Point, int Neighbour)> GridOrder(int[] size, double[] low, double[] step)
    {
        int d = size.Length;
        int points = size.Aggregate(1, (p, s) => p * s);
        var order = new List<(int, int)>(points);
        if (d == 0)
        {
            order.Add((0, -1));
            return order;
        }

        var start = new int[d];
        for (int a = 0; a < d; a++)
            start[a] = Math.Clamp((int)Math.Round(-low[a] / step[a]), 0, size[a] - 1);
        int Index(int[] c) { int index = 0; for (int a = 0; a < d; a++) index = index * size[a] + c[a]; return index; }

        var visited = new bool[points];
        var queue = new Queue<int>();
        int first = Index(start);
        visited[first] = true;
        queue.Enqueue(first);
        order.Add((first, -1));
        var c = new int[d];
        while (queue.Count > 0)
        {
            int point = queue.Dequeue();
            int rest = point;
            for (int a = d - 1; a >= 0; a--)
            {
                c[a] = rest % size[a];
                rest /= size[a];
            }
            for (int a = 0; a < d; a++)
                foreach (int delta in new[] { -1, 1 })
                {
                    int value = c[a] + delta;
                    if (value < 0 || value >= size[a])
                        continue;
                    c[a] = value;
                    int next = Index(c);
                    c[a] -= delta;
                    if (visited[next])
                        continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                    order.Add((next, point));
                }
        }
        return order;
    }
}

/// <summary>Uses lookup tables as the black boxes for their segments.</summary>
public sealed class LookupTableSolver : ISegmentSolver
{
    private readonly Dictionary<int, LookupTable> tables;

    public LookupTableSolver(IEnumerable<LookupTable> tables) => this.tables = tables.ToDictionary(t => t.Segment.Index);

    public void Solve(Segment segment, ReadOnlySpan<double> inputs, Span<double> outputs) => tables[segment.Index].Lookup(inputs, outputs);
}

/// <summary>Mean and principal axes of a set of vectors, axes in order of decreasing variance.</summary>
internal sealed class Pca
{
    public required double[] Mean { get; init; }
    public required double[][] Vectors { get; init; }
    public required double[] Values { get; init; }

    public static Pca Of(IReadOnlyList<double[]> samples, int n)
    {
        var mean = new double[n];
        foreach (double[] x in samples)
            for (int i = 0; i < n; i++)
                mean[i] += x[i];
        for (int i = 0; i < n; i++)
            mean[i] /= Math.Max(samples.Count, 1);

        var covariance = new double[n, n];
        foreach (double[] x in samples)
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                    covariance[i, j] += (x[i] - mean[i]) * (x[j] - mean[j]);
        for (int i = 0; i < n; i++)
            for (int j = 0; j <= i; j++)
                covariance[j, i] = covariance[i, j] /= Math.Max(samples.Count, 1);

        (double[] values, double[,] vectors) = Jacobi(covariance);
        int[] order = Enumerable.Range(0, n).OrderByDescending(i => values[i]).ToArray();
        return new Pca
        {
            Mean = mean,
            Values = order.Select(i => values[i]).ToArray(),
            Vectors = order.Select(i => Enumerable.Range(0, n).Select(r => vectors[r, i]).ToArray()).ToArray(),
        };
    }

    // Cyclic Jacobi rotations, accumulating the eigenvectors as columns.
    private static (double[] Values, double[,] Vectors) Jacobi(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        var v = new double[n, n];
        for (int i = 0; i < n; i++)
            v[i, i] = 1;

        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0, diagonal = 0;
            for (int i = 0; i < n; i++)
            {
                diagonal += m[i, i] * m[i, i];
                for (int j = i + 1; j < n; j++)
                    off += m[i, j] * m[i, j];
            }
            if (off <= 1e-30 * Math.Max(diagonal, 1e-300))
                break;

            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (m[p, q] == 0)
                        continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < n; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        return (Enumerable.Range(0, n).Select(i => m[i, i]).ToArray(), v);
    }
}
