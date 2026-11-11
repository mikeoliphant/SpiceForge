using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Circuit;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer.Documents;

/// <summary>
/// <c>circuit.check</c>: builds a document the way LiveSPICE does, without solving it, and reports in the client's
/// terms (its element ids) the problems, the server's node assignment, and the pots and switches a simulation would
/// have.
/// </summary>
public static class CircuitChecker
{
    public static JsonObject Check(JsonObject document)
    {
        // Build the schematic from the document's elements one by one, remembering which element each came from.
        XElement root = SchxConverter.ToSchematic(document);
        JsonArray elements = document["elements"] as JsonArray ?? new JsonArray();
        var ids = new Dictionary<Element, string>();
        var built = new List<Element>();
        var errors = new JsonArray();
        var warnings = new JsonArray();
        int index = 0;
        foreach (XElement xml in root.Elements("Element"))
        {
            string id = elements[index] is JsonObject e && e["id"] is JsonValue v && v.TryGetValue(out string? s) ? s : $"#{index}";
            index++;
            try
            {
                Element element = Element.Deserialize(xml);
                ids[element] = id;
                built.Add(element);
            }
            catch (Exception ex)
            {
                errors.Add(Problem($"Element couldn't be loaded: {ex.Message}", id));
            }
        }

        var schematic = new Schematic(new NullLog(), built);
        schematic.Circuit.Name = root.Attribute("Name")?.Value ?? "";
        string IdOf(Component component) => component.Tag is Element element && ids.TryGetValue(element, out string? id) ? id : "";

        // The nodes as the schematic connected them, before Build adds stand-ins for unconnected terminals.
        var nodes = new JsonArray();
        foreach (Node node in schematic.Circuit.Nodes)
        {
            nodes.Add(new JsonObject
            {
                ["name"] = node.Name,
                ["terminals"] = Json.Array(node.Connected.Select(t => (JsonNode?)new JsonObject
                {
                    ["element"] = IdOf(t.Owner),
                    ["terminal"] = t.Name,
                })),
                ["wires"] = Json.Strings(schematic.Wires.Where(w => w.Node == node).Select(w => ids[w])),
            });
        }

        // The checks Schematic.Build makes, attributed to elements. Duplicate names come from the document itself:
        // LiveSPICE's component collection renames a clashing component as it's added (R1 to R2), so the schematic
        // never holds duplicates, though a file can, and Build's own check never sees them.
        List<Component> components = schematic.Circuit.Components.ToList();
        var documentNames = elements.OfType<JsonObject>()
            .Where(e => e["kind"]?.GetValue<string>() == "symbol" && e["component"]?["properties"]?["Name"] is JsonValue)
            .GroupBy(e => e["component"]!["properties"]!["Name"]!.GetValue<string>(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1);
        foreach (var named in documentNames)
            errors.Add(Problem($"Name '{named.Key}' is not unique", named.Select(e => e["id"]?.GetValue<string>() ?? "").ToArray()));
        foreach (Component component in components)
        {
            string id = IdOf(component);
            foreach (Terminal terminal in component.Terminals.Where(t => t.ConnectedTo == null))
                warnings.Add(Problem($"Unconnected terminal '{terminal}'", id));
            string typeName = component.GetType().Name;
            if (typeName == "Error")
                errors.Add(Problem(component.ToString(), id));
            else if (typeName == "Warning")
                warnings.Add(Problem(component.ToString(), id));
            if (component.GetType().GetCustomAttribute<ObsoleteAttribute>() != null)
                warnings.Add(Problem($"Use of deprecated symbol '{component}'", id));
        }

        // Then the build itself, for anything else it rejects.
        if (errors.Count == 0)
        {
            try
            {
                schematic.Build(new NullLog());
            }
            catch (Exception ex)
            {
                errors.Add(Problem(ex.Message));
            }
        }

        return new JsonObject
        {
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["nodes"] = nodes,
            ["controls"] = Controls(components, IdOf),
        };
    }

    // The controls as SpiceForge's SchematicProcessor would make them: one per pot or switch, or per group of them.
    private static JsonObject Controls(List<Component> components, Func<Component, string> idOf)
    {
        static string ControlName(Component c, string? group) => string.IsNullOrEmpty(group) ? c.Name : group;

        var pots = new JsonArray();
        foreach (var group in components.OfType<IPotControl>().Cast<Component>()
                     .GroupBy(c => ControlName(c, ((IPotControl)c).Group), StringComparer.OrdinalIgnoreCase))
        {
            Component first = group.First();
            pots.Add(new JsonObject
            {
                ["name"] = group.Key,
                ["position"] = ((IPotControl)first).PotValue,
                ["taper"] = first.GetType().GetProperty("Sweep")?.GetValue(first)?.ToString(),
                ["elements"] = Json.Strings(group.Select(idOf)),
            });
        }

        var switches = new JsonArray();
        foreach (var group in components.OfType<IButtonControl>().Cast<Component>()
                     .GroupBy(c => ControlName(c, ((IButtonControl)c).Group), StringComparer.OrdinalIgnoreCase))
        {
            var first = (IButtonControl)group.First();
            switches.Add(new JsonObject
            {
                ["name"] = group.Key,
                ["position"] = first.Position,
                ["positions"] = first.NumPositions,
                ["elements"] = Json.Strings(group.Select(idOf)),
            });
        }
        return new JsonObject { ["pots"] = pots, ["switches"] = switches };
    }

    private static JsonObject Problem(string message, params string[] elements) =>
        new() { ["message"] = message, ["elements"] = Json.Strings(elements.Where(e => e.Length > 0)) };
}
