using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpiceForgeServer.Protocol;

/// <summary>A request's arguments, with the checks every handler needs: a missing or mistyped argument is a
/// <c>bad_request</c> naming it.</summary>
public sealed class Args
{
    private readonly JsonObject args;

    public Args(JsonObject? args) => this.args = args ?? new JsonObject();

    public JsonNode? this[string name] => args[name];

    public bool Has(string name) => args[name] is not null;

    public string String(string name) => OptionalString(name) ?? throw Missing(name);

    public string? OptionalString(string name) => args[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw Wrong(name, "a string"),
    };

    public bool Bool(string name, bool fallback) => args[name] switch
    {
        null => fallback,
        JsonValue v when v.TryGetValue(out bool b) => b,
        _ => throw Wrong(name, "true or false"),
    };

    public int Int(string name, int fallback) => args[name] switch
    {
        null => fallback,
        JsonValue v when v.TryGetValue(out int i) => i,
        _ => throw Wrong(name, "an integer"),
    };

    public JsonObject Object(string name) => OptionalObject(name) ?? throw Missing(name);

    public JsonObject? OptionalObject(string name) => args[name] switch
    {
        null => null,
        JsonObject o => o,
        _ => throw Wrong(name, "an object"),
    };

    public JsonArray Array(string name) => OptionalArray(name) ?? throw Missing(name);

    public JsonArray? OptionalArray(string name) => args[name] switch
    {
        null => null,
        JsonArray a => a,
        _ => throw Wrong(name, "an array"),
    };

    public List<string> Strings(string name)
    {
        var result = new List<string>();
        foreach (JsonNode? item in Array(name))
        {
            if (item is JsonValue v && v.TryGetValue(out string? s))
                result.Add(s);
            else
                throw Wrong(name, "an array of strings");
        }
        return result;
    }

    private static ApiException Missing(string name) => new(ErrorCodes.BadRequest, $"Missing argument '{name}'.");

    private static ApiException Wrong(string name, string expected) => new(ErrorCodes.BadRequest, $"Argument '{name}' must be {expected}.");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (JsonNode? item in items)
            array.Add(item);
        return array;
    }

    public static JsonArray Strings(IEnumerable<string> items) => Array(items.Select(s => (JsonNode?)JsonValue.Create(s)));

    public static JsonArray Point(double x, double y) => new(JsonValue.Create(x), JsonValue.Create(y));

    public static JsonArray Point(int x, int y) => new(JsonValue.Create(x), JsonValue.Create(y));
}
