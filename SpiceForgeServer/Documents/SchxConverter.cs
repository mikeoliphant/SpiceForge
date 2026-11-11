using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Documents;

/// <summary>
/// Converts between LiveSPICE's schematic XML and the JSON document of WebSocketApi.md, The document.
/// <para>
/// The conversion is attribute for attribute, so it's lossless: a file read and written back unchanged comes out the
/// same, unresolved components and subcircuit bodies included. It doesn't deserialize the components to do it; it only
/// asks LiveSPICE whether each one would load (<see cref="ResolveComponent"/>), to flag the ones that wouldn't.
/// </para>
/// </summary>
public static class SchxConverter
{
    private static readonly string SymbolType = typeof(Circuit.Symbol).AssemblyQualifiedName!;
    private static readonly string WireType = typeof(Circuit.Wire).AssemblyQualifiedName!;

    /// <summary>A schematic (or clipboard) root element to a document. Elements get provisional ids e1, e2, ...</summary>
    public static JsonObject ToDocument(XElement schematic, Func<XElement, string?>? resolve = null)
    {
        var circuit = new JsonObject();
        foreach (XAttribute attribute in schematic.Attributes())
            circuit[attribute.Name.LocalName] = attribute.Value;
        return new JsonObject
        {
            ["circuit"] = circuit,
            ["elements"] = ToElements(schematic.Elements("Element"), resolve),
        };
    }

    public static JsonArray ToElements(IEnumerable<XElement> elements, Func<XElement, string?>? resolve = null)
    {
        var result = new JsonArray();
        int id = 0;
        foreach (XElement element in elements)
            result.Add(ToElement(element, "e" + ++id, resolve));
        return result;
    }

    private static JsonObject ToElement(XElement element, string id, Func<XElement, string?>? resolve)
    {
        string type = element.Attribute("Type")?.Value ?? "";
        if (IsType(type, "Circuit.Symbol") && element.Element("Component") is XElement component)
        {
            return new JsonObject
            {
                ["id"] = id,
                ["kind"] = "symbol",
                ["position"] = PointOf(element, "Position"),
                ["rotation"] = int.Parse(Required(element, "Rotation"), CultureInfo.InvariantCulture),
                ["flip"] = XmlConvert.ToBoolean(Required(element, "Flip").ToLowerInvariant()),
                ["component"] = ToComponent(component, resolve),
            };
        }
        if (IsType(type, "Circuit.Wire"))
            return new JsonObject { ["id"] = id, ["kind"] = "wire", ["a"] = PointOf(element, "A"), ["b"] = PointOf(element, "B") };

        // Anything else is kept as it is, for writing back.
        return new JsonObject { ["id"] = id, ["kind"] = "raw", ["xml"] = element.ToString(SaveOptions.DisableFormatting) };
    }

