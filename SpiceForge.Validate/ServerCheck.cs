using System.Buffers.Binary;
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
/// errors, the token check, and watching files;</item>
/// <item>it simulates: the Speaker voltage its scope streams matches SchematicProcessor's own, offline, on the same
/// schematic and input; controls, updates, probes and stopping behave as the spec says.</item>
/// <item>its audio operations work on the internal clock (no device needed): listing, configuring, checking a
/// configuration, the audio test, and a simulation running at the configured rate.</item>
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
        OutputSignals(testsDir).GetAwaiter().GetResult();
        Live(repo, testsDir).GetAwaiter().GetResult();
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>asio "driver" [bufferSize] [seconds] [sampleRate]</c>: runs the Rat on a real ASIO device, for a person with the hardware.
    /// The outputs stay closed (silent), so it's safe with speakers on; the input is an expression, so the load doesn't
    /// depend on what's plugged in. The scope streams a probe throughout, as the editor's would. Reports the device's
    /// rate, buffer and latency, the load, and the dropouts: driver resyncs plus callbacks that overran their buffer.
    /// </summary>
    public static int RunAsio(string repo, string testsDir, string device, int? bufferSize, int seconds, int? sampleRate)
    {
        problems = 0;
        RunAsioAsync(repo, testsDir, device, bufferSize, seconds, sampleRate).GetAwaiter().GetResult();
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private static async Task RunAsioAsync(string repo, string testsDir, string device, int? bufferSize, int seconds, int? sampleRate)
    {
        Console.WriteLine($"=== The Rat on the ASIO device '{device}'{(bufferSize != null ? $", {bufferSize}-sample buffers" : "")}, outputs silent");
        string? server = FindServer(repo);
        if (server == null)
        {
            Check(false, "SpiceForgeServer.dll not found: build SpiceForgeServer first");
            return;
        }
        int port = FreePort();
        string scratch = Path.Combine(Path.GetTempPath(), "SpiceForgeAsioCheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        string buffer = (bufferSize != null ? $" --bufferSize {bufferSize}" : "") + (sampleRate != null ? $" --sampleRate {sampleRate}" : "");
        var start = new ProcessStartInfo("dotnet", $"\"{server}\" --port {port} --token {Token} --settings \"{Path.Combine(scratch, "settings.json")}\" " +
            $"--userFolder \"{scratch}\" --audio ASIO --audioDevice \"{device}\" --audioOutputs={buffer}")
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
            await using Client client = await Client.Connect(port);
            Result(await client.Request("hello", new JsonObject { ["protocol"] = 1, ["client"] = "AsioCheck" }));
            JsonObject read = Result(await client.Request("file.read", new JsonObject { ["path"] = Path.Combine(testsDir, "Examples", "Pro Co Rat.schx") }));
            JsonObject document = read["document"]!.AsObject();
            JsonObject sim = Result(await client.Request("sim.start", new JsonObject { ["document"] = document.DeepClone(), ["title"] = "Rat" }));
            string id = sim["sim"]!.GetValue<string>();
            JsonObject? state = await WaitFor(client, "audio.state", e => e["open"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(20));
            Check(state != null && state["driver"]?.GetValue<string>() == "ASIO" && state["fallback"]?.GetValue<bool>() == false,
                $"the device opens: {state?.ToJsonString()}");
            string? running = await WaitForState(client, id, "running");
            Check(running == "running", $"the simulation runs ({running})");
            JsonObject got = Result(await client.Request("sim.get", new JsonObject { ["sim"] = id }));
            Check(got["sampleRate"]?.GetValue<double>() == state?["sampleRate"]?.GetValue<double>(), $"at the device's rate, {got["sampleRate"]} Hz");

            JsonArray elements = document["elements"]!.AsArray();
            string IdOfType(string type) => elements.First(e => e!["component"]?["type"]?.GetValue<string>().StartsWith(type) == true)!["id"]!.GetValue<string>();
            string input = IdOfType("Circuit.Input"), speaker = IdOfType("Circuit.Speaker");
            JsonArray inputs = state?["inputs"]?.AsArray() ?? new JsonArray();
            if (inputs.Count > 0)
            {
                string first = inputs[0]!.GetValue<string>();
                Check(got["inputs"]?[0]?["source"]?["channel"]?.GetValue<string>() == first, $"the Input plays the device's first input, '{first}'");
            }
            Result(await client.Request("sim.setInput", new JsonObject { ["sim"] = id, ["element"] = input, ["source"] = new JsonObject { ["expression"] = "0.5*Sin(2*Pi*220*t)" } }));
            JsonObject probe = Result(await client.Request("sim.probe", new JsonObject { ["sim"] = id, ["target"] = new JsonObject { ["element"] = speaker, ["terminal"] = "Anode" } }));
            int stream = probe["stream"]!.GetValue<int>();
            await client.Request("sim.scope", new JsonObject { ["sim"] = id, ["enabled"] = true });
            // The first second includes the build's own warm-up: measure after it.
            await Task.Delay(1500);
            while (await client.WaitForEvent("sim.stats", TimeSpan.Zero) != null) { }
            client.Frames.Clear();
            var loads = new List<double>();
            long firstDropouts = -1, lastDropouts = 0;
            for (int s = 0; s < seconds; s++)
            {
                JsonObject? stats = await client.WaitForEvent("sim.stats", TimeSpan.FromSeconds(3));
                if (stats == null)
                    break;
                loads.Add(stats["load"]!.GetValue<double>());
                lastDropouts = stats["dropouts"]!.GetValue<long>();
                if (firstDropouts < 0)
                    firstDropouts = lastDropouts;
                Console.WriteLine($"       load {stats["load"]}, dropouts {lastDropouts}");
            }
            var frames = client.Frames.ToArray().Select(ParseFrame).Where(f => f.Signals.ContainsKey(stream)).ToList();
            long gaps = frames.Zip(frames.Skip(1)).Count(p => p.Second.First != p.First.First + (ulong)p.First.Signals[stream].Length);
            Check(loads.Count == seconds, $"{loads.Count} seconds of stats; load {(loads.Count > 0 ? $"{loads.Average():F3} on average, {loads.Max():F3} at most" : "-")}");
            Check(gaps == 0 && frames.Count > 30 * seconds, $"the scope streams {frames.Count} frames with {gaps} gap(s)");
            Check(lastDropouts - Math.Max(0, firstDropouts) == 0,
                $"no dropouts while it runs: {lastDropouts - Math.Max(0, firstDropouts)} in {loads.Count} s ({firstDropouts} while it started, before it played)");

            // A rebuild while it plays (an edit): the new build takes over at a block boundary.
            await client.Request("sim.update", new JsonObject { ["sim"] = id, ["document"] = document.DeepClone() });
            string? rebuilt = await WaitForState(client, id, "running");
            await Task.Delay(2500);
            JsonObject? after = null;
            for (JsonObject? e; (e = await client.WaitForEvent("sim.stats", TimeSpan.FromMilliseconds(200))) != null;)
                after = e;
            after ??= await client.WaitForEvent("sim.stats", TimeSpan.FromSeconds(2));
            long rebuildDropouts = (after?["dropouts"]?.GetValue<long>() ?? -1) - lastDropouts;
            Check(rebuilt == "running" && rebuildDropouts == 0, $"a rebuild takes over without a dropout ({rebuildDropouts})");

            await client.Request("sim.stop", new JsonObject { ["sim"] = id });
            JsonObject? closed = await WaitFor(client, "audio.state", e => e["open"]?.GetValue<bool>() == false, TimeSpan.FromSeconds(10));
            Check(closed != null, "stopping the simulation closes the device");
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Output signals, in process: a simulation of the Rat given three output channels, as a device would open them, and
    /// run block by block here, with no sound. Out 1 plays the Speakers; Out 2 the voltage across the Speaker (its
    /// output times its 0 dBFS voltage); Out 3 the voltage across the Input (the 0.5 V test sine, through the
    /// resampling filters). Each must play its own, and a change must rebuild.
    /// </summary>
    private static async Task OutputSignals(string testsDir)
    {
        Console.WriteLine("=== Output signals, in process");
        string scratch = Path.Combine(Path.GetTempPath(), "SpiceForgeOutputCheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            using var context = new SpiceForgeServer.ServerContext(new SpiceForgeServer.ServerOptions
            {
                SettingsPath = Path.Combine(scratch, "settings.json"),
                UserFolder = scratch,
            });
            string rat = Path.Combine(testsDir, "Examples", "Pro Co Rat.schx");
            JsonObject document = SchxConverter.ToDocument(XDocument.Load(rat).Root!, SchxConverter.ResolveComponent);
            JsonObject check = SpiceForgeServer.Simulating.Simulation.Check(document);
            JsonObject[] elements = document["elements"]!.AsArray().OfType<JsonObject>().ToArray();
            string IdOf(string type) => elements.First(e => e["component"]?["type"]?.GetValue<string>().StartsWith(type) == true)["id"]!.GetValue<string>();
            string speaker = IdOf("Circuit.Speaker"), input = IdOf("Circuit.Input"), pot = IdOf("Circuit.Potentiometer");

            var sim = new SpiceForgeServer.Simulating.Simulation(context, 1, "Rat", document, check, context.Settings.SimulationDefaults);
            var format = new SpiceForgeServer.Audio.AudioFormat(48000, 256, Array.Empty<string>(), new[] { "Out 1", "Out 2", "Out 3" });
            sim.Prepare(format);
            sim.Start();
            var block = SpiceForgeServer.Audio.AudioBlock.For(format);
            async Task<bool> Running()
            {
                for (int i = 0; i < 600; i++)
                {
                    if (sim.ToJson()["state"]?.GetValue<string>() == "running")
                        return true;
                    await Task.Delay(100);
                }
                return false;
            }
            // A second of each channel's peak, after a second to settle.
            double[] Peaks()
            {
                var peaks = new double[3];
                var outs = new List<float[]>[] { new(), new(), new() };
                for (int b = 0; b < 2 * 48000 / 256; b++)
                {
                    block.Frames = 256;
                    sim.Process(block);
                    if (b >= 48000 / 256)
                        for (int o = 0; o < 3; o++)
                        {
                            outs[o].Add(block.Outputs[o][..256]);
                            for (int n = 0; n < 256; n++)
                                peaks[o] = Math.Max(peaks[o], Math.Abs(block.Outputs[o][n]));
                        }
                }
                lastOuts = outs.Select(l => l.SelectMany(x => x).ToArray()).ToArray();
                return peaks;
            }
            Check(await Running(), "the simulation builds");
            Check(Code(() => sim.SetOutput("Out 9", new JsonObject { ["speakers"] = true })) == "invalid_value", "a channel that isn't open is invalid_value");
            Check(Code(() => sim.SetOutput("Out 2", new JsonObject { ["voltageOf"] = new JsonArray(pot) })) == "invalid_value", "a pot isn't a two-terminal component: invalid_value");
            Check(Code(() => sim.SetOutput("Out 2", new JsonObject { ["expression"] = "V" })) == "unsupported", "an expression is unsupported, for now");
            double[] before = Peaks();
            Check(before.All(p => p > 0.01) && before[0] == before[1] && before[1] == before[2], $"every channel plays the Speakers at first: peaks {string.Join(", ", before.Select(p => p.ToString("F4")))}");

            sim.SetOutput("Out 2", new JsonObject { ["voltageOf"] = new JsonArray(speaker) });
            sim.SetOutput("out 3", new JsonObject { ["voltageOf"] = new JsonArray(input) });
            Check(await Running(), "setting a signal rebuilds");
            double[] after = Peaks();
            float[][] outs = lastOuts!;
            // The Speaker's output is its voltage over its 0 dBFS voltage: Out 2 is Out 1 times that, sample for sample.
            double fullScale = after[1] / after[0], worst = 0;
            for (int n = 0; n < outs[0].Length; n++)
                worst = Math.Max(worst, Math.Abs(outs[1][n] - outs[0][n] * fullScale));
            Check(worst < 1e-5 * Math.Max(1, after[1]), $"Out 2 plays the voltage across the Speaker: its output times {fullScale:G4} V, worst difference {worst:E1}");
            Check(Math.Abs(after[2] - 0.5) < 0.01 && after[0] == before[0], $"Out 3 plays the voltage across the Input, the 0.5 V sine (peak {after[2]:F4} V); Out 1 the Speakers still");
            JsonArray listed = sim.ToJson()["outputs"]!.AsArray();
            Check(listed[1]?["signal"]?["voltageOf"]?[0]?.GetValue<string>() == speaker && listed[0]?["signal"]?["speakers"]?.GetValue<bool>() == true,
                "sim.get lists each channel's signal");
            sim.SetOutput("Out 3", new JsonObject { ["speakers"] = true });
            await Running();
            double[] reset = Peaks();
            Check(reset[2] == reset[0], "setting the Speakers back puts the channel back");
            sim.Stop();
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static float[][]? lastOuts;

    private static string? Code(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (SpiceForgeServer.Protocol.ApiException ex)
        {
            return ex.Code;
        }
    }

    /// <summary>
    /// <c>jack [seconds]</c>: SpiceForgeServer on JACK, with a JACK server of its own: jackd on the dummy backend (a
    /// timer, no sound card) under a private server name, which SpiceForgeServer is pointed at through
    /// <c>JACK_DEFAULT_SERVER</c>, so neither a sound card nor a JACK server of the user's is touched. Checks the device's
    /// ports and settings, the Rat running at the server's rate and buffer size, the connections in the JACK graph (seen
    /// by a client of its own), the load and dropouts, the client leaving when the simulation stops, and the fallback to
    /// the internal clock when the JACK server goes away.
    /// </summary>
    public static int RunJack(string repo, string testsDir, int seconds)
    {
        problems = 0;
        RunJackAsync(repo, testsDir, seconds).GetAwaiter().GetResult();
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private const string JackServerName = "spiceforge-check";

    private static async Task RunJackAsync(string repo, string testsDir, int seconds)
    {
        Console.WriteLine($"=== The Rat on JACK (a dummy-backend server, '{JackServerName}', of this check's own)");
        if (!JackSharp.JackLibrary.IsAvailable)
        {
            Console.WriteLine("  skipped: JACK isn't installed");
            return;
        }
        JackSharp.JackLibrary.SetMessageHandlers(null, null);
        string? jackd = FindJackd();
        string? server = FindServer(repo);
        if (jackd == null || server == null)
        {
            Check(false, jackd == null ? "jackd not found" : "SpiceForgeServer.dll not found: build SpiceForgeServer first");
            return;
        }
        Process? jack = StartJack(jackd);
        if (jack == null)
        {
            Check(false, "jackd started on the dummy backend");
            return;
        }
        int port = FreePort();
        string scratch = Path.Combine(Path.GetTempPath(), "SpiceForgeJackCheck-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo("dotnet", $"\"{server}\" --port {port} --token {Token} --settings \"{Path.Combine(scratch, "settings.json")}\" " +
            $"--userFolder \"{scratch}\" --audio JACK")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["JACK_DEFAULT_SERVER"] = JackServerName;
        Process? spiceforge = null;
        JackSharp.Controller? watch = null;
        try
        {
            spiceforge = Process.Start(start)!;
            if (!await WaitForListening(spiceforge))
            {
                Check(false, "server started");
                return;
            }
            watch = new JackSharp.Controller("spiceforge-check-watch", new JackSharp.ClientOptions { ServerName = JackServerName });
            await using Client client = await Client.Connect(port);
            JsonObject hello = Result(await client.Request("hello", new JsonObject { ["protocol"] = 1, ["client"] = "JackCheck" }));
            Check(hello["capabilities"]?["audioDrivers"]?.AsArray().Any(d => d?.GetValue<string>() == "JACK") == true, "hello lists the JACK driver");
            // SpiceForge's client leaving the graph at any point would drop connections made in a patchbay.
            int left = 0;
            watch.ClientRegistered += (_, e) => { if (e.ClientName == JackSharp_ClientName && !e.Registered) Interlocked.Increment(ref left); };

            // With JACK configured, the client is in the graph before anything runs, its ports connected.
            bool resident = WaitUntil(() =>
                watch.GetConnections($"{JackSharp_ClientName}:in_1").SequenceEqual(new[] { "system:capture_1" })
                && watch.GetConnections($"{JackSharp_ClientName}:out_1").SequenceEqual(new[] { "system:playback_1" })
                && watch.GetConnections($"{JackSharp_ClientName}:out_2").SequenceEqual(new[] { "system:playback_2" }), 10000);
            Check(resident, $"before anything runs, {JackSharp_ClientName} is in the graph: in_1 from system:capture_1, out_1 and out_2 to system:playback_1 and _2");
            JsonObject idle = Result(await client.Request("audio.getConfig", new JsonObject()))["state"]!.AsObject();
            Check(idle["open"]?.GetValue<bool>() == true && idle["running"]?.GetValue<bool>() == false && idle["connections"]?.AsArray().Count == 3,
                $"audio.state: open, nothing running, three ports with their connections");

            // A connection made in a patchbay is reported.
            watch.Connect($"{JackSharp_ClientName}:out_1", "system:playback_2");
            JsonObject? patched = await WaitFor(client, "audio.state", e => e["connections"]?.AsArray().OfType<JsonObject>().Any(c =>
                c["port"]?.GetValue<string>() == $"{JackSharp_ClientName}:out_1" && c["connected"]!.AsArray().Count == 2) == true, TimeSpan.FromSeconds(3));
            Check(patched != null, "a connection made in a patchbay (out_1 to system:playback_2 as well) is reported in audio.state");

            JsonObject info = Result(await client.Request("audio.deviceInfo", new JsonObject { ["driver"] = "JACK" }));
            string[] inputs = info["inputs"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            string[] outputs = info["outputs"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Check(inputs.SequenceEqual(new[] { "system:capture_1", "system:capture_2" }) && outputs.SequenceEqual(new[] { "system:playback_1", "system:playback_2" }),
                $"audio.deviceInfo lists the server's ports: in {string.Join(", ", inputs)}; out {string.Join(", ", outputs)}");
            Check(info["sampleRates"]!.AsArray().Single()!.GetValue<int>() == 48000 && info["bufferSizes"]!.AsArray().Single()!.GetValue<int>() == 256,
                "and its rate and buffer size, as the only ones");
            Check(Code(await client.Request("audio.setConfig", new JsonObject { ["driver"] = "JACK", ["sampleRate"] = 44100 })) == "invalid_value",
                "another rate is invalid_value: the server's settings are the user's");

            JsonObject read = Result(await client.Request("file.read", new JsonObject { ["path"] = Path.Combine(testsDir, "Examples", "Pro Co Rat.schx") }));
            JsonObject document = read["document"]!.AsObject();
            JsonObject sim = Result(await client.Request("sim.start", new JsonObject { ["document"] = document.DeepClone(), ["title"] = "Rat" }));
            string id = sim["sim"]!.GetValue<string>();
            JsonObject? state = await WaitFor(client, "audio.state", e => e["running"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(20));
            Check(state?["driver"]?.GetValue<string>() == "JACK" && state["fallback"]?.GetValue<bool>() == false
                    && state["sampleRate"]?.GetValue<double>() == 48000 && state["bufferSize"]?.GetValue<int>() == 256,
                $"the simulation opens JACK: {state?.ToJsonString()}");
            string? running = await WaitForState(client, id, "running");
            Check(running == "running", $"the simulation runs ({running})");
            JsonObject got = Result(await client.Request("sim.get", new JsonObject { ["sim"] = id }));
            Check(got["sampleRate"]?.GetValue<double>() == 48000 && got["inputs"]?[0]?["source"]?["channel"]?.GetValue<string>() == "system:capture_1",
                "at the server's rate, the Input playing system:capture_1");

            Check(Volatile.Read(ref left) == 0 && watch.GetConnections($"{JackSharp_ClientName}:out_1").Count == 2,
                "the simulation runs in the same client: it never left the graph, and the patchbay connection is still there");
            // The simulation's resampling latency, reported to JACK: its outputs' capture latency is its inputs' plus it.
            int simLatency = got["latency"]!.GetValue<int>();
            uint inLatency = 0, outLatency = 0;
            bool reported = WaitUntil(() =>
            {
                inLatency = watch.GetLatency($"{JackSharp_ClientName}:in_1", JackSharp.LatencyMode.Capture).Max;
                outLatency = watch.GetLatency($"{JackSharp_ClientName}:out_1", JackSharp.LatencyMode.Capture).Max;
                return simLatency > 0 && outLatency == inLatency + simLatency;
            });
            Check(reported, $"the simulation's latency ({simLatency} samples) is reported to JACK: capture latency {inLatency} in, {outLatency} out");

            JsonArray elements = document["elements"]!.AsArray();
            string input = elements.First(e => e!["component"]?["type"]?.GetValue<string>().StartsWith("Circuit.Input") == true)!["id"]!.GetValue<string>();
            Result(await client.Request("sim.setInput", new JsonObject { ["sim"] = id, ["element"] = input, ["source"] = new JsonObject { ["expression"] = "0.5*Sin(2*Pi*220*t)" } }));
            await Task.Delay(1500);
            while (await client.WaitForEvent("sim.stats", TimeSpan.Zero) != null) { }
            var loads = new List<double>();
            long firstDropouts = -1, lastDropouts = 0;
            for (int s = 0; s < seconds; s++)
            {
                JsonObject? stats = await client.WaitForEvent("sim.stats", TimeSpan.FromSeconds(3));
                if (stats == null)
                    break;
                loads.Add(stats["load"]!.GetValue<double>());
                lastDropouts = stats["dropouts"]!.GetValue<long>();
                if (firstDropouts < 0)
                    firstDropouts = lastDropouts;
            }
            Check(loads.Count == seconds && loads.Min() > 0,
                $"{loads.Count} seconds of stats; load {(loads.Count > 0 ? $"{loads.Average():F3} on average, {loads.Max():F3} at most" : "-")}");
            Check(lastDropouts - Math.Max(0, firstDropouts) == 0, $"no dropouts while it runs: {lastDropouts - Math.Max(0, firstDropouts)} in {loads.Count} s");
            JsonObject? levels = await WaitFor(client, "sim.levels", e => e["outputs"]?.AsArray().Count == 2 && e["outputs"]![0]!.GetValue<double>() > 0.01, TimeSpan.FromSeconds(3));
            Check(levels != null, $"each output has its level: {levels?["outputs"]?.ToJsonString()}");

            await client.Request("sim.stop", new JsonObject { ["sim"] = id });
            JsonObject? stopped = await WaitFor(client, "audio.state", e => e["running"]?.GetValue<bool>() == false, TimeSpan.FromSeconds(5));
            Thread.Sleep(300);
            Check(stopped?["open"]?.GetValue<bool>() == true && watch.GetPorts($"^{JackSharp_ClientName}:").Count == 3
                    && watch.GetConnections($"{JackSharp_ClientName}:out_1").Count == 2 && Volatile.Read(ref left) == 0,
                "stopping it leaves SpiceForge's client in the graph, silent, with the patchbay connection");

            // The JACK server going away mid-simulation: the simulation carries on, on the internal clock.
            JsonObject again = Result(await client.Request("sim.start", new JsonObject { ["document"] = document.DeepClone(), ["title"] = "Rat" }));
            string id2 = again["sim"]!.GetValue<string>();
            await WaitForState(client, id2, "running");
            watch.Dispose();
            watch = null;
            StopProcess(jack);
            jack = null;
            JsonObject? fallback = await WaitFor(client, "audio.state", e => e["fallback"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(10));
            Check(fallback?["driver"]?.GetValue<string>() == "None" && fallback["error"]?.GetValue<string>().Contains("shut down") == true,
                $"when the JACK server goes, it falls back to the internal clock: {fallback?["error"]}");
            JsonObject after = Result(await client.Request("sim.get", new JsonObject { ["sim"] = id2 }));
            Check(after["state"]?.GetValue<string>() is "running" or "rebuilding", $"and the simulation carries on ({after["state"]})");
        }
        finally
        {
            watch?.Dispose();
            StopProcess(spiceforge);
            StopProcess(jack);
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private const string JackSharp_ClientName = "SpiceForge";

    private static string? FindJackd()
    {
        if (OperatingSystem.IsWindows())
            foreach (string programs in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (File.Exists(Path.Combine(programs, "JACK2", "jackd.exe")))
                    return Path.Combine(programs, "JACK2", "jackd.exe");
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in new[] { "jackd", "jackd.exe" })
                if (folder.Length > 0 && File.Exists(Path.Combine(folder, name)))
                    return Path.Combine(folder, name);
        return null;
    }

    // jackd on the dummy backend under the check's server name, once a client can connect to it.
    private static Process? StartJack(string jackd)
    {
        var process = Process.Start(new ProcessStartInfo(jackd, $"-n {JackServerName} -d dummy -r 48000 -p 256 -C 2 -P 2")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process == null)
            return null;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        bool up = WaitUntil(() =>
        {
            try
            {
                using var probe = new JackSharp.Controller("spiceforge-check-probe", new JackSharp.ClientOptions { ServerName = JackServerName });
                return true;
            }
            catch (JackSharp.JackException)
            {
                return false;
            }
        }, 10000);
        if (up)
            return process;
        StopProcess(process);
        return null;
    }

    private static void StopProcess(Process? process)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    private static bool WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            Thread.Sleep(50);
        }
        return condition();
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
        var start = new ProcessStartInfo("dotnet", $"\"{server}\" --port {port} --token {Token} --settings \"{Path.Combine(scratch, "settings.json")}\" --userFolder \"{UserFolderIn(scratch)}\"")
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

    /// <summary>The server check's user folder, which doesn't exist when the server starts: it makes it, as LiveSPICE
    /// does.</summary>
    private static string UserFolderIn(string scratch) => Path.Combine(scratch, "Documents", "LiveSPICE");

    private static async Task Exercise(int port, string testsDir, string scratch)
    {
        string userFolder = UserFolderIn(scratch);
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
        Check(hello["protocol"]?.GetValue<int>() == 1 && hello["capabilities"]?["simulation"]?.GetValue<bool>() == true
                && hello["capabilities"]?["audio"]?.GetValue<bool>() == true
                && hello["capabilities"]?["audioDrivers"]?.AsArray().Any(d => d?.GetValue<string>() == "None") == true,
            "hello: protocol 1, simulation and audio, with the None driver");

        Check(hello["folders"]?["user"]?.GetValue<string>() == userFolder && Directory.Exists(userFolder),
            "the user folder, missing at startup, is made, and hello names it");

        // LiveSPICE's examples, for the file dialogs: run from the repository, the submodule's.
        string? examples = hello["folders"]?["examples"]?.GetValue<string>();
        int exampleCount = 0;
        if (examples != null)
        {
            JsonObject exampleListing = Result(await client.Request("fs.list", new JsonObject { ["path"] = examples, ["extensions"] = new JsonArray(".schx") }));
            exampleCount = exampleListing["entries"]!.AsArray().Count(e => e?["kind"]?.GetValue<string>() == "file");
        }
        Check(exampleCount >= 10, $"hello names the examples folder, and it lists them: {exampleCount} in {examples ?? "(none)"}");

        // The About dialog's information, and its license texts, served beside the server.
        JsonObject? about = hello["about"] as JsonObject;
        string[] files = new[] { about?["licenseFile"]?.GetValue<string>() ?? "" }
            .Concat(about?["components"]?.AsArray().Select(c => c?["file"]?.GetValue<string>() ?? "") ?? []).Distinct().ToArray();
        Check(about?["version"]?.GetValue<string>() is { Length: > 0 } && about["copyright"]?.GetValue<string>() is { Length: > 0 }
                && files.Length > 1,
            $"hello: about, version {about?["version"]}, commit {about?["commit"]}, {files.Length} license files");
        using (var http = new HttpClient())
        {
            var missing = new List<string>();
            foreach (string file in files)
            {
                HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/licenses/{Uri.EscapeDataString(file)}");
                if (!response.IsSuccessStatusCode || !(await response.Content.ReadAsStringAsync()).Contains("Copyright", StringComparison.OrdinalIgnoreCase))
                    missing.Add(file);
            }
            Check(missing.Count == 0, missing.Count == 0 ? "every license text is served, with its copyright" : $"license texts not served: {string.Join(", ", missing)}");
        }
        Check(Code(await client.Request("no.such", null)) == "unknown_op", "an unknown operation is unknown_op");
        await Audio(client);

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

        // New folders, and deleting schematics (only schematics).
        string newFolder = Path.Combine(scratch, "New folder");
        JsonObject made = Result(await client.Request("fs.mkdir", new JsonObject { ["path"] = newFolder }));
        Check(made["path"]?.GetValue<string>() == newFolder && Directory.Exists(newFolder), "fs.mkdir creates a folder");
        Check(Code(await client.Request("fs.mkdir", new JsonObject { ["path"] = newFolder })) == "exists", "fs.mkdir where a folder is refuses");
        Check(Code(await client.Request("fs.mkdir", new JsonObject { ["path"] = Path.Combine(scratch, "missing", "deeper") })) == "not_found",
            "fs.mkdir doesn't create a chain of folders");
        string notSchematic = Path.Combine(scratch, "notes.txt");
        File.WriteAllText(notSchematic, "keep me");
        Check(Code(await client.Request("fs.delete", new JsonObject { ["path"] = notSchematic })) == "invalid_value" && File.Exists(notSchematic),
            "fs.delete refuses a file that isn't a schematic");
        Check(Code(await client.Request("fs.delete", new JsonObject { ["path"] = newFolder })) == "invalid_value" && Directory.Exists(newFolder),
            "fs.delete refuses a folder");
        await client.Request("file.watch", new JsonObject { ["paths"] = new JsonArray() });
        Result(await client.Request("fs.delete", new JsonObject { ["path"] = copy }));
        string[] recentAfter = Result(await client.Request("fs.recent", null))["files"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
        Check(!File.Exists(copy) && !recentAfter.Contains(copy, StringComparer.OrdinalIgnoreCase), "fs.delete deletes a schematic and forgets it");
        Check(Code(await client.Request("fs.delete", new JsonObject { ["path"] = copy })) == "not_found", "fs.delete of a missing file is not_found");

        // Settings.
        JsonObject settings = Result(await client.Request("settings.set", new JsonObject { ["client"] = new JsonObject { ["layout"] = "x" } }));
        Check(settings["client"]?["layout"]?.GetValue<string>() == "x", "the client's state is stored");

        // Component folders: a missing one is refused; an added one reloads the catalogue with its parts.
        Check(Code(await client.Request("settings.set", new JsonObject { ["componentFolders"] = new JsonArray(Path.Combine(scratch, "missing")) })) == "invalid_value",
            "a component folder that doesn't exist is refused");
        string parts = Path.Combine(scratch, "Parts");
        Directory.CreateDirectory(parts);
        File.WriteAllText(Path.Combine(parts, "Mine.xml"),
            "<Library><Component _Type=\"Circuit.Diode, Circuit, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null\" IS=\"2 pA\" Type=\"Diode\" Name=\"D1\" PartNumber=\"CheckPart\" Description=\"A user part\" /></Library>");
        while (await client.WaitForEvent("catalog.changed", TimeSpan.Zero) != null) { }
        await client.Request("settings.set", new JsonObject { ["componentFolders"] = new JsonArray(parts) });
        JsonObject? reloaded = await client.WaitForEvent("catalog.changed", TimeSpan.FromSeconds(30));
        bool hasPart = reloaded?["entries"]?.AsObject().Any(e => e.Value?["component"]?["properties"]?["PartNumber"]?.GetValue<string>() == "CheckPart") == true;
        await client.Request("settings.set", new JsonObject { ["componentFolders"] = new JsonArray() });
        JsonObject? restored = await client.WaitForEvent("catalog.changed", TimeSpan.FromSeconds(30));
        bool gone = restored?["entries"]?.AsObject().All(e => e.Value?["component"]?["properties"]?["PartNumber"]?.GetValue<string>() != "CheckPart") == true;
        Check(hasPart && gone, "adding a component folder reloads the catalogue with its parts, and removing it takes them out");

        await Simulate(client, rat, document);
    }

    /// <summary>
    /// Simulates the Rat through the API and checks what streams back against SchematicProcessor run offline on the
    /// same file, input and settings: the server builds from the client's document, drives the Input from an expression
    /// and reads the Speaker's node through a probe, so agreement covers all of that.
    /// </summary>
    private static async Task Simulate(Client client, string rat, JsonObject document)
    {
        Console.WriteLine("=== Simulation");
        Check(Code(await client.Request("sim.start", new JsonObject())) == "bad_request", "sim.start without a document is refused");
        JsonObject sim = Result(await client.Request("sim.start", new JsonObject { ["document"] = document.DeepClone(), ["title"] = "Rat" }));
        string id = sim["sim"]!.GetValue<string>();
        Check(sim["state"]?.GetValue<string>() == "building" && sim["pots"]!.AsArray().Count == 3, $"sim.start answers at once, building, with the pots ({id})");
        string? state = await WaitForState(client, id, "running");
        Check(state == "running", $"it starts running ({state})");

        JsonArray elements = document["elements"]!.AsArray();
        string IdOfType(string type) => elements.First(e => e!["component"]?["type"]?.GetValue<string>().StartsWith(type) == true)!["id"]!.GetValue<string>();
        string speaker = IdOfType("Circuit.Speaker"), input = IdOfType("Circuit.Input");
        JsonObject running = Result(await client.Request("sim.get", new JsonObject { ["sim"] = id }));
        string? source = running["inputs"]?[0]?["source"]?["expression"]?.GetValue<string>();
        Check(running["inputs"]?[0]?["element"]?.GetValue<string>() == input && source != null, $"the Input plays a test signal: {source}");

        JsonObject probe = Result(await client.Request("sim.probe", new JsonObject { ["sim"] = id, ["target"] = new JsonObject { ["element"] = speaker, ["terminal"] = "Anode" } }));
        int stream = probe["stream"]?.GetValue<int>() ?? -1;
        Check(probe["color"]?.GetValue<string>() == "red" && stream > 0, $"a probe on the Speaker: {probe["label"]}, stream {stream}");
        JsonObject again = Result(await client.Request("sim.probe", new JsonObject { ["sim"] = id, ["target"] = new JsonObject { ["element"] = speaker, ["terminal"] = "Anode" } }));
        Check(again["id"]?.GetValue<string>() == probe["id"]?.GetValue<string>(), "a second probe on the same node moves the first");
        Check(Code(await client.Request("sim.probe", new JsonObject { ["sim"] = id, ["target"] = new JsonObject { ["wire"] = "nope" } })) == "not_found",
            "a probe on nothing is not_found");

        client.Frames.Clear();
        await client.Request("sim.scope", new JsonObject { ["sim"] = id, ["enabled"] = true });
        await Task.Delay(1500);
        var frames = client.Frames.ToArray().Select(ParseFrame).Where(f => f.Signals.ContainsKey(stream)).ToList();
        bool contiguous = frames.Count > 30 && frames.Zip(frames.Skip(1)).All(p => p.Second.First == p.First.First + (ulong)p.First.Signals[stream].Length);
        Check(contiguous, $"the scope streams frames without gaps ({frames.Count} in 1.5 s)");
        JsonObject? stats = await client.WaitForEvent("sim.stats", TimeSpan.FromSeconds(3));
        Check(stats?["cappedSteps"] is JsonValue capped && capped.TryGetValue(out long _) && stats["load"]?.GetValue<double>() > 0,
            $"sim.stats reports the load and capped steps: {stats?.ToJsonString()}");

        // The same schematic, input and settings, offline: the probe reads each sample's last step.
        if (frames.Count > 0)
        {
            ulong first = frames[0].First;
            float[] streamed = frames.SelectMany(f => f.Signals[stream]).ToArray();
            long length = (long)first + streamed.Length;
            var processor = new SpiceForge.SchematicProcessor(rat, 48000, new SpiceForge.SchematicProcessorOptions
            {
                Oversample = 4, IterationCap = 32, Resampling = SpiceForge.Resampling.Sinc, Probes = true,
            });
            string node = probe["node"]!.GetValue<string>();
            int index = processor.ProbeNodes.ToList().IndexOf(node);
            var offline = new double[length];
            var inputs = new[] { new double[256] };
            var outputs = new[] { new double[256] };
            for (long start = 0; start < length; start += 256)
            {
                for (int n = 0; n < 256; n++)
                    inputs[0][n] = 0.5 * Math.Sin(2 * Math.PI * 220 * ((start + n) / 48000.0)) / processor.InputFullScale;
                processor.Process(inputs, outputs, 256);
                for (int n = 0; n < 256 && start + n < length; n++)
                    offline[start + n] = index >= 0 ? processor.ProbeSamples[index][n] : double.NaN;
            }
            double worst = 0, peak = 0;
            for (int n = 0; n < streamed.Length; n++)
            {
                worst = Math.Max(worst, Math.Abs(streamed[n] - offline[(long)first + n]));
                peak = Math.Max(peak, Math.Abs(offline[(long)first + n]));
            }
            Check(index >= 0 && peak > 0.05 && worst < 1e-5,
                $"the Speaker voltage matches SchematicProcessor offline: {streamed.Length} samples from {first}, peak {peak:F3} V, worst difference {worst:E1} V");
        }

        await client.Request("sim.set", new JsonObject { ["sim"] = id, ["pots"] = new JsonObject { ["Distortion"] = 1.0 } });
        // Earlier sim.controls events (from the build) are still queued: look for one with the new position.
        double? distortion = null;
        for (JsonObject? controls; distortion != 1.0 && (controls = await client.WaitForEvent("sim.controls", TimeSpan.FromSeconds(2))) != null;)
            distortion = controls["pots"]?.AsArray().FirstOrDefault(p => p!["name"]?.GetValue<string>() == "Distortion")?["position"]?.GetValue<double>();
        Check(distortion == 1.0, "sim.set moves a pot, and sim.controls says so");
        Check(Code(await client.Request("sim.set", new JsonObject { ["sim"] = id, ["pots"] = new JsonObject { ["Distortion"] = 2.0 } })) == "invalid_value",
            "a pot position outside 0 to 1 is refused");
        Check(Code(await client.Request("sim.setInput", new JsonObject { ["sim"] = id, ["element"] = input, ["source"] = new JsonObject { ["expression"] = "Sin(2*Pi*f*t)" } })) == "invalid_value",
            "an expression in anything but t is refused");
        Check(Code(await client.Request("sim.setInput", new JsonObject { ["sim"] = id, ["element"] = input, ["source"] = new JsonObject { ["channel"] = "In 1" } })) == "invalid_value",
            "a channel the device hasn't opened is refused");
        Check(running["sampleRate"]?.GetValue<double>() == 48000, "it runs at the device's rate");
        Check(Code(await client.Request("audio.test", new JsonObject { ["enabled"] = true })) == "audio_busy", "the audio test is audio_busy while a simulation runs");
        Check(Code(await client.Request("sim.start", new JsonObject { ["document"] = document.DeepClone() })) == "audio_busy",
            "a second sim.start without replace is audio_busy");

        await client.Request("sim.update", new JsonObject { ["sim"] = id, ["document"] = document.DeepClone() });
        state = await WaitForState(client, id, "running");
        JsonObject updated = Result(await client.Request("sim.get", new JsonObject { ["sim"] = id }));
        double? kept = updated["pots"]?.AsArray().FirstOrDefault(p => p!["name"]?.GetValue<string>() == "Distortion")?["position"]?.GetValue<double>();
        Check(state == "running" && kept == 1.0 && updated["probes"]?.AsArray().Count == 1, "sim.update rebuilds, keeping pot positions and probes");

        var broken = (JsonObject)document.DeepClone();
        foreach (JsonNode? e in broken["elements"]!.AsArray())
            if (e?["component"]?["properties"]?["Name"]?.GetValue<string>() == "R1")
                e["component"]!["properties"]!["Name"] = "R2";
        Check(Code(await client.Request("sim.update", new JsonObject { ["sim"] = id, ["document"] = broken })) == "build_failed",
            "an update that doesn't build is build_failed, and the simulation carries on");

        await client.Request("sim.stop", new JsonObject { ["sim"] = id });
        state = await WaitForState(client, id, "stopped");
        Check(state == "stopped" && Code(await client.Request("sim.get", new JsonObject { ["sim"] = id })) == "not_found", "sim.stop stops it");
    }

    /// <summary>
    /// The audio operations on the internal clock, which every machine has. ASIO devices are only listed (that reads the
    /// registry): opening one is left to a person with the hardware, so this check never takes over a sound card.
    /// </summary>
    private static async Task Audio(Client client)
    {
        Console.WriteLine("=== Audio, on the internal clock");
        JsonObject devices = Result(await client.Request("audio.devices", new JsonObject()));
        JsonObject? none = devices["drivers"]?.AsArray().OfType<JsonObject>().FirstOrDefault(d => d["name"]?.GetValue<string>() == "None");
        Check(none?["devices"]?[0]?["name"]?.GetValue<string>() == "Internal clock",
            $"audio.devices lists the internal clock ({string.Join(", ", devices["drivers"]!.AsArray().Select(d => $"{d!["name"]}: {d["devices"]!.AsArray().Count}"))})");
        JsonObject info = Result(await client.Request("audio.deviceInfo", new JsonObject { ["driver"] = "None" }));
        Check(info["sampleRates"]?.AsArray().Any(r => r?.GetValue<int>() == 48000) == true && info["inputs"]?.AsArray().Count == 0,
            "audio.deviceInfo: the clock's rates, and no channels");
        JsonObject config = Result(await client.Request("audio.getConfig", new JsonObject()));
        Check(config["driver"]?.GetValue<string>() == "None" && config["state"]?["open"]?.GetValue<bool>() == false,
            "audio.getConfig: the internal clock by default, closed while nothing runs");
        JsonObject bad = await client.Request("audio.setConfig", new JsonObject { ["driver"] = "None", ["sampleRate"] = 12345 });
        Check(Code(bad) == "invalid_value" && bad["error"]?["details"]?.AsArray().Count > 0, "a rate the device doesn't offer is invalid_value, listing those it does");
        Check(Code(await client.Request("audio.setConfig", new JsonObject { ["driver"] = "Nonesuch" })) == "invalid_value", "an unknown driver is invalid_value");
        JsonObject set = Result(await client.Request("audio.setConfig", new JsonObject { ["driver"] = "None", ["sampleRate"] = 44100, ["bufferSize"] = 128 }));
        Check(set["sampleRate"]?.GetValue<int>() == 44100 && set["bufferSize"]?.GetValue<int>() == 128, "audio.setConfig applies a rate and buffer size");
        JsonObject saved = Result(await client.Request("settings.get", new JsonObject()));
        Check(saved["audio"]?["sampleRate"]?.GetValue<int>() == 44100, "and saves them in the settings");

        Result(await client.Request("audio.test", new JsonObject { ["enabled"] = true }));
        JsonObject? opened = await WaitFor(client, "audio.state", e => e["open"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(3));
        Check(opened?["open"]?.GetValue<bool>() == true && opened["sampleRate"]?.GetValue<double>() == 44100 && opened["bufferSize"]?.GetValue<int>() == 128,
            $"audio.test opens the device at the configured rate: {opened?.ToJsonString()}");
        Check(await client.WaitForEvent("audio.levels", TimeSpan.FromSeconds(2)) != null, "audio.levels arrive during the test");
        Result(await client.Request("audio.test", new JsonObject { ["enabled"] = false }));
        JsonObject? closed = await WaitFor(client, "audio.state", e => e["open"]?.GetValue<bool>() == false, TimeSpan.FromSeconds(3));
        Check(closed?["open"]?.GetValue<bool>() == false, "stopping the test closes the device");

        // Back to the defaults, which the simulation check below compares against offline.
        JsonObject reset = Result(await client.Request("audio.setConfig", new JsonObject { ["driver"] = "None", ["sampleRate"] = null, ["bufferSize"] = null }));
        Check(reset["sampleRate"] == null && reset["bufferSize"] == null, "a null puts a setting back to the device's default");
    }

    /// <summary>The first event of that name that matches, skipping earlier ones; null if none comes in time.</summary>
    private static async Task<JsonObject?> WaitFor(Client client, string name, Func<JsonObject, bool> match, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            JsonObject? e = await client.WaitForEvent(name, until - DateTime.UtcNow);
            if (e == null || match(e))
                return e;
        }
        return null;
    }

    /// <summary>Waits for a simulation to reach a state, or an error; returns the state it got to.</summary>
    private static async Task<string?> WaitForState(Client client, string id, string wanted)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < until)
        {
            JsonObject? e = await client.WaitForEvent("sim.state", until - DateTime.UtcNow);
            if (e == null)
                return null;
            string? state = e["state"]?.GetValue<string>();
            if (e["sim"]?.GetValue<string>() == id && (state == wanted || state == "error"))
                return state == "error" ? $"error: {e["message"]}" : state;
        }
        return null;
    }

    private sealed record Frame(ulong First, Dictionary<int, float[]> Signals);

    private static Frame ParseFrame(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
        var signals = new Dictionary<int, float[]>();
        int at = 24;
        for (int s = 0; s < count; s++)
        {
            int stream = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 4)..]);
            var samples = new float[n];
            for (int i = 0; i < n; i++)
                samples[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(at + 8 + 4 * i)..]);
            signals[stream] = samples;
            at += 8 + 4 * n;
        }
        return new Frame(first, signals);
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

    /// <summary>Whether the server announces it's listening within 20 seconds. Its About summary comes first.</summary>
    private static async Task<bool> WaitForListening(Process process)
    {
        _ = process.StandardError.ReadToEndAsync();
        Task<bool> listening = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is string line)
                if (line.Contains("listening"))
                    return true;
            return false;
        });
        if (await Task.WhenAny(listening, Task.Delay(TimeSpan.FromSeconds(20))) != listening || !listening.Result)
            return false;
        _ = process.StandardOutput.ReadToEndAsync();
        return true;
    }

    /// <summary>A minimal client: requests with ids, and events kept until asked for.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly Dictionary<int, TaskCompletionSource<JsonObject>> waiting = new();
        private readonly List<JsonObject> events = new();

        /// <summary>Binary frames (scope samples) as they came.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<byte[]> Frames { get; } = new();
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
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        Frames.Enqueue(message.ToArray());
                        message.SetLength(0);
                        continue;
                    }
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
