namespace SpiceForge;

/// <summary>
/// A candidate black box: one coupled group of the circuit's Newton solve. Each step, the group's outputs are a
/// fixed function of its inputs, with no memory of earlier steps:
/// <list type="bullet">
/// <item>inputs: the open-circuit value of each of its ports (voc), the constant part of each of its constraint
/// rows (only when E is singular), the converged values of any upstream ports it reads, and the values of any
/// runtime parameters (dynamic pots) it reads;</item>
/// <item>outputs: the converged value of each of its ports, then each of its free unknowns, then the value of each of
/// its nonlinear terms. Every unknown in the circuit follows linearly from the nonlinear-term values.</item>
/// </list>
/// Everything else in the circuit, including all state, stays outside the box.
/// </summary>
public sealed class Segment
{
    public required int Index { get; init; }

    /// <summary>Port indices this segment solves for.</summary>
    public required int[] Ports { get; init; }

    /// <summary>Free-unknown indices this segment solves for (into the circuit's free unknowns).</summary>
    public required int[] Free { get; init; }

    /// <summary>Constraint rows this segment includes (the same indices as <see cref="Free"/>).</summary>
    public required int[] Constraints { get; init; }

    /// <summary>Ports of earlier segments this segment reads.</summary>
    public required int[] Upstream { get; init; }

    /// <summary>Nonlinear terms this segment computes: those that read its ports and no later segment's.</summary>
    public required int[] NonlinearTerms { get; init; }

    /// <summary>Runtime parameters (dynamic pots) that change this segment's map from inputs to outputs, as indices into
    /// ExtractedSystem.RuntimeParameters: on the reduced path, those that change its Newton coefficients. Pots that only
    /// change the linear network around the solve change what the segment is fed, not the segment. They come last
    /// among the inputs.</summary>
    public required int[] Parameters { get; init; }

    /// <summary>For each of <see cref="Parameters"/>, the largest change it makes to the segment's Newton coefficients
    /// across its travel, relative to their largest entry. A box may treat one that is negligible as fixed.</summary>
    public double[] ParameterSensitivity { get; init; } = Array.Empty<double>();

    public required string[] InputNames { get; init; }
    public required string[] OutputNames { get; init; }

    public int InputCount => Ports.Length + Constraints.Length + Upstream.Length + Parameters.Length;
    public int OutputCount => Ports.Length + Free.Length + NonlinearTerms.Length;
}

/// <summary>Receives each segment's inputs and converged outputs on every step.</summary>
public interface ISegmentObserver
{
    void Observe(int step, Segment segment, ReadOnlySpan<double> inputs, ReadOnlySpan<double> outputs, int iterations, bool capped);
}

/// <summary>A black box that computes a segment's outputs from its inputs, in place of Newton's method.</summary>
public interface ISegmentSolver
{
    void Solve(Segment segment, ReadOnlySpan<double> inputs, Span<double> outputs);
}
