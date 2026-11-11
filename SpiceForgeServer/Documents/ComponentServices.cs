using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Circuit;
using Component = Circuit.Component;
using SpiceForgeServer.Catalogue;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Documents;

/// <summary><c>catalog.layout</c> and <c>values.check</c>: what the client asks about single components.</summary>
public static class ComponentServices
{
    /// <summary>The symbol of a document component. One LiveSPICE can't load gets a placeholder, with the reason.</summary>
    public static JsonObject Layout(JsonObject component, string where)
    {
        XElement xml = SchxConverter.ToXmlComponent(component, where);
        try
        {
            return Symbols.Layout(Component.Deserialize(xml));
        }
        catch (Exception ex)
        {
            return Placeholder(ex.Message);
        }
    }

    // LiveSPICE draws a component it can't load as a red warning sign, 40 units square, with no terminals.
    private static JsonObject Placeholder(string message) => new()
    {
        ["bounds"] = new JsonObject { ["min"] = Json.Point(-20, -20), ["max"] = Json.Point(20, 20) },
        ["terminals"] = new JsonArray(),
        ["lines"] = new JsonArray(),
        ["rectangles"] = new JsonArray(
            new JsonObject { ["edge"] = "red", ["a"] = Json.Point(-20, -20), ["b"] = Json.Point(20, 20), ["fill"] = false }),
        ["ellipses"] = new JsonArray(),
        ["curves"] = new JsonArray(),
        ["arcs"] = new JsonArray(),
        ["texts"] = new JsonArray(
            new JsonObject { ["text"] = "?", ["at"] = Json.Point(0, 0), ["h"] = "center", ["v"] = "center", ["size"] = "large" }),
        ["message"] = message,
    };

    /// <summary>Parses a property value as LiveSPICE would, and returns it in the form a file would store it.</summary>
    public static JsonObject CheckValue(string typeName, string propertyName, string value)
    {
        Type? type = typeName == "circuit" ? typeof(Circuit.Circuit) : Catalog.FindType(typeName);
        if (type == null)
            return Failed($"There's no component type '{typeName}'.");
        PropertyInfo? property = Symbols.EditableProperties(type).FirstOrDefault(p => p.Name == propertyName);
        if (property == null)
            return Failed($"{type.Name} has no property '{propertyName}'.");
        if (property.SetMethod is not { IsPublic: true })
            return Failed($"{type.Name}.{propertyName} is read-only.");
        Component? instance = Symbols.TryCreate(type);
        if (instance == null)
            return Failed($"{type.Name} can't be created to check its properties.");

        try
        {
            TypeConverter converter = TypeDescriptor.GetConverter(property.PropertyType);
            object? parsed = converter.ConvertFromString(null, CultureInfo.InvariantCulture, value);
            // Set it on a real component and read it back, so the result is what the component would hold: a quantity
            // takes the property's units, for instance.
            // LiveSPICE parses a quantity as an expression, so "banana" parses, as a symbol, and only fails when the
            // circuit is solved. Time is the one symbol a value may use (a source's "Sin(2*Pi*440*t)").
            if (parsed is Quantity quantity)
            {
                var unknown = SpiceForge.SymbolScan.Collect(quantity).Where(s => !s.Equals(Component.t)).ToList();
                if (unknown.Count > 0)
                    return Failed($"'{value}' isn't a value: it uses {string.Join(", ", unknown)}.");
            }
            property.SetValue(instance, parsed);
            object? stored = property.GetValue(instance);
            string canonical = stored != null ? Symbols.ToInvariantString(property, stored) ?? "" : "";
            return new JsonObject { ["ok"] = true, ["value"] = canonical };
        }
        catch (Exception ex)
        {
            Exception inner = ex is TargetInvocationException { InnerException: Exception i } ? i : ex;
            return Failed(inner.Message);
        }
    }

    private static JsonObject Failed(string message) => new() { ["ok"] = false, ["message"] = message };
}
