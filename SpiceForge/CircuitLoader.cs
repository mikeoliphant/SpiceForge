using Circuit;
using ComputerAlgebra;
using Util;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>A schematic loaded and extracted, ready to build an engine from.</summary>
public sealed class LoadedCircuit
{
    /// <summary>The circuit the system was extracted from: with dynamic pots, the one whose pots were replaced.</summary>
    public required Circuit.Circuit Circuit { get; init; }

    /// <summary>The extracted system the engines run.</summary>
    public required ExtractedSystem System { get; init; }

    /// <summary>
    /// LiveSPICE's solution of the circuit with its pots at their schematic positions. The engines use it for their
    /// stopping rule and DC start, and LiveSPICE's own Simulation can run it for comparison.
    /// </summary>
    public required TransientSolution Solution { get; init; }

    /// <summary>The runtime parameters, one per pot or variable resistor; empty when pots are baked in.</summary>
    public required IReadOnlyList<PotParameter> Pots { get; init; }
}

/// <summary>Loads schematics the way every tool should, with pots as runtime parameters unless asked otherwise.</summary>
public static class CircuitLoader
{
    /// <summary>
    /// Loads and extracts a schematic at timestep <paramref name="h"/>. By default every pot and variable resistor
    /// becomes a runtime parameter (see <see cref="DynamicPots"/>), so pots can move while the circuit runs; with
    /// <paramref name="dynamicPots"/> false they are baked in at their schematic positions.
    /// </summary>
    public static LoadedCircuit Load(string schematicPath, Expression h, bool dynamicPots = true, ILog? log = null)
    {
        log ??= new NullLog();
        Circuit.Circuit baked = Build(schematicPath, log);
        TransientSolution solution = TransientSolution.Solve(baked.Analyze(), h);

        Circuit.Circuit circuit = baked;
        IReadOnlyList<PotParameter> pots = Array.Empty<PotParameter>();
        if (dynamicPots)
        {
            // LiveSPICE's solve needs the pots baked in, so the dynamic circuit is a second copy.
            circuit = Build(schematicPath, log);
            pots = DynamicPots.Replace(circuit);
            if (pots.Count == 0)
                circuit = baked;
        }

        return new LoadedCircuit
        {
            Circuit = circuit,
            System = Extractor.Extract(circuit.Analyze(), h, pots),
            Solution = solution,
            Pots = pots,
        };
    }

    private static Circuit.Circuit Build(string schematicPath, ILog log)
    {
        Circuit.Circuit circuit = Schematic.Load(schematicPath, log).Build();
        circuit.Name = Path.GetFileNameWithoutExtension(schematicPath);
        return circuit;
    }
}
