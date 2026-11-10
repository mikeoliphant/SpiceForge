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
    /// <paramref name="dynamicPots"/> false they are baked in. <paramref name="potPositions"/> sets wiper positions
    /// (0 to 1) in place of the schematic's, by pot name or group (case-insensitive); see <see cref="SetPots"/>.
    /// </summary>
    public static LoadedCircuit Load(string schematicPath, Expression h, bool dynamicPots = true, ILog? log = null,
        IReadOnlyDictionary<string, double>? potPositions = null)
    {
        log ??= new NullLog();
        Circuit.Circuit Build()
        {
            Circuit.Circuit built = BuildSchematic(schematicPath, log);
            if (potPositions != null)
                SetPots(built, potPositions);
            return built;
        }

        Circuit.Circuit baked = Build();
        TransientSolution solution = TransientSolution.Solve(baked.Analyze(), h);

        Circuit.Circuit circuit = baked;
        IReadOnlyList<PotParameter> pots = Array.Empty<PotParameter>();
        if (dynamicPots)
        {
            // LiveSPICE's solve needs the pots baked in, so the dynamic circuit is a second copy.
            circuit = Build();
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

    /// <summary>
    /// Sets wiper positions (0 to 1) by pot name or group, case-insensitively, including inside subcircuits. A group
    /// sets every pot in it, so ganged pots move together. Throws if a name matches no pot or a position is out of range.
    /// </summary>
    public static void SetPots(Circuit.Circuit circuit, IReadOnlyDictionary<string, double> positions)
    {
        var pots = PotsOf(circuit).ToList();
        foreach ((string name, double position) in positions)
        {
            if (!(position >= 0 && position <= 1))
                throw new ArgumentException($"Pot '{name}' position {position} isn't between 0 and 1.");
            var matches = pots.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(p.Group) && string.Equals(p.Group, name, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0)
                throw new ArgumentException($"No pot or pot group is named '{name}'. The pots are: "
                    + (pots.Count == 0 ? "(none)" : string.Join(", ", PotNames(circuit))) + ".");
            foreach (var pot in matches)
                pot.Control.PotValue = position;
        }
    }

    /// <summary>The names to set pots by: each group once, and each ungrouped pot by its name.</summary>
    public static IReadOnlyList<string> PotNames(Circuit.Circuit circuit) => PotsOf(circuit)
        .Select(p => string.IsNullOrEmpty(p.Group) ? p.Name : p.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<(string Name, string Group, IPotControl Control)> PotsOf(Circuit.Circuit circuit)
    {
        foreach (Component component in circuit.Components)
        {
            if (component is Circuit.Circuit inner)
                foreach (var pot in PotsOf(inner))
                    yield return pot;
            else if (component is IPotControl control)
                yield return (component.Name, control.Group, control);
        }
    }

    private static Circuit.Circuit BuildSchematic(string schematicPath, ILog log)
    {
        Circuit.Circuit circuit = Schematic.Load(schematicPath, log).Build();
        circuit.Name = Path.GetFileNameWithoutExtension(schematicPath);
        return circuit;
    }
}
