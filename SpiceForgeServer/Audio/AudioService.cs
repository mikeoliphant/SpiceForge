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
    }

    /// <summary>The drivers this platform has, the internal clock first.</summary>
    public IReadOnlyList<AudioDriver> Drivers => asio != null ? new[] { clock, asio } : new AudioDriver[] { clock };

    public static IReadOnlyList<string> DriverNames => OperatingSystem.IsWindows() ? new[] { ClockDriver.Name, AsioDriver.Name } : new[] { ClockDriver.Name };

    public AudioDriver Driver(string name) =>
        Drivers.FirstOrDefault(d => string.Equals(d.DriverName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ApiException(ErrorCodes.InvalidValue, $"There's no audio driver '{name}'. The drivers are: {string.Join(", ", Drivers.Select(d => d.DriverName))}.");

    /// <summary>The client running on the device, if any.</summary>
    public IAudioClient? Client => client;

    public AudioConfig Config => config;

    /// <summary>The configuration a run starts with: the saved one, with whatever the command line gives on top. A
    /// different driver on the command line starts from that driver's defaults.</summary>
    public static AudioConfig Startup(AudioConfig saved, ServerOptions options)
    {
        AudioConfig c = saved;
        if (options.AudioDriver != null && !string.Equals(options.AudioDriver, saved.Driver, StringComparison.OrdinalIgnoreCase))
            c = new AudioConfig { Driver = options.AudioDriver };
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

    /// <summary>Opens the device for <paramref name="newClient"/>, prepares it and starts it, falling back to the
    /// internal clock if the device can't be opened. Any other client is detached first.</summary>
    public async Task Attach(IAudioClient newClient)
    {
        await gate.WaitAsync();
        try
        {
            if (client != null)
                await CloseLocked();
            client = newClient;
            await OpenLocked();
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
    }

    public async Task Detach(IAudioClient oldClient)
    {
        await gate.WaitAsync();
        try
        {
            if (client != oldClient)
                return;
            await CloseLocked();
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
    }

    private async Task OpenLocked()
    {
        error = null;
        try
        {
            stream = await Driver(config.Driver).Open(config, client!);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Write(MessageType.Warning, "audio", $"Couldn't open {Describe(config)}, so running on the internal clock: {ex.Message}");
            stream = await clock.Open(config, client!);
        }
        stream.Failed += OnFailed;
        stream.Changed += OnChanged;
    }

    private async Task CloseLocked()
    {
        AudioStream? s = stream;
        stream = null;
        client = null;
        if (s != null)
        {
            s.Failed -= OnFailed;
            s.Changed -= OnChanged;
            await s.Close();
        }
    }

    // The device stopped on its own: carry on on the internal clock.
    private void OnFailed(AudioStream failed, string message) => _ = Task.Run(async () =>
    {
        await gate.WaitAsync();
        try
        {
            if (stream != failed || client == null)
                return;
            log.Write(MessageType.Error, "audio", $"{message} Running on the internal clock.");
            failed.Failed -= OnFailed;
            failed.Changed -= OnChanged;
            stream = null;
            try { await failed.Close(); }
            catch (Exception ex) { log.Write(MessageType.Warning, "audio", $"Closing the failed device: {ex.Message}"); }
            stream = await clock.Open(config with { SampleRate = (int)failed.Format.SampleRate }, client);
            stream.Failed += OnFailed;
            error = message;
        }
        finally
        {
            gate.Release();
        }
        BroadcastState();
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
        }
        else
        {
            json["driver"] = config.Driver;
            json["device"] = config.Device;
        }
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
        return Task.FromResult<JsonNode?>(json);
    }

    /// <summary><c>audio.setConfig</c>: checks the configuration against the device, saves it, and moves a running
    /// client to it. Fields left out keep their values, except that a new driver starts from its defaults.</summary>
    public async Task<JsonNode?> SetConfig(Session session, Args args)
    {
        await gate.WaitAsync();
        try
        {
            AudioConfig next = Merge(config, args);
            AudioDriver driver = Driver(next.Driver);
            string device = next.Device ?? (await driver.Devices()).FirstOrDefault()
                ?? throw new ApiException(ErrorCodes.NotFound, $"There are no {driver.DriverName} devices.");
            next = next with { Driver = driver.DriverName, Device = device };
            if (client == null)
                config = next.Checked(await driver.Info(device));
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
                IAudioClient running = client;
                AudioConfig previous = config;
                await CloseLocked();
                client = running;
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
        return result;
    }

    private static AudioConfig Merge(AudioConfig current, Args args)
    {
        AudioConfig c = current;
        if (args.OptionalString("driver") is string driver && !string.Equals(driver, current.Driver, StringComparison.OrdinalIgnoreCase))
            c = new AudioConfig { Driver = driver };
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
                client = newTest;
                await OpenLocked();
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
            CloseLocked().Wait(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release();
        }
        (asio as IDisposable)?.Dispose();
    }
}
