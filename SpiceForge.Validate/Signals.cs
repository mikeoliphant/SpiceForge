/// <summary>Test signals and error measures shared by the validation modes.</summary>
internal static class Signals
{
    /// <summary>LiveSPICE's own test waveform: a sum of harmonics of <paramref name="f0"/>.</summary>
    internal static double Harmonics(double t, double amplitude, double f0, int count)
    {
        double s = 0;
        for (int i = 1; i <= count; ++i)
            s += Math.Sin(t * f0 * 2 * 3.1415 * i) / count;
        return amplitude * s;
    }

    /// <summary>The signal with roundoff-sized random noise on every sample, for sensitivity controls.</summary>
    internal static double[] WithNoise(double[] signal, double amount)
    {
        var random = new Random(7);
        return signal.Select(x => x * (1 + amount * (random.NextDouble() * 2 - 1))).ToArray();
    }

    /// <summary>
    /// The worst error across all nodes, relative to that node's peak. Nodes that sit near zero are measured against the
    /// circuit's peak instead, so roundoff on them isn't reported as a large error.
    /// </summary>
    internal static double WorstErrorDb(double[][] reference, double[][] actual)
    {
        double circuitPeak = reference.Max(o => o.Max(Math.Abs));
        double floor = Math.Max(circuitPeak * 1e-6, 1e-12);
        double worst = 0;
        for (int o = 0; o < reference.Length; o++)
        {
            double peak = Math.Max(reference[o].Max(Math.Abs), floor);
            for (int n = 0; n < reference[o].Length; n++)
            {
                double difference = Math.Abs(reference[o][n] - actual[o][n]);
                if (double.IsNaN(difference))
                    return double.PositiveInfinity;
                worst = Math.Max(worst, difference / peak);
            }
        }
        return worst == 0 ? double.NegativeInfinity : 20 * Math.Log10(worst);
    }
}
