using Circuit;
using ComputerAlgebra;
using Expression = ComputerAlgebra.Expression;

// LiveSPICE marks its on/off Switch obsolete (single-pole multi-throw switches replace it), but older schematics
// still contain it, so it's handled here deliberately.
#pragma warning disable CS0612

namespace SpiceForge;

/// <summary>
/// The state of one switch contact (one throw of one switch, or of every switch in a group of ganged switches), as a
/// runtime parameter δ: -1/2 closed, near +1/2 open. The contact obeys (1/2 - δ)·v = (1/2 + δ)·R0·i, written as
/// v - R0·i = 2δ·s with s = v + R0·i, so δ only ever multiplies the one port s: closed it is v = 0, an exact short;
/// at δ = +1/2 it would be i = 0, exactly open. E holds the contact at δ = 0, a resistor R0, so it is nonsingular, and
/// nothing divides by δ.
/// <para>
/// Open is exact (i = 0). A node that only switch contacts connect to (the Blues Breaker's Bypass) would be undetermined
/// with every contact on it open, so <see cref="DynamicSwitches.Replace"/> ties it to ground through
/// <see cref="TieResistance"/>: floating, it sits at 0 V, as LiveSPICE reports such a node. (Opening its contacts to a
/// finite resistance instead joined the switches' commons through it, which LiveSPICE doesn't.) The DC starting point
/// uses 10¹² Ω for open contacts, as LiveSPICE does.
/// </para>
/// </summary>
public sealed class SwitchParameter : RuntimeParameter
{
    /// <summary>The switch's name, or its group's when it is ganged with others.</summary>
    public required string Switch { get; init; }

    /// <summary>Which throw of the switch this contact is (0 for a single-throw switch).</summary>
    public required int Throw { get; init; }

    /// <summary>The switch's number of positions (2 for a single-throw switch: open, closed).</summary>
    public required int Positions { get; init; }

    /// <summary>The switch's position in the schematic.</summary>
    public required int InitialPosition { get; init; }

    /// <summary>The contact's resistance halfway between open and closed, which E holds. It only affects conditioning.</summary>
    /// <remarks>10 MΩ: folding a contact from R0 to a short or an open puts roundoff on tiny nearby nodes, and a larger R0
    /// put less there. On the Blues Breaker, 10 kΩ left millivolt nodes at -41 dB from LiveSPICE; 1 MΩ, -79 dB; 10 MΩ
    /// passes everywhere (switched circuits -193 dB or better against baked). 100 MΩ starts to cost the simple ones.</remarks>
    public const double NominalResistance = 10e6;

    /// <summary>An open contact's resistance while running, and in the DC solve (LiveSPICE's value there).</summary>
    /// <summary>The resistance to ground that keeps a node only switch contacts touch determined.</summary>
    public const double TieResistance = 1e15;
    public const double DcOpenResistance = 1e12;

    public const double Closed = -0.5;
    public const double Open = 0.5;
    private static double OpenValue(double resistance) => 0.5 * (resistance - NominalResistance) / (resistance + NominalResistance);

    /// <summary>
    /// The parameter value with the switch in this position. For a multi-throw switch, the contact is closed when the
    /// position is its throw; a single-throw switch (Positions 2, Throw 0) is closed at position 1.
    /// </summary>
    public double ValueAt(int position) => (Positions == 2 && IsSingleThrow ? position == 1 : position == Throw) ? Closed : Open;

    /// <summary>Whether this is the contact of a single-throw (on/off) switch.</summary>
    public required bool IsSingleThrow { get; init; }

    public override double InitialValue => ValueAt(InitialPosition);

    public override double DcValue => InitialValue == Closed ? Closed : OpenValue(DcOpenResistance);
}

