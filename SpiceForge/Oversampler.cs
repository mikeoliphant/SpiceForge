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
