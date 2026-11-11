using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Circuit;
using SpiceForgeServer.Documents;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer.Catalogue;

/// <summary>
/// The component catalogue (WebSocketApi.md, Catalogue), loaded the way LiveSPICE's component library panel loads it
/// (LiveSPICE/Controls/Library): the Common components, every component type in the loaded assemblies under Generic,
/// then the XML, SPICE and schematic libraries in the components folders, whose subfolders become categories.
/// </summary>
public sealed class Catalog
{
    // LiveSPICE's ComponentLibrary.Common, in its order.
    private static readonly Type[] Common =
    {
        typeof(Conductor), typeof(Ground), typeof(Rail), typeof(Resistor), typeof(Capacitor), typeof(Inductor),
        typeof(VoltageSource), typeof(CurrentSource), typeof(NamedWire), typeof(Label),
    };

    private readonly IReadOnlyList<string> folders;
    private readonly ILog log;
    private readonly Category root = new("");
    private readonly JsonObject entries = new();
    private readonly SortedDictionary<string, Type> types = new(StringComparer.Ordinal);
    private int nextEntry;

    /// <summary>Loads the catalogue from the given folders, in order; missing folders are skipped.</summary>
    public Catalog(IReadOnlyList<string> folders, ILog log)
    {
        this.folders = folders;
        this.log = log;
        Load();
    }

    public JsonObject ToJson() => new()
    {
        ["categories"] = root.ChildrenJson(),
        ["entries"] = entries.DeepClone(),
        ["types"] = TypesJson(),
    };

    /// <summary>Finds a component type by the document's name for it ("Circuit.Resistor, Circuit").</summary>
    public static Type? FindType(string name)
    {
        Type? type = Type.GetType(name);
        return type != null && typeof(Component).IsAssignableFrom(type) ? type : null;
    }

    private void Load()
    {
        Category common = root.Child("Common");
        common.Expanded = true;
        foreach (Type type in Common)
            AddType(common, type);

        Category generic = root.Child("Generic");
        foreach (Type type in ComponentTypes())
            AddType(generic, type);

        foreach (string folder in folders)
            if (Directory.Exists(folder))
                LoadFolder(root, folder);
    }

    private static IEnumerable<Type> ComponentTypes()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] exported;
            try
            {
                exported = assembly.GetExportedTypes();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (Type type in exported.OrderBy(t => t.FullName, StringComparer.Ordinal))
                if (!type.IsAbstract && typeof(Component).IsAssignableFrom(type) && type.GetCustomAttribute<ObsoleteAttribute>() == null
                    && type.GetConstructor(Type.EmptyTypes) != null)
                    yield return type;
        }
    }

    private void LoadFolder(Category category, string folder)
    {
        foreach (string child in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            LoadFolder(category.Child(Path.GetFileName(child)), child);
        foreach (string file in Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            LoadLibrary(category, file);
    }

    // LiveSPICE's Category.LoadLibrary: an XML component library, a schematic (as a subcircuit), or else a SPICE model
    // library.
    private void LoadLibrary(Category category, string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        XDocument document;
        try
        {
            document = XDocument.Load(file);
        }
        catch (XmlException)
        {
            LoadSpiceLibrary(category, file, name);
            return;
        }
        catch (Exception ex)
        {
            log.WriteLine(MessageType.Warning, "Failed to load component library '{0}': {1}", file, ex.Message);
            return;
        }

        try
        {
            if (document.Element("Library") is XElement library)
            {
                Category child = category.Child(library.Attribute("Category")?.Value ?? name);
                foreach (XElement element in library.Elements("Component"))
                {
                    try
                    {
                        AddComponent(child, Component.Deserialize(element));
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine(MessageType.Warning, "Failed to load component: {0}", ex.Message);
                    }
                }
            }
            else if (document.Element("Schematic") is XElement schematic)
            {
                Circuit.Circuit circuit = Schematic.Deserialize(schematic, log).Build(log);
                AddComponent(category, circuit, name, circuit.Description);
            }
        }
        catch (Exception ex)
        {
            log.WriteLine(MessageType.Warning, "Failed to load component library '{0}': {1}", file, ex.Message);
        }
    }

    private void LoadSpiceLibrary(Category category, string file, string name)
    {
        try
        {
            var statements = new Circuit.Spice.Statements { Log = log };
            statements.Parse(file);
            var models = statements.OfType<Circuit.Spice.Model>().Where(m => m.Component != null).ToList();
            if (models.Count > 0)
            {
                Category child = category.Child(name);
                foreach (Circuit.Spice.Model model in models)
                    AddComponent(child, model.Component, model.Component.PartNumber, model.Description);
            }
        }
        catch (Exception ex)
        {
            log.WriteLine(MessageType.Warning, "Failed to load component library '{0}': {1}", file, ex.Message);
        }
    }

    // As LiveSPICE's library does, a type that can't be created or described is skipped (Specialization, for one,
    // throws from PartNumber).
    private void AddType(Category category, Type type)
    {
        try
        {
            if (Symbols.TryCreate(type) is Component component)
                AddComponent(category, component);
        }
        catch (Exception)
        {
        }
    }

    private void AddComponent(Category category, Component component) =>
        AddComponent(category, component, string.IsNullOrEmpty(component.PartNumber) ? component.TypeName : component.PartNumber, component.Description);

    private void AddComponent(Category category, Component component, string name, string? description)
    {
        try
        {
            var entry = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["component"] = SchxConverter.ToComponent(component.Serialize()),
                ["layout"] = Symbols.Layout(component),
            };
            string id = "l" + ++nextEntry;
            entries[id] = entry;
            category.Entries.Add(id);
            types[SchxConverter.ShortTypeName(component.GetType().AssemblyQualifiedName!)] = component.GetType();
        }
        catch (Exception ex)
        {
            log.WriteLine(MessageType.Warning, "Failed to add component '{0}': {1}", name, ex.Message);
        }
    }

    private JsonObject TypesJson()
    {
        var result = new JsonObject();
        foreach ((string name, Type type) in types)
            result[name] = new JsonObject
            {
                ["name"] = Symbols.TryCreate(type)?.TypeName ?? type.Name,
                ["properties"] = Symbols.Descriptors(type),
            };
        // The schematic's own properties, which values.check and the property grid treat as type "circuit".
        result["circuit"] = new JsonObject { ["name"] = "Circuit", ["properties"] = CircuitDescriptors() };
        return result;
    }

    private static JsonArray CircuitDescriptors() =>
        Json.Array(Symbols.Descriptors(typeof(Circuit.Circuit))
            .OfType<JsonObject>()
            .Where(d => d["name"]?.GetValue<string>() is "Name" or "Description" or "PartNumber")
            .Select(d => (JsonNode?)d.DeepClone()));

    private sealed class Category
    {
        public string Name { get; }
        public bool Expanded { get; set; }
        public List<string> Entries { get; } = new();
        private readonly List<Category> children = new();

        public Category(string name) => Name = name;

        public Category Child(string name)
        {
            Category? child = children.FirstOrDefault(c => c.Name == name);
            if (child == null)
            {
                child = new Category(name);
                children.Add(child);
            }
            return child;
        }

        public JsonArray ChildrenJson() => Json.Array(children.Select(c => (JsonNode?)new JsonObject
        {
            ["name"] = c.Name,
            ["expanded"] = c.Expanded,
            ["entries"] = Json.Strings(c.Entries),
            ["children"] = c.ChildrenJson(),
        }));
    }
}
