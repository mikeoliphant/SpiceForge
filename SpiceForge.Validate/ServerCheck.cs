using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SpiceForgeServer.Documents;

/// <summary>
/// Checks SpiceForgeServer's editing side (DesignDocs/WebSocketApi.md):
/// <list type="bullet">
/// <item>every test schematic converts to the JSON document and back unchanged, attribute for attribute;</item>
/// <item>a real server process, over a real WebSocket, answers each editing operation as the spec says, including its
/// errors, the token check, and watching files.</item>
/// </list>
/// </summary>
internal static class ServerCheck
{
    private const string Token = "validate";
    private static int problems;

    public static int Run(string repo, string testsDir)
    {
        problems = 0;
        RoundTrip(testsDir);
        Live(repo, testsDir).GetAwaiter().GetResult();
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok)
            problems++;
    }

    private static void RoundTrip(string testsDir)
    {
        Console.WriteLine("=== Round trip through the JSON document");
        var files = Directory.GetFiles(testsDir, "*.schx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList();
        int unchanged = 0, unresolved = 0;
        foreach (string file in files)
        {
            XElement original = XDocument.Load(file).Root!;
            JsonObject document = SchxConverter.ToDocument(original, SchxConverter.ResolveComponent);
            unresolved += document["elements"]!.AsArray().Count(e => e?["component"]?["unresolved"] != null);
            // Through JSON text, as it travels over the socket.
            var parsed = (JsonObject)JsonNode.Parse(document.ToJsonString())!;
            XElement back = XDocument.Load(new MemoryStream(SchxConverter.ToFileBytes(SchxConverter.ToSchematic(parsed)))).Root!;
            if (XNode.DeepEquals(original, back))
                unchanged++;
            else
                Check(false, $"{Path.GetRelativePath(testsDir, file)} changed in the round trip");
        }
        Check(unchanged == files.Count, $"{unchanged} of {files.Count} schematics unchanged ({unresolved} unresolved component(s) kept)");
    }

    private static async Task Live(string repo, string testsDir)
    {
        Console.WriteLine("=== A running server");
        string? server = FindServer(repo);
        if (server == null)
        {
            Check(false, "SpiceForgeServer.dll not found: build SpiceForgeServer first");
            return;
        }

        int port = FreePort();
        string scratch = Path.Combine(Path.GetTempPath(), "SpiceForgeServerCheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo("dotnet", $"\"{server}\" --port {port} --token {Token} --settings \"{Path.Combine(scratch, "settings.json")}\" --userFolder \"{scratch}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(start)!;
        try
        {
            if (!await WaitForListening(process))
            {
                Check(false, "server started");
                return;
            }
            await Exercise(port, testsDir, scratch);
            Conformance(repo, port, testsDir);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task Exercise(int port, string testsDir, string scratch)
    {
        // The token is checked before the socket opens.
        using (var refused = new ClientWebSocket())
        {
            bool rejected = false;
            try { await refused.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api?token=wrong"), CancellationToken.None); }
            catch (WebSocketException) { rejected = true; }
            Check(rejected, "a wrong token is refused");
        }

        // A first message that isn't hello closes the connection.
        await using (var early = await Client.Connect(port))
        {
            JsonObject reply = await early.Request("catalog.get", null);
            Check(Code(reply) == "bad_request", "a request before hello is refused");
        }

        await using Client client = await Client.Connect(port);
        JsonObject hello = Result(await client.Request("hello", new JsonObject { ["protocol"] = 1, ["client"] = "ServerCheck" }));
        Check(hello["protocol"]?.GetValue<int>() == 1 && hello["capabilities"]?["simulation"]?.GetValue<bool>() == false,
            "hello: protocol 1, simulation not yet supported");
        Check(Code(await client.Request("no.such", null)) == "unknown_op", "an unknown operation is unknown_op");
        Check(Code(await client.Request("sim.start", new JsonObject())) == "unsupported", "simulation answers unsupported");

        // The catalogue.
        JsonObject catalog = Result(await client.Request("catalog.get", null));
        JsonObject entries = catalog["entries"]!.AsObject();
        JsonObject? resistor = entries.Select(e => e.Value as JsonObject).FirstOrDefault(e => e?["component"]?["type"]?.GetValue<string>() == "Circuit.Resistor, Circuit");
        string[] categories = catalog["categories"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToArray();
        Check(categories.Take(2).SequenceEqual(new[] { "Common", "Generic" }) && categories.Contains("Transistors") && categories.Contains("Tubes"),
            $"catalogue categories: {string.Join(", ", categories)}");
        Check(resistor?["layout"]?["terminals"]?.AsArray().Count == 2, $"{entries.Count} catalogue entries; the resistor has 2 terminals");
        JsonArray? resistorProperties = catalog["types"]?["Circuit.Resistor, Circuit"]?["properties"]?.AsArray();
        JsonNode? resistance = resistorProperties?.FirstOrDefault(p => p?["name"]?.GetValue<string>() == "Resistance");
        Check(resistance?["kind"]?.GetValue<string>() == "quantity" && resistance?["units"]?.GetValue<string>() == "Ω",
            "the resistor's Resistance is a quantity in ohms (U+2126, as LiveSPICE writes it)");

        // Layouts follow properties: a label shows its text.
        JsonObject layouts = Result(await client.Request("catalog.layout", new JsonObject
        {
            ["components"] = new JsonArray(
                new JsonObject { ["type"] = "Circuit.Label, Circuit", ["properties"] = new JsonObject { ["Text"] = "Hello" } },
                new JsonObject { ["type"] = "No.Such.Type, Nowhere", ["properties"] = new JsonObject() }),
        }));
        JsonArray layoutList = layouts["layouts"]!.AsArray();
        Check(layoutList[0]!["texts"]!.AsArray().Any(t => t!["text"]!.GetValue<string>() == "Hello"), "a label's layout shows its text");
        Check(layoutList[1]!["message"] != null && layoutList[1]!["terminals"]!.AsArray().Count == 0, "an unknown type gets a placeholder and a message");

        // Values.
        JsonObject values = Result(await client.Request("values.check", new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject { ["type"] = "Circuit.Resistor, Circuit", ["property"] = "Resistance", ["value"] = "4.7k" },
                new JsonObject { ["type"] = "Circuit.Resistor, Circuit", ["property"] = "Resistance", ["value"] = "banana" },
                new JsonObject { ["type"] = "Circuit.Potentiometer, Circuit", ["property"] = "Sweep", ["value"] = "Logarithmic" },
                new JsonObject { ["type"] = "Circuit.VoltageSource, Circuit", ["property"] = "Voltage", ["value"] = "Sin(2*Pi*440*t) V" }),
        }));
        JsonArray checkedValues = values["items"]!.AsArray();
        Check(checkedValues[3]!["ok"]!.GetValue<bool>(), $"a source voltage in t checks: {checkedValues[3]}");
        Check(checkedValues[0]!["value"]?.GetValue<string>() == "4.7 kΩ", $"'4.7k' checks as {checkedValues[0]!["value"]}");
        Check(checkedValues[1]!["ok"]!.GetValue<bool>() == false, "'banana' is refused as a resistance");
        Check(checkedValues[2]!["value"]?.GetValue<string>() == "Logarithmic", "an enumeration checks");

        // Files: read, check, write, and read back.
        string rat = Path.Combine(testsDir, "Examples", "Pro Co Rat.schx");
        JsonObject read = Result(await client.Request("file.read", new JsonObject { ["path"] = rat }));
        JsonObject document = read["document"]!.AsObject();
        JsonObject check = Result(await client.Request("circuit.check", new JsonObject { ["document"] = document.DeepClone() }));
        int nodes = check["nodes"]!.AsArray().Count;
        string[] pots = check["controls"]!["pots"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray();
        Check(check["errors"]!.AsArray().Count == 0 && nodes > 10 && pots.Length == 3,
            $"the Rat checks: {check["errors"]!.AsArray().Count} errors, {check["warnings"]!.AsArray().Count} warnings, {nodes} nodes, pots {string.Join(", ", pots)}");

        string copy = Path.Combine(scratch, "Rat copy.schx");
        JsonObject written = Result(await client.Request("file.write", new JsonObject { ["path"] = copy, ["document"] = document.DeepClone() }));
        Check(XNode.DeepEquals(XDocument.Load(rat).Root, XDocument.Load(copy).Root), "writing the read document reproduces the file");
        Check(Code(await client.Request("file.write", new JsonObject { ["path"] = copy, ["document"] = document.DeepClone(), ["overwrite"] = false })) == "exists",
            "writing over a file without overwrite is refused");
        Check(Code(await client.Request("file.write", new JsonObject
        {
            ["path"] = copy,
            ["document"] = document.DeepClone(),
            ["expectModified"] = "2000-01-01T00:00:00.0000000Z",
        })) == "modified_on_disk", "writing over a file that changed since it was read is refused");
        Check(Code(await client.Request("file.write", new JsonObject
        {
            ["path"] = copy,
            ["document"] = new JsonObject { ["elements"] = new JsonArray(new JsonObject { ["kind"] = "wire", ["a"] = "nope" }) },
        })) == "bad_document", "a malformed document is refused");

        // A subcircuit's terminals come from the Ports in its body, which the client carries as an opaque string.
        string subcircuitFile = Path.Combine(testsDir, "Circuits", "Subcircuit Trivial Two-Port.schx");
        JsonObject subDocument = Result(await client.Request("file.read", new JsonObject { ["path"] = subcircuitFile }))["document"]!.AsObject();
        JsonObject? subcircuit = subDocument["elements"]!.AsArray().OfType<JsonObject>()
            .Select(e => e["component"] as JsonObject).FirstOrDefault(c => c?["body"] != null);
        int subTerminals = -1;
        if (subcircuit != null)
        {
            JsonObject subLayout = Result(await client.Request("catalog.layout", new JsonObject { ["components"] = new JsonArray(subcircuit.DeepClone()) }));
            subTerminals = subLayout["layouts"]![0]!["terminals"]!.AsArray().Count;
        }
        Check(subTerminals >= 2, $"a subcircuit's layout has its ports as terminals ({subTerminals})");

        // A problem is attributed to its elements.
        var duplicate = new JsonObject
        {
            ["elements"] = new JsonArray(
                new JsonObject { ["id"] = "a", ["kind"] = "symbol", ["position"] = new JsonArray(0, 0), ["rotation"] = 0, ["flip"] = false,
                    ["component"] = new JsonObject { ["type"] = "Circuit.Resistor, Circuit", ["properties"] = new JsonObject { ["Name"] = "R1" } } },
                new JsonObject { ["id"] = "b", ["kind"] = "symbol", ["position"] = new JsonArray(100, 0), ["rotation"] = 0, ["flip"] = false,
                    ["component"] = new JsonObject { ["type"] = "Circuit.Resistor, Circuit", ["properties"] = new JsonObject { ["Name"] = "R1" } } }),
        };
        JsonObject duplicateCheck = Result(await client.Request("circuit.check", new JsonObject { ["document"] = duplicate }));
        JsonNode? clash = duplicateCheck["errors"]!.AsArray().FirstOrDefault();
        Check(clash?["elements"]?.AsArray().Select(e => e!.GetValue<string>()).SequenceEqual(new[] { "a", "b" }) == true,
            $"a duplicate name is an error on both elements: {clash?["message"]}");

        // The clipboard format.
        JsonArray someElements = new(document["elements"]!.AsArray().Take(3).Select(e => e!.DeepClone()).ToArray());
        string xml = Result(await client.Request("schx.toXml", new JsonObject { ["elements"] = someElements }))["xml"]!.GetValue<string>();
        JsonArray pasted = Result(await client.Request("schx.fromXml", new JsonObject { ["xml"] = xml }))["elements"]!.AsArray();
        Check(pasted.Count == 3 && pasted[0]!["kind"]!.GetValue<string>() == someElements[0]!["kind"]!.GetValue<string>(), "elements survive the clipboard format");

        // Folders and recent files.
        JsonObject listing = Result(await client.Request("fs.list", new JsonObject { ["path"] = scratch, ["extensions"] = new JsonArray(".schx") }));
        Check(listing["entries"]!.AsArray().Any(e => e!["name"]!.GetValue<string>() == "Rat copy.schx"), "fs.list finds the written file");
        JsonArray recent = Result(await client.Request("fs.recent", null))["files"]!.AsArray();
        string[] recentFiles = recent.Select(r => r!.GetValue<string>()).ToArray();
        Check(recentFiles.Length >= 3 && recentFiles[0] == subcircuitFile && recentFiles[1] == copy && recentFiles.Contains(rat),
            "recent files are most recent first, written files included");

        // Watching: an outside change to a watched file is reported; the server's own write isn't.
        await client.Request("file.watch", new JsonObject { ["paths"] = new JsonArray(copy) });
        Result(await client.Request("file.write", new JsonObject { ["path"] = copy, ["document"] = document.DeepClone() }));
        bool ownWriteReported = await client.WaitForEvent("file.changed", TimeSpan.FromMilliseconds(800)) != null;
        File.AppendAllText(copy, "\n");
        JsonObject? changed = await client.WaitForEvent("file.changed", TimeSpan.FromSeconds(3));
        Check(!ownWriteReported && changed?["path"]?.GetValue<string>() == copy, "an outside change is reported, and the server's own write isn't");

        // Settings.
        JsonObject settings = Result(await client.Request("settings.set", new JsonObject { ["client"] = new JsonObject { ["layout"] = "x" } }));
        Check(settings["client"]?["layout"]?.GetValue<string>() == "x", "the client's state is stored");
    }

    /// <summary>
    /// The web editor's contract with the file format: SpiceForgeWebUI's conformance test (test/conformance.ts) compares
    /// the client's connectivity with circuit.check's on every test schematic, as loaded and after random edits. It
    /// needs Node and the front end's npm packages; without them it's reported as skipped, not passed.
    /// </summary>
    private static void Conformance(string repo, int port, string testsDir)
    {
        Console.WriteLine("=== The web editor against the server (SpiceForgeWebUI conformance)");
        string webui = Path.Combine(repo, "SpiceForgeWebUI");
        if (!Directory.Exists(Path.Combine(webui, "node_modules", "esbuild")))
        {
            Console.WriteLine("  skipped: run `npm install` in SpiceForgeWebUI first");
            return;
        }
        (int exit, string output) Node(string arguments)
        {
            var start = new ProcessStartInfo("node", arguments)
            {
                WorkingDirectory = webui,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["SPICEFORGE_HOST"] = $"ws://127.0.0.1:{port}";
            start.Environment["SPICEFORGE_TOKEN"] = Token;
            start.Environment["SPICEFORGE_TESTS"] = testsDir;
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output + error.Result);
        }
        try
        {
            var build = Node("build.mjs --test");
            if (build.exit != 0)
            {
                Check(false, $"building the conformance test: {build.output.Trim()}");
                return;
            }
            var run = Node("dist-test/conformance.mjs");
            string[] lines = run.output.Trim().Split('\n');
            foreach (string line in lines.Take(lines.Length - 1).Take(12))
                Console.WriteLine("    " + line.TrimEnd());
            Check(run.exit == 0, lines.LastOrDefault()?.Trim() ?? "no output");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine("  skipped: Node.js isn't on the PATH");
        }
    }

    private static string? Code(JsonObject reply) => reply["error"]?["code"]?.GetValue<string>();

    private static JsonObject Result(JsonObject reply)
    {
        if (reply["error"] is JsonNode error)
            throw new InvalidOperationException($"The server answered an error: {error.ToJsonString()}");
        return reply["result"] as JsonObject ?? new JsonObject();
    }

    private static string? FindServer(string repo)
    {
        foreach (string configuration in new[] { "Release", "Debug" })
        {
            string path = Path.Combine(repo, "SpiceForgeServer", "bin", configuration, "net10.0", "SpiceForgeServer.dll");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<bool> WaitForListening(Process process)
    {
        Task<string?> line = process.StandardOutput.ReadLineAsync();
        if (await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(20))) != line)
            return false;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return line.Result?.Contains("listening") == true;
    }

    /// <summary>A minimal client: requests with ids, and events kept until asked for.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly Dictionary<int, TaskCompletionSource<JsonObject>> waiting = new();
        private readonly List<JsonObject> events = new();
        private readonly object sync = new();
        private Task? reading;
        private int nextId;

        public static async Task<Client> Connect(int port)
        {
            var client = new Client();
            await client.socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api?token={Token}"), CancellationToken.None);
            client.reading = client.Read();
            return client;
        }

        public async Task<JsonObject> Request(string op, JsonObject? args)
        {
            int id = Interlocked.Increment(ref nextId);
            var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
                waiting[id] = reply;
            var request = new JsonObject { ["op"] = op, ["id"] = id, ["args"] = args };
            await socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            if (await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(60))) != reply.Task)
                throw new TimeoutException($"No reply to '{op}'.");
            return reply.Task.Result;
        }

        public async Task<JsonObject?> WaitForEvent(string name, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                lock (sync)
                {
                    JsonObject? found = events.FirstOrDefault(e => e["event"]?.GetValue<string>() == name);
                    if (found != null)
                    {
                        events.Remove(found);
                        return found["data"] as JsonObject;
                    }
                }
                await Task.Delay(50);
            }
            return null;
        }

        private async Task Read()
        {
            var buffer = new byte[1 << 20];
            var message = new MemoryStream();
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                        continue;
                    var json = (JsonObject)JsonNode.Parse(message.ToArray())!;
                    message.SetLength(0);
                    lock (sync)
                    {
                        if (json["re"] is JsonNode re && waiting.Remove(re.GetValue<int>(), out var reply))
                            reply.SetResult(json);
                        else if (json["event"] != null)
                            events.Add(json);
                    }
                }
            }
            catch (WebSocketException)
            {
            }
            lock (sync)
                foreach (var reply in waiting.Values)
                    reply.TrySetResult(new JsonObject { ["error"] = new JsonObject { ["code"] = "closed" } });
        }

        public async ValueTask DisposeAsync()
        {
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch (WebSocketException) { }
            if (reading != null)
                await Task.WhenAny(reading, Task.Delay(1000));
            socket.Dispose();
        }
    }
}
