using Circuit;
using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

namespace SpiceForge;

/// <summary>
/// A pot whose position can change while the circuit runs, without re-solving anything. Its tapered wiper fraction P
/// is split around a nominal position P0: the parameter is the offset δ = P - P0, and each pot leg obeys
/// v = R·P0·i + R·δ·i (or R·(1 - P0)·i - R·δ·i), with its current as an unknown. So E holds the circuit with its pots at
/// P0, which is nonsingular whenever the circuit is, and δ only ever multiplies a current: it never ends up in a
/// denominator, and a leg at 0 Ω is still an exact short. The engine treats each leg as one more port, with δ read at runtime.
/// </summary>
public sealed class PotParameter : RuntimeParameter
{
    public required SweepType Sweep { get; init; }

    /// <summary>The wiper position from the schematic.</summary>
    public required double InitialWipe { get; init; }

    /// <summary>The nominal tapered position the legs are split around; the parameter is the offset from it.</summary>
    public const double Nominal = 0.5;

    /// <summary>The parameter value for a wiper position between 0 and 1: the tapered position, minus <see cref="Nominal"/>.</summary>
    public double ValueAt(double wipe) => VariableResistor.AdjustWipe(wipe, Sweep) - Nominal;

    public override double InitialValue => ValueAt(InitialWipe);
}

/// <summary>
/// A value the circuit's equations read at runtime, so it can change between any two blocks without re-solving: a pot's
/// position (<see cref="PotParameter"/>) or a switch contact's state (<see cref="SwitchParameter"/>).
/// </summary>
public abstract class RuntimeParameter
{
    public required string Name { get; init; }

    /// <summary>The component this parameter belongs to (the replacement in the circuit).</summary>
    public required Component Component { get; init; }

    /// <summary>The parameter's symbol in the circuit's equations.</summary>
    public required Variable Symbol { get; init; }

    /// <summary>The value at the schematic's setting.</summary>
    public abstract double InitialValue { get; }

    /// <summary>The value to solve the DC starting point with, at the schematic's setting.</summary>
    public virtual double DcValue => InitialValue;
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

/// <summary>A potentiometer whose tapered position, as an offset from the nominal position, is the parameter.</summary>
public sealed class DynamicPotentiometer : Potentiometer
{
    public Variable Parameter { get; }

    public DynamicPotentiometer(Variable parameter) => Parameter = parameter;

    public override void Analyze(Analysis Mna)
    {
        Expression r = Resistance;
        DynamicPots.Leg(Mna, Name + "c", Cathode, Wiper, r * PotParameter.Nominal + r * Parameter);
        DynamicPots.Leg(Mna, Name + "a", Anode, Wiper, r * (1 - PotParameter.Nominal) - r * Parameter);
    }
}

/// <summary>A variable resistor whose tapered position, as an offset from the nominal position, is the parameter.</summary>
public sealed class DynamicVariableResistor : VariableResistor
{
    public Variable Parameter { get; }

    public DynamicVariableResistor(Variable parameter) => Parameter = parameter;

    public override void Analyze(Analysis Mna)
    {
        Expression r = Resistance;
        DynamicPots.Leg(Mna, Name, Anode, Cathode, r * PotParameter.Nominal + r * Parameter);
    }
}
