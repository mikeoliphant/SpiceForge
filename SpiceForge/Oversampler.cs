namespace SpiceForge;

/// <summary>
/// Converts between the base sample rate and an engine's step rate, <see cref="Factor"/> times higher: the input up
/// before the steps run, and each output down after. The engines step once per input value and know nothing about
/// the base rate, so how a circuit is oversampled can be changed without touching them.
/// </summary>
/// <remarks>An instance belongs to one engine: it may keep state between blocks, for the input and for each output.</remarks>
public abstract class Oversampler
{
    protected Oversampler(int factor)
    {
        if (factor < 1)
            throw new ArgumentOutOfRangeException(nameof(factor), "The oversampling factor must be at least 1.");
        Factor = factor;
    }

    /// <summary>Steps per base-rate sample.</summary>
    public int Factor { get; }

    /// <summary>How many whole base-rate samples the output lags the input by, from the resampling alone (a host
    /// reports it so it can line the output up). Any fraction of a sample isn't counted.</summary>
    public virtual int Latency => 0;

    /// <summary>Writes the step-rate input for <paramref name="samples"/>: <see cref="Factor"/> values per sample,
    /// into the first <c>samples.Length * Factor</c> entries of <paramref name="steps"/>.</summary>
    public abstract void Upsample(ReadOnlySpan<double> samples, Span<double> steps);

    /// <summary>Writes one base-rate sample of output <paramref name="output"/> for each <see cref="Factor"/> values of
    /// <paramref name="steps"/>.</summary>
    public abstract void Downsample(int output, ReadOnlySpan<double> steps, Span<double> samples);
}

/// <summary>
/// LiveSPICE's oversampling: the input is interpolated linearly from one sample to the next over the steps between
/// them, and each output sample is the average of its steps. The arithmetic is LiveSPICE's own, step for step, so the
/// result is bit-identical to running the steps inside its simulation loop.
/// </summary>
/// <remarks>Interpolating then averaging rolls the response off as sinc³(f/fs), and the average rejects harmonics that
/// fold back near the top of the band only weakly; see the Oversampling section of LessonsLearned.md.</remarks>
public sealed class LinearOversampler : Oversampler
{
    private readonly double inverseFactor;
    // The input at the last step: where interpolation to the next sample starts. Accumulated rather than recomputed,
    // as LiveSPICE does, so it may differ from the last sample by roundoff.
    private double input;

    public LinearOversampler(int factor) : base(factor)
    {
        inverseFactor = 1.0 / factor;
    }

    public override void Upsample(ReadOnlySpan<double> samples, Span<double> steps)
    {
        int factor = Factor;
        for (int n = 0, s = 0; n < samples.Length; n++)
        {
            double delta = (samples[n] - input) * inverseFactor;
            for (int j = 0; j < factor; j++, s++)
            {
                input += delta;
                steps[s] = input;
            }
        }
    }

    public override void Downsample(int output, ReadOnlySpan<double> steps, Span<double> samples)
    {
        int factor = Factor;
        for (int n = 0, s = 0; n < samples.Length; n++)
        {
            double sum = 0;
            for (int j = 0; j < factor; j++, s++)
                sum += steps[s];
            samples[n] = sum * inverseFactor;
        }
    }
}

/// <summary>
/// Oversampling with windowed-sinc filters: a polyphase interpolator on the way up and a decimating lowpass on each
/// output. Both pass to 0.417·fs (20 kHz at 48 kHz) and stop from 0.583·fs, so harmonics that would fold back into the
/// band are rejected by about <c>attenuation</c> dB, and the band isn't rolled off the way linear interpolation and
/// averaging roll it off. The cost is <see cref="Latency"/>: each filter delays by a whole number of base samples.
/// </summary>
/// <remarks>It doesn't match LiveSPICE. It allocates nothing once each buffer has grown to the largest block.</remarks>
public sealed class SincOversampler : Oversampler
{
    private const double PassFraction = 20.0 / 48;
    private const double StopFraction = 28.0 / 48;

    // The filter at the step rate: 2·half + 1 taps, centered on tap half, with half a multiple of the factor so its
    // delay is a whole number of base samples.
    private readonly double[] taps;
    private readonly int half;
    private readonly int delay;

    // Upsampling: for each step j within a sample, the taps that apply to the span base samples up to it, oldest first.
    private readonly double[][] phases;
    private readonly int span;
    // The last span - 1 base samples, followed by the block being upsampled.
    private double[] upWork;

    // Downsampling, per output: the last 2·half steps, followed by the block being downsampled.
    private double[][] downWork = Array.Empty<double[]>();

