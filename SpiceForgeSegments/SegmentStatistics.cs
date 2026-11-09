/// <summary>Running ranges, means and covariance of a segment's inputs, over every step.</summary>
internal sealed class SegmentStatistics
{
    private readonly int inputs;
    private readonly double[] sum;
    private readonly double[,] products;

    public long Steps { get; private set; }
    public long CappedSteps { get; private set; }
    public double[] InputMin { get; }
    public double[] InputMax { get; }
    public double[] OutputMin { get; }
    public double[] OutputMax { get; }

    public SegmentStatistics(int inputs, int outputs)
    {
        this.inputs = inputs;
        sum = new double[inputs];
        products = new double[inputs, inputs];
        InputMin = Enumerable.Repeat(double.PositiveInfinity, inputs).ToArray();
        InputMax = Enumerable.Repeat(double.NegativeInfinity, inputs).ToArray();
        OutputMin = Enumerable.Repeat(double.PositiveInfinity, outputs).ToArray();
        OutputMax = Enumerable.Repeat(double.NegativeInfinity, outputs).ToArray();
    }

    public void Add(ReadOnlySpan<double> input, ReadOnlySpan<double> output, bool capped)
    {
        Steps++;
        if (capped)
            CappedSteps++;
        for (int i = 0; i < inputs; i++)
        {
            double x = input[i];
            sum[i] += x;
            InputMin[i] = Math.Min(InputMin[i], x);
            InputMax[i] = Math.Max(InputMax[i], x);
            for (int j = 0; j <= i; j++)
                products[i, j] += x * input[j];
        }
        for (int o = 0; o < output.Length; o++)
        {
            OutputMin[o] = Math.Min(OutputMin[o], output[o]);
            OutputMax[o] = Math.Max(OutputMax[o], output[o]);
        }
    }

    public double[] Mean => sum.Select(s => s / Math.Max(Steps, 1)).ToArray();

    /// <summary>Standard deviation of each input.</summary>
    public double[] Deviation
    {
        get
        {
            double[] mean = Mean;
            return Enumerable.Range(0, inputs).Select(i => Math.Sqrt(Math.Max(products[i, i] / Math.Max(Steps, 1) - mean[i] * mean[i], 0))).ToArray();
        }
    }

    /// <summary>Inputs that never change, which a box can treat as constants.</summary>
    public int ConstantInputs => Deviation.Count(d => d == 0);

    /// <summary>
    /// Eigenvalues of the inputs' correlation matrix, largest first. Each input is standardized first, so inputs on
    /// very different scales (an op-amp's open-circuit output can reach 1e5 V) count equally. Inputs that move
    /// together count once, so the number of significant eigenvalues is how many dimensions a box really needs.
    /// Constant inputs contribute zero.
    /// </summary>
    public double[] Spectrum()
    {
        double[] mean = Mean, deviation = Deviation;
        var correlation = new double[inputs, inputs];
        for (int i = 0; i < inputs; i++)
            for (int j = 0; j <= i; j++)
            {
                double covariance = products[i, j] / Math.Max(Steps, 1) - mean[i] * mean[j];
                double scale = deviation[i] * deviation[j];
                correlation[i, j] = correlation[j, i] = scale > 0 ? covariance / scale : 0;
            }
        return Jacobi(correlation).OrderByDescending(v => v).ToArray();
    }

    public int EffectiveDimension(double relative)
    {
        double[] spectrum = Spectrum();
        if (spectrum.Length == 0 || spectrum[0] <= 0)
            return 0;
        return spectrum.Count(v => v > spectrum[0] * relative);
    }

    // Cyclic Jacobi rotation: fine for the few inputs a segment has.
    private static double[] Jacobi(double[,] a)
    {
        int n = a.GetLength(0);
        var m = (double[,])a.Clone();
        for (int sweep = 0; sweep < 100; sweep++)
        {
            double off = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    off += m[i, j] * m[i, j];
            if (off < 1e-30)
                break;

            for (int p = 0; p < n; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300)
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
                }
        }
        return Enumerable.Range(0, n).Select(i => m[i, i]).ToArray();
    }
}
