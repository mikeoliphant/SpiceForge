using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;
using Util;

namespace SpiceForgeServer.Audio;

/// <summary>
/// The audio device and the <c>audio.*</c> operations (WebSocketApi.md, Audio). One client runs on the device at a
/// time: a simulation, or the audio test. The device opens when a client attaches and closes when it detaches, so an
/// idle server doesn't hold an ASIO driver another program wants. If the configured device can't be opened, or fails
/// while running, the client carries on on the internal clock, and <c>audio.state</c> says why.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly Hub hub;
    private readonly LogHub log;
    private readonly SettingsStore settings;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ClockDriver clock = new();
    private readonly AudioDriver? asio;
    private readonly AudioDriver? jack;

    // Guarded by gate.
    private AudioConfig config;
    private AudioStream? stream;
    private IAudioClient? client;
    private string? error;

    public AudioService(Hub hub, LogHub log, SettingsStore settings, AudioConfig initial)
    {
        this.hub = hub;
        this.log = log;
        this.settings = settings;
        config = initial;
        if (OperatingSystem.IsWindows())
            asio = new AsioDriver();
        if (JackDriver.IsAvailable)
            jack = new JackDriver();
        // A driver whose stream stays open (JACK) opens at startup, so its ports are in the graph from the start.
        _ = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                if (stream == null && client == null)
                    await OpenLocked();
            }
            finally
            {
                gate.Release();
            }
            BroadcastState();
        });
    }

    /// <summary>The drivers this platform has, the internal clock first.</summary>
    public IReadOnlyList<AudioDriver> Drivers => new[] { clock, asio, jack }.OfType<AudioDriver>().ToList();

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

    public AudioDriver Driver(string name) =>
        Drivers.FirstOrDefault(d => string.Equals(d.DriverName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ApiException(ErrorCodes.InvalidValue, $"There's no audio driver '{name}'. The drivers are: {string.Join(", ", Drivers.Select(d => d.DriverName))}.");

    /// <summary>The client running on the device, if any.</summary>
    public IAudioClient? Client => client;

    public AudioConfig Config => config;

    /// <summary>The configuration a run starts with: the saved one, with whatever the command line gives on top. A
    /// different driver on the command line starts from the configuration last used with it, else its defaults.</summary>
    public static AudioConfig Startup(SettingsStore settings, ServerOptions options)
    {
        AudioConfig saved = AudioConfig.FromJson(settings.Audio);
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
    /// internal clock if the device can't be opened. Any other client is detached first. A stream that stays open
    /// (JACK's) takes the client in; any other opens for it.</summary>
    public async Task Attach(IAudioClient newClient)
    {
        await gate.WaitAsync();
        try
        {
            await AttachLocked(newClient);
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
    }

    private async Task AttachLocked(IAudioClient newClient)
    {
        if (client != null)
            await DetachLocked();
        client = newClient;
        if (stream != null && stream.Resident && stream.Driver == config.Driver)
            stream.SetClient(newClient);
        else
        {
            await CloseStreamLocked();
            await OpenLocked();
        }
    }

    public async Task Detach(IAudioClient oldClient)
    {
        await gate.WaitAsync();
        try
        {
            if (client != oldClient)
                return;
            await DetachLocked();
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
    }

    // The client stops running: a resident stream carries on in silence; any other closes.
    private async Task DetachLocked()
    {
        client = null;
        if (stream != null && stream.Resident)
            stream.SetClient(null);
        else
            await CloseStreamLocked();
    }

    /// <summary>The running client's latency changed (a rebuild): drivers that report it pass it on.</summary>
    public void LatencyChanged(IAudioClient changed)
    {
        if (client == changed)
            stream?.LatencyChanged(changed.Latency);
    }

    // Opens the configured device for the client; with no client, only a driver that keeps its stream open does.
    private async Task OpenLocked()
    {
        error = null;
        AudioDriver? driver = Drivers.FirstOrDefault(d => string.Equals(d.DriverName, config.Driver, StringComparison.OrdinalIgnoreCase));
        if (client == null)
        {
            if (driver?.KeepsOpen != true)
                return;
            try
            {
                stream = await driver.Open(config, null);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                log.Write(MessageType.Info, "audio", $"Couldn't open {Describe(config)}: {ex.Message}");
                return;
            }
        }
        else
        {
            try
            {
                stream = await Driver(config.Driver).Open(config, client);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                log.Write(MessageType.Warning, "audio", $"Couldn't open {Describe(config)}, so running on the internal clock: {ex.Message}");
                stream = await clock.Open(config, client);
            }
        }
        stream.Failed += OnFailed;
        stream.Changed += OnChanged;
    }

    private async Task CloseStreamLocked()
    {
        AudioStream? s = stream;
        stream = null;
        if (s != null)
        {
            s.Failed -= OnFailed;
            s.Changed -= OnChanged;
            await s.Close();
        }
    }

    // The device stopped on its own (unplugged, say). With a client running, the configuration goes back to no device
    // (the internal clock), at the same rate, and is saved, so the client carries on and the editor shows None rather
    // than a device that's gone; choosing the device again brings it back. With no client, the stream just closes.
    private void OnFailed(AudioStream failed, string message) => _ = Task.Run(async () =>
    {
        bool reset = false;
        await gate.WaitAsync();
        try
        {
            if (stream != failed)
                return;
            if (client == null)
            {
                log.Write(MessageType.Warning, "audio", message);
                error = message;
                await CloseStreamLocked();
                return;
            }
            log.Write(MessageType.Error, "audio", $"{message} Switched to no audio device (the internal clock); the simulation carries on.");
            failed.Failed -= OnFailed;
            failed.Changed -= OnChanged;
            stream = null;
            try { await failed.Close(); }
            catch (Exception ex) { log.Write(MessageType.Warning, "audio", $"Closing the failed device: {ex.Message}"); }
            config = LastUsed(settings, ClockDriver.Name) with { SampleRate = (int)failed.Format.SampleRate };
            settings.SetAudio(config.ToJson());
            reset = true;
            stream = await clock.Open(config, client);
            stream.Failed += OnFailed;
            stream.Changed += OnChanged;
            error = message;
        }
        catch (Exception ex)
        {
            log.Write(MessageType.Error, "audio", $"Couldn't recover from the device failing: {ex.Message}");
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
        if (reset)
            hub.Broadcast("settings.changed", settings.Snapshot());
    });

    private void OnChanged(AudioStream changed) => BroadcastState();

    // ---- State ----

    public JsonObject StateJson()
    {
        AudioStream? s = stream;
        var json = new JsonObject { ["open"] = s != null };
        if (s != null)
        {
            json["driver"] = s.Driver;
            json["device"] = s.Device;
            json["sampleRate"] = s.Format.SampleRate;
            json["bufferSize"] = s.BufferSize;
            json["inputs"] = Json.Strings(s.Format.Inputs);
            json["outputs"] = Json.Strings(s.Format.Outputs);
            json["inputLatency"] = s.InputLatency;
            json["outputLatency"] = s.OutputLatency;
            if (s.Connections() is JsonArray connections)
                json["connections"] = connections;
        }
        else
        {
            json["driver"] = config.Driver;
            json["device"] = config.Device;
        }
        // Whether a simulation or the test is running on it (a resident stream stays open without).
        json["running"] = client != null;
        // Running on the clock although a device was configured.
        json["fallback"] = s is not null && s.Driver != config.Driver;
        if (error != null)
            json["error"] = error;
        return json;
    }

    private void BroadcastState() => hub.Broadcast("audio.state", StateJson());

    private static string Describe(AudioConfig c) => c.Device != null ? $"the {c.Driver} device '{c.Device}'" : $"the first {c.Driver} device";

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
        JsonObject json = config.ToJson();
        json["state"] = StateJson();
        json["byDriver"] = settings.AudioByDriver;
        return Task.FromResult<JsonNode?>(json);
    }

    /// <summary>The configuration last used with a driver, or that driver's defaults.</summary>
    private static AudioConfig LastUsed(SettingsStore settings, string driver) =>
        settings.AudioFor(driver) is JsonObject last ? AudioConfig.FromJson(last) with { Driver = driver } : new AudioConfig { Driver = driver };

    /// <summary><c>audio.setConfig</c>: checks the configuration against the device, saves it, and moves a running
    /// client to it. Fields left out keep their values, except that a new driver starts from its defaults.</summary>
    public async Task<JsonNode?> SetConfig(Session session, Args args)
    {
        await gate.WaitAsync();
        try
        {
            AudioConfig next = Merge(config, args);
            AudioDriver driver = Driver(next.Driver);
            // A remembered device that's gone (uninstalled) gives way to the driver's first, with its default channels.
            if (!args.Has("device") && next.Device != null
                && !(await driver.Devices()).Contains(next.Device, StringComparer.OrdinalIgnoreCase))
                next = next with { Device = null, Inputs = null, Outputs = null };
            string device = next.Device ?? (await driver.Devices()).FirstOrDefault()
                ?? throw new ApiException(ErrorCodes.NotFound, $"There are no {driver.DriverName} devices.");
            next = next with { Driver = driver.DriverName, Device = device };
            if (client == null)
            {
                config = next.Checked(await driver.Info(device));
                // A stream that stays open (JACK's) reopens with the new settings, or closes for another driver.
                await CloseStreamLocked();
                await OpenLocked();
            }
            else
            {
                AudioDeviceInfo? info = null;
                try
                {
                    // The running device, or one the running one doesn't stop us looking at: check before closing it.
                    info = await driver.Info(device);
                    next = next.Checked(info);
                }
                catch (ApiException ex) when (ex.Code == ErrorCodes.AudioBusy)
                {
                    // Another ASIO driver is running: it has to close before this one can be looked at.
                }
                AudioConfig previous = config;
                await CloseStreamLocked();
                try
                {
                    if (info == null)
                        next = next.Checked(await driver.Info(device));
                    config = next;
                }
                catch
                {
                    config = previous;
                    await OpenLocked();
                    BroadcastState();
                    throw;
                }
                await OpenLocked();
            }
            settings.SetAudio(config.ToJson());
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
        hub.Broadcast("settings.changed", settings.Snapshot());
        JsonObject result = config.ToJson();
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
        AudioDriver driver = Driver(args.OptionalString("driver") ?? config.Driver);
        string device = args.OptionalString("device") ?? stream?.Device ?? config.Device ?? (await driver.Devices()).FirstOrDefault()
            ?? throw new ApiException(ErrorCodes.NotFound, $"There are no {driver.DriverName} devices.");
        await driver.ShowControlPanel(device);
        return null;
    }

    /// <summary><c>audio.test</c>: loops the sum of the inputs to every output, streaming it to the scope.</summary>
    public async Task<JsonNode?> Test(Session session, Args args)
    {
        bool enabled = args.Bool("enabled", true);
        if (enabled)
        {
            AudioTest newTest;
            await gate.WaitAsync();
            try
            {
                if (client is AudioTest)
                    return null;
                if (client != null)
                    throw new ApiException(ErrorCodes.AudioBusy, "A simulation is running on the audio device.");
                newTest = new AudioTest(hub);
                await AttachLocked(newTest);
            }
            finally
            {
                gate.Release();
            }
            newTest.Start();
            BroadcastState();
        }
        else
            await StopTest();
        return null;
    }

    public async Task StopTest()
    {
        if (client is AudioTest running)
        {
            running.Stop();
            await Detach(running);
        }
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            if (client is AudioTest running)
                running.Stop();
            client = null;
            CloseStreamLocked().Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release();
        }
        (asio as IDisposable)?.Dispose();
    }
}