    public SincOversampler(int factor, double attenuation = 100) : base(factor)
    {
        double width = (StopFraction - PassFraction) / factor;       // the transition, as a fraction of the step rate
        double beta = attenuation > 50 ? 0.1102 * (attenuation - 8.7) : 0.5842 * Math.Pow(attenuation - 21, 0.4) + 0.07886 * (attenuation - 21);
        int length = (int)Math.Ceiling((attenuation - 8) / (2.285 * 2 * Math.PI * width));
        // With no oversampling there's nothing to filter: one tap, no delay.
        delay = factor == 1 ? 0 : (length / 2 + factor - 1) / factor;
        half = delay * factor;
        double cutoff = (PassFraction + StopFraction) / 2 / factor;  // cycles per step
        taps = new double[2 * half + 1];
        double i0Beta = BesselI0(beta);
        for (int k = -half; k <= half; k++)
        {
            double sinc = k == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * k) / (Math.PI * k);
            double r = half == 0 ? 0 : (double)k / half;
            taps[k + half] = sinc * BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0Beta;
        }
        double sum = taps.Sum();
        for (int k = 0; k < taps.Length; k++)
            taps[k] /= sum;

        // Step j of sample n is at step index (n - 1 - delay)·factor + j + 1: the interpolated input delay samples
        // back. The base samples reaching it are n, n - 1, ..., n - span + 1.
        span = 2 * delay + 1;
        phases = new double[factor][];
        for (int j = 0; j < factor; j++)
        {
            phases[j] = new double[span];
            for (int i = 0; i < span; i++)
            {
                // Base sample n - i sits at step index (n - i)·factor; its distance from this step, in steps:
                int distance = (-1 - delay) * factor + j + 1 + i * factor;
                int t = distance + half;
                phases[j][span - 1 - i] = t >= 0 && t < taps.Length ? factor * taps[t] : 0;
            }
        }
        upWork = new double[span - 1];
    }

    /// <summary>The delay through both filters, in base samples.</summary>
    public override int Latency => 2 * delay;

    public override void Upsample(ReadOnlySpan<double> samples, Span<double> steps)
    {
        int history = span - 1;
        if (upWork.Length < history + samples.Length)
            upWork = Grow(upWork, history + samples.Length, history);
        samples.CopyTo(upWork.AsSpan(history));
        int factor = Factor;
        for (int n = 0, s = 0; n < samples.Length; n++)
        {
            ReadOnlySpan<double> window = upWork.AsSpan(n, span);
            for (int j = 0; j < factor; j++, s++)
                steps[s] = Dot(phases[j], window);
        }
        // Keep the newest samples for the next block.
        upWork.AsSpan(samples.Length, history).CopyTo(upWork);
    }

    public override void Downsample(int output, ReadOnlySpan<double> steps, Span<double> samples)
    {
        int history = 2 * half;
        if (output >= downWork.Length)
            downWork = GrowOutputs(output + 1, history);
        double[] work = downWork[output];
        if (work.Length < history + steps.Length)
            work = downWork[output] = Grow(work, history + steps.Length, history);
        steps.CopyTo(work.AsSpan(history));
        int factor = Factor;
        for (int n = 0; n < samples.Length; n++)
        {
            // Centered half steps back from the last step of sample n, delay samples back in time. The taps are
            // symmetric, so they apply to the window in either order.
            int last = history + (n + 1) * factor - 1;
            samples[n] = Dot(taps, work.AsSpan(last - 2 * half, taps.Length));
        }
        work.AsSpan(steps.Length, history).CopyTo(work);
    }

    // A larger buffer keeping the first `keep` values (the history). Separate methods, so the common path has no
    // allocation or closure in it.
    private static double[] Grow(double[] buffer, int size, int keep)
    {
        var grown = new double[size];
        Array.Copy(buffer, grown, keep);
        return grown;
    }

    private double[][] GrowOutputs(int count, int history)
    {
        var grown = new double[count][];
        for (int o = 0; o < count; o++)
            grown[o] = o < downWork.Length ? downWork[o] : new double[history];
        return grown;
    }

    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        var sum = System.Numerics.Vector<double>.Zero;
        int width = System.Numerics.Vector<double>.Count, i = 0;
        for (; i <= a.Length - width; i += width)
            sum += new System.Numerics.Vector<double>(a.Slice(i)) * new System.Numerics.Vector<double>(b.Slice(i));
        double total = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++)
            total += a[i] * b[i];
        return total;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 60; k++)
        {
            term *= x * x / (4.0 * k * k);
            sum += term;
        }
        return sum;
    }
}