    /// <summary>A <c>Component</c> element to <c>{type, properties, body}</c>, flagged <c>unresolved</c> if LiveSPICE
    /// couldn't load it.</summary>
    public static JsonObject ToComponent(XElement component, Func<XElement, string?>? resolve = null)
    {
        string fullType = component.Attribute("_Type")?.Value ?? "";
        string? problem = resolve?.Invoke(component);
        var properties = new JsonObject();
        foreach (XAttribute attribute in component.Attributes().Where(a => a.Name.LocalName != "_Type"))
            properties[attribute.Name.LocalName] = attribute.Value;
        var result = new JsonObject
        {
            // An unresolved type keeps its full name, so it's written back exactly.
            ["type"] = problem == null ? ShortTypeName(fullType) : fullType,
            ["properties"] = properties,
        };
        if (component.Nodes().Any())
            result["body"] = string.Concat(component.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
        if (problem != null)
            result["unresolved"] = new JsonObject { ["message"] = problem };
        return result;
    }

    /// <summary>Whether LiveSPICE can load this component, and if not, why not: null if it can.</summary>
    public static string? ResolveComponent(XElement component)
    {
        try
        {
            Circuit.Component.Deserialize(component);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A document to a schematic root element. Throws <c>bad_document</c> on anything malformed.</summary>
    public static XElement ToSchematic(JsonObject document)
    {
        var schematic = new XElement("Schematic");
        if (document["circuit"] is JsonObject circuit)
            foreach (KeyValuePair<string, JsonNode?> property in circuit)
                schematic.SetAttributeValue(property.Key, StringOf(property.Value, $"circuit.{property.Key}"));
        else
            foreach (string name in new[] { "Name", "Description", "PartNumber" })
                schematic.SetAttributeValue(name, "");
        foreach (XElement element in ToXmlElements(ElementsOf(document, "elements")))
            schematic.Add(element);
        return schematic;
    }

    public static IEnumerable<XElement> ToXmlElements(JsonArray elements)
    {
        int index = 0;
        foreach (JsonNode? node in elements)
        {
            string where = $"elements[{index++}]";
            if (node is not JsonObject element)
                throw Bad($"{where} isn't an object.");
            yield return ToXmlElement(element, where);
        }
    }

    public static XElement ToXmlElement(JsonObject element, string where)
    {
        switch (StringOf(element["kind"], where + ".kind"))
        {
            case "symbol":
                var symbol = new XElement("Element");
                symbol.SetAttributeValue("Type", SymbolType);
                symbol.SetAttributeValue("Rotation", IntOf(element["rotation"], where + ".rotation").ToString(CultureInfo.InvariantCulture));
                symbol.SetAttributeValue("Flip", XmlConvert.ToString(BoolOf(element["flip"], where + ".flip")));
                symbol.SetAttributeValue("Position", CoordOf(element["position"], where + ".position"));
                symbol.Add(ToXmlComponent(element["component"] as JsonObject ?? throw Bad($"{where}.component is missing."), where + ".component"));
                return symbol;
            case "wire":
                var wire = new XElement("Element");
                wire.SetAttributeValue("Type", WireType);
                wire.SetAttributeValue("A", CoordOf(element["a"], where + ".a"));
                wire.SetAttributeValue("B", CoordOf(element["b"], where + ".b"));
                return wire;
            case "raw":
                try
                {
                    return XElement.Parse(StringOf(element["xml"], where + ".xml"));
                }
                catch (XmlException ex)
                {
                    throw Bad($"{where}.xml isn't XML: {ex.Message}");
                }
            case var kind:
                throw Bad($"{where}.kind is '{kind}', not symbol, wire or raw.");
        }
    }

    public static XElement ToXmlComponent(JsonObject component, string where)
    {
        var result = new XElement("Component");
        result.SetAttributeValue("_Type", FullTypeName(StringOf(component["type"], where + ".type")));
        if (component["properties"] is JsonObject properties)
            foreach (KeyValuePair<string, JsonNode?> property in properties)
            {
                if (property.Key == "_Type" || !IsXmlName(property.Key))
                    throw Bad($"{where}.properties has a bad name '{property.Key}'.");
                result.SetAttributeValue(property.Key, StringOf(property.Value, $"{where}.properties.{property.Key}"));
            }
        if (component["body"] is JsonNode body)
        {
            try
            {
                result.Add(XElement.Parse("<body>" + StringOf(body, where + ".body") + "</body>").Nodes());
            }
            catch (XmlException ex)
            {
                throw Bad($"{where}.body isn't XML: {ex.Message}");
            }
        }
        return result;
    }

    /// <summary>The clipboard format LiveSPICE's editor uses: a <c>Schematic</c> holding the elements.</summary>
    public static string ToClipboardXml(JsonArray elements)
    {
        var root = new XElement("Schematic");
        foreach (XElement element in ToXmlElements(elements))
            root.Add(element);
        return root.ToString();
    }

    /// <summary>A whole file's text, as LiveSPICE writes it: an XML declaration, then the indented schematic.</summary>
    public static byte[] ToFileBytes(XElement schematic)
    {
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), schematic);
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(true) }))
            document.Save(writer);
        return stream.ToArray();
    }

    /// <summary>"Circuit.Resistor, Circuit, Version=..., ..." to "Circuit.Resistor, Circuit".</summary>
    public static string ShortTypeName(string assemblyQualified)
    {
        string[] parts = assemblyQualified.Split(',');
        return parts.Length >= 2 ? parts[0].Trim() + ", " + parts[1].Trim() : assemblyQualified.Trim();
    }

    /// <summary>The assembly-qualified name a file stores, if the type can be found; otherwise the name as given.</summary>
    public static string FullTypeName(string type) => Type.GetType(type)?.AssemblyQualifiedName ?? type;

    private static bool IsType(string assemblyQualified, string fullName) => assemblyQualified.Split(',')[0].Trim() == fullName;

    private static bool IsXmlName(string name)
    {
        try
        {
            XmlConvert.VerifyName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string Required(XElement element, string attribute) =>
        element.Attribute(attribute)?.Value ?? throw new FormatException($"An element is missing its {attribute}.");

    private static JsonArray PointOf(XElement element, string attribute)
    {
        Circuit.Coord coord = Circuit.Coord.Parse(Required(element, attribute));
        return Json.Point(coord.x, coord.y);
    }

    private static JsonArray ElementsOf(JsonObject document, string name) =>
        document[name] switch
        {
            null => new JsonArray(),
            JsonArray array => array,
            _ => throw Bad($"{name} isn't an array."),
        };

    private static string CoordOf(JsonNode? node, string where)
    {
        if (node is JsonArray { Count: 2 } point)
            return $"{IntOf(point[0], where + "[0]")},{IntOf(point[1], where + "[1]")}";
        throw Bad($"{where} must be [x, y], in integers.");
    }

    private static int IntOf(JsonNode? node, string where) =>
        node is JsonValue v && v.TryGetValue(out int i) ? i : throw Bad($"{where} must be an integer.");

    private static bool BoolOf(JsonNode? node, string where) =>
        node is JsonValue v && v.TryGetValue(out bool b) ? b : throw Bad($"{where} must be true or false.");

    private static string StringOf(JsonNode? node, string where) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : throw Bad($"{where} must be a string.");

    private static ApiException Bad(string message) => new(ErrorCodes.BadDocument, message);
}
