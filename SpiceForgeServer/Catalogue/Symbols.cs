using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Circuit;
using Component = Circuit.Component;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Catalogue;

/// <summary>Symbol layouts and property descriptors (WebSocketApi.md, Symbols and Catalogue), from LiveSPICE's
/// components.</summary>
public static class Symbols
{
    /// <summary>A component's symbol in its own coordinates: binding terminals and bounds, and default artwork.</summary>
    public static JsonObject Layout(Component component)
    {
        SymbolLayout layout = component.LayoutSymbol();
        var result = new JsonObject
        {
            ["bounds"] = new JsonObject
            {
                ["min"] = Json.Point(layout.LowerBound.x, layout.LowerBound.y),
                ["max"] = Json.Point(layout.UpperBound.x, layout.UpperBound.y),
            },
            ["terminals"] = Json.Array(layout.Terminals.Select(t =>
            {
                Coord at = layout.MapTerminal(t);
                return (JsonNode?)new JsonObject { ["name"] = t.Name, ["at"] = Json.Point(at.x, at.y) };
            })),
            ["lines"] = Json.Array(layout.Lines.Select(s => (JsonNode?)Segment(s))),
            ["rectangles"] = Json.Array(layout.Rectangles.Select(s => (JsonNode?)Filled(s))),
            ["ellipses"] = Json.Array(layout.Ellipses.Select(s => (JsonNode?)Filled(s))),
            ["curves"] = Json.Array(layout.Curves.Select(c => (JsonNode?)new JsonObject
            {
                ["edge"] = Edge(c.Edge),
                ["points"] = Json.Array(c.x.Select(p => (JsonNode?)Json.Point(p.x, p.y))),
            })),
            ["arcs"] = Json.Array(layout.Arcs.Select(a => (JsonNode?)new JsonObject
            {
                ["edge"] = Edge(a.Type),
                ["center"] = Json.Point(a.Center.x, a.Center.y),
                ["radius"] = a.Radius,
                ["start"] = a.StartAngle,
                ["end"] = a.EndAngle,
                ["direction"] = a.Direction == Direction.Clockwise ? "clockwise" : "counterclockwise",
            })),
            ["texts"] = Json.Array(layout.Texts.Select(t => (JsonNode?)new JsonObject
            {
                ["text"] = TextOf(t),
                ["at"] = Json.Point(t.x.x, t.x.y),
                ["h"] = Alignment(t.HorizontalAlign),
                ["v"] = Alignment(t.VerticalAlign),
                ["size"] = t.Size switch { Size.Small => "small", Size.Large => "large", _ => "normal" },
            })),
        };
        return result;
    }

    private static JsonObject Segment(SymbolLayout.Shape s) =>
        new() { ["edge"] = Edge(s.Edge), ["a"] = Json.Point(s.x1.x, s.x1.y), ["b"] = Json.Point(s.x2.x, s.x2.y) };

    private static JsonObject Filled(SymbolLayout.Shape s)
    {
        JsonObject shape = Segment(s);
        shape["fill"] = s.Fill;
        return shape;
    }

    // A text's string is computed from the component when it's drawn (its name, its value); one that can't be is
    // shown empty rather than failing the whole layout.
    private static string TextOf(SymbolLayout.Text text)
    {
        try
        {
            return text.String ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static string Edge(EdgeType edge) => edge.ToString().ToLowerInvariant();

    private static string Alignment(Alignment alignment) => alignment switch
    {
        Circuit.Alignment.Near => "near",
        Circuit.Alignment.Center => "center",
        _ => "far",
    };

    /// <summary>The browsable, serialized properties of a component type, as the property grid shows them.</summary>
    public static JsonArray Descriptors(Type type)
    {
        Component? instance = TryCreate(type);
        var result = new JsonArray();
        foreach (PropertyInfo property in EditableProperties(type))
        {
            Type valueType = property.PropertyType;
            object? value = instance != null ? SafeGet(property, instance) : null;
            var descriptor = new JsonObject
            {
                ["name"] = property.Name,
                ["category"] = property.GetCustomAttribute<CategoryAttribute>()?.Category ?? "Misc",
                ["description"] = property.GetCustomAttribute<DescriptionAttribute>()?.Description,
                ["kind"] = Kind(valueType),
                ["units"] = value is Quantity q && q.Units is not null ? q.Units.ToString() : null,
                ["default"] = value != null ? ToInvariantString(property, value) : null,
                ["readOnly"] = property.SetMethod is not { IsPublic: true },
            };
            if (valueType.IsEnum)
                descriptor["values"] = Json.Strings(Enum.GetNames(valueType));
            result.Add(descriptor);
        }
        return result;
    }

    /// <summary>The properties a file stores (marked <c>[Serialize]</c>) that the property grid shows.</summary>
    public static IEnumerable<PropertyInfo> EditableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<Circuit.Serialize>() != null)
            .Where(p => p.GetCustomAttribute<BrowsableAttribute>()?.Browsable ?? true);

    private static string Kind(Type type)
    {
        if (type == typeof(Quantity)) return "quantity";
        if (type == typeof(bool)) return "boolean";
        if (type.IsEnum) return "enum";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        return "text";
    }

    public static string? ToInvariantString(PropertyInfo property, object value) =>
        TypeDescriptor.GetConverter(property.PropertyType).ConvertToString(null, CultureInfo.InvariantCulture, value);

    private static object? SafeGet(PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static Component? TryCreate(Type type)
    {
        try
        {
            return type.GetConstructor(Type.EmptyTypes) != null ? (Component)Activator.CreateInstance(type)! : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