/// <summary>Replaces a circuit's switches with dynamic versions whose contacts are runtime parameters.</summary>
public static class DynamicSwitches
{
    /// <summary>
    /// Swaps every switch in the circuit, including inside subcircuits, for one whose contacts are runtime parameters
    /// (see <see cref="SwitchParameter"/>). Ganged switches (the same non-empty group) share parameters, one per throw.
    /// Returns the parameters, in a stable order.
    /// </summary>
    public static IReadOnlyList<SwitchParameter> Replace(Circuit.Circuit circuit)
    {
        var parameters = new List<SwitchParameter>();
        Replace(circuit, "", parameters, new Dictionary<string, SwitchParameter[]>());
        return parameters;
    }

    /// <summary>Sets a switch (or group) to a position in a parameter array laid out as ExtractedSystem.RuntimeParameters.</summary>
    public static void Set(double[] values, IReadOnlyList<RuntimeParameter> parameters, string nameOrGroup, int position)
    {
        bool found = false;
        for (int j = 0; j < parameters.Count; j++)
            if (parameters[j] is SwitchParameter contact && string.Equals(contact.Switch, nameOrGroup, StringComparison.OrdinalIgnoreCase))
            {
                if (position < 0 || position >= contact.Positions)
                    throw new ArgumentException($"Switch '{nameOrGroup}' has positions 0 to {contact.Positions - 1}, not {position}.");
                values[j] = contact.ValueAt(position);
                found = true;
            }
        if (!found)
            throw new ArgumentException($"No dynamic switch named '{nameOrGroup}'.");
    }

    private static void Replace(Circuit.Circuit circuit, string prefix, List<SwitchParameter> parameters, Dictionary<string, SwitchParameter[]> groups)
    {
        // Tie each node only switch contacts touch to ground, so it stays determined with every contact on it open.
        Node? ground = circuit.Components.OfType<Ground>().Select(g => g.Terminal.ConnectedTo).FirstOrDefault(n => n != null);
        if (ground != null)
            foreach (Node node in circuit.Nodes.ToList())
            {
                var touching = circuit.Components.Where(c => c is not NamedWire && c.Terminals.Any(t => t.ConnectedTo == node)).ToList();
                if (touching.Count > 0 && touching.All(c => c is SinglePoleSwitch or Circuit.Switch)
                    && touching.Sum(c => c.Terminals.Count(t => t.ConnectedTo == node)) > 1)
                {
                    var tie = new Resistor { Name = "_tie_" + node.Name, Resistance = new Quantity(SwitchParameter.TieResistance, Units.Ohm) };
                    circuit.Components.Add(tie);
                    tie.ConnectTo(node, ground);
                }
            }

        foreach (Component component in circuit.Components.ToList())
        {
            switch (component)
            {
                case Circuit.Circuit inner:
                    Replace(inner, prefix + inner.Name + ".", parameters, groups);
                    break;

                case SinglePoleSwitch sw when sw is not DynamicSinglePoleSwitch:
                {
                    SwitchParameter[] contacts = Contacts(prefix + sw.Name, sw.Group, sw.Throws.Length, sw.Position, false, parameters, groups);
                    // A throw whose node connects to nothing else carries no current in any position, so its contact adds
                    // nothing, as LiveSPICE drops such a node (the Blues Breaker's unused Bypass control).
                    bool[] dangling = sw.Throws.Select(t => t.ConnectedTo is Node node
                        && circuit.Components.Where(c => c is not NamedWire).Sum(c => c.Terminals.Count(x => x.ConnectedTo == node)) == 1).ToArray();
                    var replacement = new DynamicSinglePoleSwitch(contacts.Select(c => c.Symbol).ToArray(), dangling) { Name = sw.Name, Group = sw.Group, Position = sw.Position };
                    Node? common = sw.Common.ConnectedTo;
                    Node?[] throws = sw.Throws.Select(t => t.ConnectedTo).ToArray();
                    foreach (Terminal terminal in sw.Terminals)
                        terminal.ConnectTo(null);
                    circuit.Components.Remove(sw);
                    replacement.Common.ConnectTo(common);
                    for (int i = 0; i < throws.Length; i++)
                        replacement.Throws[i].ConnectTo(throws[i]);
                    circuit.Components.Add(replacement);
                    break;
                }

                case Circuit.Switch sw when sw is not DynamicSwitch:
                {
                    SwitchParameter[] contacts = Contacts(prefix + sw.Name, "", 2, sw.Closed ? 1 : 0, true, parameters, groups);
                    var replacement = new DynamicSwitch(contacts[0].Symbol) { Name = sw.Name, Closed = sw.Closed };
                    Node? a = sw.Anode.ConnectedTo, c = sw.Cathode.ConnectedTo;
                    sw.Anode.ConnectTo(null);
                    sw.Cathode.ConnectTo(null);
                    circuit.Components.Remove(sw);
                    replacement.ConnectTo(a, c);
                    circuit.Components.Add(replacement);
                    break;
                }
            }
        }
    }

