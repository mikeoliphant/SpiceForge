using ComputerAlgebra;

namespace SpiceForge;

/// <summary>A source term of a row that is not linear in the parameters, such as Sin[t].</summary>
public sealed record NonlinearSource(int Row, double Coefficient, Expression Body);

/// <summary>
/// One discretized timestep of a circuit, separated into linear and nonlinear parts:
/// <c>E·y = H·h + C + (nonlinear sources) + P·q(w)</c>, with <c>w = Q·y</c>.
/// </summary>
public sealed class ExtractedSystem
{
    /// <summary>Unknowns at the current timestep (columns of E and Q).</summary>
    public required Expression[] Unknowns { get; init; }

    /// <summary>Everything else the equations read: inputs, past values and time (columns of H).</summary>
    public required Expression[] Parameters { get; init; }

    /// <summary>Nonlinear terms, each with its constant factor moved into P.</summary>
    public required Expression[] Nonlinear { get; init; }

    public required double[,] E { get; init; }
    public required double[,] H { get; init; }
    public required double[] C { get; init; }
    public required double[,] P { get; init; }
    public required double[,] Q { get; init; }

    /// <summary>Unknowns that appear in no equation, left out of <see cref="Unknowns"/>.</summary>
    public required Expression[] FreeUnknowns { get; init; }
    public required NonlinearSource[] Sources { get; init; }

    /// <summary>The original equations, each meaning <c>F == 0</c>.</summary>
    public required Expression[] Equations { get; init; }

    /// <summary>The DC steady state, found the same way LiveSPICE finds it.</summary>
    public required Arrow[] InitialConditions { get; init; }

    public int Rows => Equations.Length;
    public int Ports => Q.GetLength(0);
}
