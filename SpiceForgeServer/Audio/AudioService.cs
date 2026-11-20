using System.Text.Json.Nodes;
using AudioIOSharp;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer.Audio;

/// <summary>
/// The audio device and the <c>audio.*</c> operations (WebSocketApi.md, Audio), over AudioIOSharp's
/// <see cref="AudioEngine"/>. One client runs on the device at a time: a simulation, or the audio test. The device opens
/// when a client attaches and closes when it detaches, so an idle server doesn't hold an ASIO driver another program
/// wants. If the configured device can't be opened, or fails while running, the client carries on on the internal
/// clock, and <c>audio.state</c> says why. The engine does all that; this class keeps what is SpiceForge's: the API,
/// the settings, the broadcasts and the audio test. The library's errors (<see cref="AudioException"/>) become API
/// errors where every request's error is answered (Connection).
/// </summary>
public sealed class AudioService : IDisposable
{
    /// <summary>SpiceForge's client name in the JACK graph, which patchbays show.</summary>
    public const string JackClientName = "SpiceForge";

    private readonly Hub hub;
    private readonly LogHub log;
    private readonly SettingsStore settings;
    private readonly AudioEngine engine;
    // Attaching and detaching clients, and the test's check that nothing else runs, happen one at a time, so a
    // simulation starting can't slip in between the test's check and its attaching.
    private readonly SemaphoreSlim clients = new(1, 1);

    public AudioService(Hub hub, LogHub log, SettingsStore settings, AudioConfig initial)
    {
        this.hub = hub;
        this.log = log;
        this.settings = settings;
        engine = new AudioEngine(initial, AudioEngine.PlatformDrivers(JackClientName),
            driver => settings.AudioFor(driver) is JsonObject last ? AudioJson.Config(last) : null);
        engine.StateChanged += BroadcastState;
        // The engine went back to no device after the device failed: save it, and say so.
        engine.ConfigChanged += config =>
        {
            settings.SetAudio(config.ToJson());
            hub.Broadcast("settings.changed", settings.Snapshot());
        };
        engine.Log += (level, message) => log.Write(level switch
        {
            AudioLogLevel.Error => MessageType.Error,
            AudioLogLevel.Warning => MessageType.Warning,
            _ => MessageType.Info,
        }, "audio", message);
        // A driver whose stream stays open (JACK) opens at startup, so its ports are in the graph from the start.
        _ = Task.Run(engine.Start);
    }

    /// <summary>The drivers this platform has, the internal clock first.</summary>
    public IReadOnlyList<AudioDriver> Drivers => engine.Drivers;

    /// <summary>The drivers this platform has: the internal clock, ASIO on Windows, and JACK where it's installed.</summary>
    public static IReadOnlyList<string> DriverNames
    {
        get
        {
            var names = new List<string> { ClockDriver.Name };
            if (OperatingSystem.IsWindows())
                names.Add(AsioDriver.Name);
            if (JackDriver.IsAvailable)
                names.Add(JackDriver.Name);
            return names;
        }
    }

    public AudioDriver Driver(string name) => engine.Driver(name);

    /// <summary>The client running on the device, if any.</summary>
    public IAudioClient? Client => engine.Client;

    public AudioConfig Config => engine.Config;

    /// <summary>The configuration a run starts with: the saved one, with whatever the command line gives on top. A
    /// different driver on the command line starts from the configuration last used with it, else its defaults.</summary>
    public static AudioConfig Startup(SettingsStore settings, ServerOptions options)
    {
        AudioConfig saved = AudioJson.Config(settings.Audio);
        AudioConfig c = saved;
        if (options.AudioDriver != null && !string.Equals(options.AudioDriver, saved.Driver, StringComparison.OrdinalIgnoreCase))
            c = LastUsed(settings, options.AudioDriver);
        return c with
        {
            Device = options.AudioDevice ?? c.Device,
            Inputs = options.AudioInputs ?? c.Inputs,
            Outputs = options.AudioOutputs ?? c.Outputs,
            SampleRate = options.SampleRate ?? c.SampleRate,
            BufferSize = options.BufferSize ?? c.BufferSize,
        };
    }

    // ---- Clients ----

    /// <summary>Runs <paramref name="newClient"/> on the device, preparing it and starting it, falling back to the
    /// internal clock if the device can't be opened. Any other client is detached first.</summary>
    public async Task Attach(IAudioClient newClient)
    {
        await clients.WaitAsync();
        try
        {
            await engine.Attach(newClient);
        }
        finally
        {
            clients.Release();
        }
    }

    public async Task Detach(IAudioClient oldClient)
    {
        await clients.WaitAsync();
        try
        {
            await engine.Detach(oldClient);
        }
        finally
        {
            clients.Release();
        }
    }

    /// <summary>The running client's latency changed (a rebuild): drivers that report it pass it on.</summary>
    public void LatencyChanged(IAudioClient changed) => engine.LatencyChanged(changed);

    // ---- State ----

    public JsonObject StateJson() => engine.State.ToJson();

    private void BroadcastState() => hub.Broadcast("audio.state", StateJson());

    // ---- Operations ----

    public async Task<JsonNode?> Devices(Session session, Args args)
    {
        var drivers = new JsonArray();
        foreach (AudioDriver driver in Drivers)
        {
            IReadOnlyList<string> devices;
            try { devices = await driver.Devices(); }
            catch (Exception ex)
            {
                log.Write(MessageType.Warning, "audio", $"Couldn't list the {driver.DriverName} devices: {ex.Message}");
                devices = Array.Empty<string>();
            }
            drivers.Add(new JsonObject
            {
                ["name"] = driver.DriverName,
                ["devices"] = Json.Array(devices.Select(d => (JsonNode?)new JsonObject { ["name"] = d })),
            });
        }
        return new JsonObject { ["drivers"] = drivers };
    }