    // One parameter per throw, shared by every switch in the same group.
    private static SwitchParameter[] Contacts(string name, string group, int positions, int position, bool singleThrow,
        List<SwitchParameter> parameters, Dictionary<string, SwitchParameter[]> groups)
    {
        string key = string.IsNullOrEmpty(group) ? name : group;
        if (groups.TryGetValue(key, out SwitchParameter[]? shared) && shared.Length == (singleThrow ? 1 : positions))
            return shared;
        var contacts = Enumerable.Range(0, singleThrow ? 1 : positions).Select(throwIndex => new SwitchParameter
        {
            Name = singleThrow ? key : $"{key}.{throwIndex}",
            Switch = key,
            Throw = throwIndex,
            Positions = positions,
            InitialPosition = position,
            IsSingleThrow = singleThrow,
            Symbol = Variable.New("switch:" + (singleThrow ? key : $"{key}.{throwIndex}")),
            Component = null!,
        }).ToArray();
        groups[key] = contacts;
        parameters.AddRange(contacts);
        return contacts;
    }

    /// <summary>A switch contact between two terminals with state δ (see <see cref="SwitchParameter"/>). A contact with
    /// an unconnected end carries no current in any state, so it adds nothing.</summary>
    internal static void Contact(Analysis mna, string name, Terminal a, Terminal b, Expression delta)
    {
        if (a.ConnectedTo is not Node anode || b.ConnectedTo is not Node cathode)
            return;
        Expression i = mna.AddUnknown("i" + name);
        mna.AddPassiveComponent(anode, cathode, i);
        Expression r0 = SwitchParameter.NominalResistance;
        Expression s = mna.AddUnknown("s" + name);
        mna.AddEquation(s, anode.V - cathode.V + r0 * i);
        mna.AddEquation(anode.V - cathode.V - r0 * i, 2 * delta * s);
    }
}

/// <summary>A single-pole multi-throw switch whose contacts' states are runtime parameters.</summary>
public sealed class DynamicSinglePoleSwitch : SinglePoleSwitch
{
    public Variable[] Parameters { get; }
    private readonly bool[] dangling;

    public DynamicSinglePoleSwitch(Variable[] parameters, bool[] dangling) : base(parameters.Length)
    {
        Parameters = parameters;
        this.dangling = dangling;
    }

    public override void Analyze(Analysis Mna)
    {
        for (int i = 0; i < Throws.Length; ++i)
            if (!dangling[i])
                DynamicSwitches.Contact(Mna, Name + "_" + i, Common, Throws[i], Parameters[i]);
    }
}

/// <summary>A single-throw (on/off) switch whose contact's state is a runtime parameter.</summary>
public sealed class DynamicSwitch : Circuit.Switch
{
    public Variable Parameter { get; }

    public DynamicSwitch(Variable parameter) => Parameter = parameter;

    public override void Analyze(Analysis Mna) => DynamicSwitches.Contact(Mna, Name, Anode, Cathode, Parameter);
}
