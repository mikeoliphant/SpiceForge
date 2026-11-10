using Circuit;
using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>
/// A pot whose position can change while the circuit runs, without re-solving anything. Its value is the tapered
/// wiper fraction P, between 0 and 1, and each pot leg obeys v = R·P·i (or R·(1 - P)·i) with its current as an
/// unknown. P only ever multiplies a current, so it never ends up in a denominator, a leg at 0 Ω is an exact short,
/// and E doesn't depend on P at all: the engine treats each leg as one more port, with P read at runtime.
/// </summary>
public sealed class PotParameter
{
    public required string Name { get; init; }

    /// <summary>The component this parameter belongs to (the replacement pot in the circuit).</summary>
    public required Component Component { get; init; }

    /// <summary>The symbol for P in the circuit's equations.</summary>
    public required Variable Symbol { get; init; }

    public required SweepType Sweep { get; init; }

    /// <summary>The wiper position from the schematic.</summary>
    public required double InitialWipe { get; init; }

    /// <summary>The parameter value for a wiper position, between 0 and 1, with the pot's taper applied.</summary>
    public double ValueAt(double wipe) => VariableResistor.AdjustWipe(wipe, Sweep);

    public double InitialValue => ValueAt(InitialWipe);
}

/// <summary>Replaces a circuit's pots and variable resistors with dynamic versions.</summary>
public static class DynamicPots
{
    /// <summary>
    /// Swaps every Potentiometer and VariableResistor in the circuit, including inside subcircuits, for one whose
    /// position is a runtime parameter. Returns the parameters, in a stable order.
    /// </summary>
    public static IReadOnlyList<PotParameter> Replace(Circuit.Circuit circuit)
    {
        var parameters = new List<PotParameter>();
        Replace(circuit, "", parameters);
        return parameters;
    }

    private static void Replace(Circuit.Circuit circuit, string prefix, List<PotParameter> parameters)
    {
        foreach (Component component in circuit.Components.ToList())
        {
            switch (component)
            {
                case Circuit.Circuit inner:
                    Replace(inner, prefix + inner.Name + ".", parameters);
                    break;

                case Potentiometer pot when pot is not DynamicPotentiometer:
                {
                    var symbol = Variable.New("pot:" + prefix + pot.Name);
                    var replacement = new DynamicPotentiometer(symbol)
                    {
                        Name = pot.Name,
                        Resistance = pot.Resistance,
                        Wipe = pot.Wipe,
                        Sweep = pot.Sweep,
                    };
                    Node a = pot.Anode.ConnectedTo, c = pot.Cathode.ConnectedTo, w = pot.Wiper.ConnectedTo;
                    pot.Anode.ConnectTo(null);
                    pot.Cathode.ConnectTo(null);
                    pot.Wiper.ConnectTo(null);
                    circuit.Components.Remove(pot);
                    replacement.ConnectTo(a, c, w);
                    circuit.Components.Add(replacement);
                    parameters.Add(new PotParameter { Name = prefix + pot.Name, Component = replacement, Symbol = symbol, Sweep = pot.Sweep, InitialWipe = pot.Wipe });
                    break;
                }

                case VariableResistor resistor when resistor is not DynamicVariableResistor:
                {
                    var symbol = Variable.New("pot:" + prefix + resistor.Name);
                    var replacement = new DynamicVariableResistor(symbol)
                    {
                        Name = resistor.Name,
                        Resistance = resistor.Resistance,
                        Wipe = resistor.Wipe,
                        Sweep = resistor.Sweep,
                    };
                    Node a = resistor.Anode.ConnectedTo, c = resistor.Cathode.ConnectedTo;
                    resistor.Anode.ConnectTo(null);
                    resistor.Cathode.ConnectTo(null);
                    circuit.Components.Remove(resistor);
                    replacement.ConnectTo(a, c);
                    circuit.Components.Add(replacement);
                    parameters.Add(new PotParameter { Name = prefix + resistor.Name, Component = replacement, Symbol = symbol, Sweep = resistor.Sweep, InitialWipe = resistor.Wipe });
                    break;
                }
            }
        }
    }

    /// <summary>Adds a resistive leg with its current as an unknown: Va - Vb = R·i, where R may contain parameters.</summary>
    internal static void Leg(Analysis mna, string name, Node anode, Node cathode, Expression resistance)
    {
        Expression i = mna.AddUnknown("i" + name);
        mna.AddPassiveComponent(anode, cathode, i);
        mna.AddEquation(anode.V - cathode.V, resistance * i);
    }
}

/// <summary>A potentiometer whose tapered position is the parameter <c>P</c>.</summary>
public sealed class DynamicPotentiometer : Potentiometer
{
    public Variable Parameter { get; }

    public DynamicPotentiometer(Variable parameter) => Parameter = parameter;

    public override void Analyze(Analysis Mna)
    {
        Expression r = Resistance;
        DynamicPots.Leg(Mna, Name + "c", Cathode, Wiper, r * Parameter);
        DynamicPots.Leg(Mna, Name + "a", Anode, Wiper, r * (1 - Parameter));
    }
}

/// <summary>A variable resistor whose tapered position is the parameter <c>P</c>.</summary>
public sealed class DynamicVariableResistor : VariableResistor
{
    public Variable Parameter { get; }

    public DynamicVariableResistor(Variable parameter) => Parameter = parameter;

    public override void Analyze(Analysis Mna)
    {
        Expression r = Resistance;
        DynamicPots.Leg(Mna, Name, Anode, Cathode, r * Parameter);
    }
}