    public async Task<JsonNode?> DeviceInfo(Session session, Args args)
    {
        AudioDriver driver = Driver(args.String("driver"));
        string device = args.OptionalString("device") ?? (await driver.Devices()).FirstOrDefault()
            ?? throw new ApiException(ErrorCodes.NotFound, $"There are no {driver.DriverName} devices.");
        return (await driver.Info(device)).ToJson();
    }

    public Task<JsonNode?> GetConfig(Session session, Args args)
    {
        JsonObject json = Config.ToJson();
        json["state"] = StateJson();
        json["byDriver"] = settings.AudioByDriver;
        return Task.FromResult<JsonNode?>(json);
    }

    /// <summary>The configuration last used with a driver, or that driver's defaults.</summary>
    private static AudioConfig LastUsed(SettingsStore settings, string driver) =>
        settings.AudioFor(driver) is JsonObject last ? AudioJson.Config(last) with { Driver = driver } : new AudioConfig { Driver = driver };

    /// <summary><c>audio.setConfig</c>: checks the configuration against the device, saves it, and moves a running
    /// client to it. Fields left out keep their values, except that a new driver starts from its defaults.</summary>
    public async Task<JsonNode?> SetConfig(Session session, Args args)
    {
        AudioConfig next = Merge(Config, args);
        AudioDriver driver = Driver(next.Driver);
        // A remembered device that's gone (uninstalled) gives way to the driver's first, with its default channels.
        if (!args.Has("device") && next.Device != null
            && !(await driver.Devices()).Contains(next.Device, StringComparer.OrdinalIgnoreCase))
            next = next with { Device = null, Inputs = null, Outputs = null };
        AudioConfig applied = await engine.SetConfig(next);
        settings.SetAudio(applied.ToJson());
        hub.Broadcast("settings.changed", settings.Snapshot());
        JsonObject result = applied.ToJson();
        result["state"] = StateJson();
        result["byDriver"] = settings.AudioByDriver;
        return result;
    }

    private AudioConfig Merge(AudioConfig current, Args args)
    {
        AudioConfig c = current;
        // Another driver starts from what was last used with it: its device, channels, rate and buffer size.
        if (args.OptionalString("driver") is string driver && !string.Equals(driver, current.Driver, StringComparison.OrdinalIgnoreCase))
            c = LastUsed(settings, driver);
        if (args.Has("device"))
            c = c with { Device = args.OptionalString("device") };
        if (args.Has("inputs"))
            c = c with { Inputs = args.Strings("inputs") };
        if (args.Has("outputs"))
            c = c with { Outputs = args.Strings("outputs") };
        if (args.Has("sampleRate"))
            c = c with { SampleRate = Positive(args, "sampleRate") };
        if (args.Has("bufferSize"))
            c = c with { BufferSize = Positive(args, "bufferSize") };
        // An explicit null puts a field back to the device's default.
        foreach (string field in new[] { "inputs", "outputs", "sampleRate", "bufferSize", "device" })
            if (args.Contains(field) && args[field] == null)
                c = field switch
                {
                    "inputs" => c with { Inputs = null },
                    "outputs" => c with { Outputs = null },
                    "sampleRate" => c with { SampleRate = null },
                    "bufferSize" => c with { BufferSize = null },
                    _ => c with { Device = null },
                };
        return c;
    }

    private static int Positive(Args args, string name)
    {
        int value = args.Int(name, 0);
        return value > 0 ? value : throw new ApiException(ErrorCodes.InvalidValue, $"'{name}' must be a positive whole number.");
    }

    public async Task<JsonNode?> ControlPanel(Session session, Args args)
    {
        await engine.ShowControlPanel(args.OptionalString("driver"), args.OptionalString("device"));
        return null;
    }

    /// <summary><c>audio.test</c>: plays a tone on every output, or loops the sum of the inputs to them, streaming the
    /// inputs' sum to the scope. A test already running with the other signal fades out and is replaced.</summary>
    public async Task<JsonNode?> Test(Session session, Args args)
    {
        bool enabled = args.Bool("enabled", true);
        if (enabled)
        {
            AudioTestSignal signal = args.OptionalString("signal")?.ToLowerInvariant() switch
            {
                null or "tone" => AudioTestSignal.Tone,
                "inputs" => AudioTestSignal.Inputs,
                string other => throw new ApiException(ErrorCodes.InvalidValue, $"'{other}' isn't a test signal: 'tone' or 'inputs'."),
            };
            if (Client is AudioTest running && running.Signal != signal)
                await StopTest();
            AudioTest newTest;
            await clients.WaitAsync();
            try
            {
                if (Client is AudioTest)
                    return null;
                if (Client != null)
                    throw new ApiException(ErrorCodes.AudioBusy, "A simulation is running on the audio device.");
                newTest = new AudioTest(hub, signal);
                await engine.Attach(newTest);
            }
            finally
            {
                clients.Release();
            }
            newTest.Start();
        }
        else
            await StopTest();
        return null;
    }

    /// <summary>Stops the test, after fading its output to silence, so the device doesn't close on a click. A device
    /// that has stopped calling doesn't hold it up for more than half a second.</summary>
    public async Task StopTest()
    {
        if (Client is AudioTest running)
        {
            await running.FadeOut(TimeSpan.FromMilliseconds(500));
            running.Stop();
            await Detach(running);
        }
    }

    public void Dispose()
    {
        if (Client is AudioTest running)
            running.Stop();
        engine.Dispose();
    }
}
