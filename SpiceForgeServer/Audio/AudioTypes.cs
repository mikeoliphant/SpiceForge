using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>What an open stream runs at: its rate, the most frames a callback can carry, and the channels it opened,
/// by name, in the order of <see cref="AudioBlock.Inputs"/> and <see cref="AudioBlock.Outputs"/>.</summary>
public sealed record AudioFormat(double SampleRate, int MaxFrames, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);

/// <summary>
/// One callback's audio. The stream owns it and reuses it every callback, so nothing is allocated on the audio thread:
/// each array is at least <see cref="AudioFormat.MaxFrames"/> long, and only the first <see cref="Frames"/> count.
/// </summary>
public sealed class AudioBlock
{
    public float[][] Inputs = Array.Empty<float[]>();
    public float[][] Outputs = Array.Empty<float[]>();

    /// <summary>The most frames a callback can carry: the length of every channel's array.</summary>
    public int Capacity;

    public int Frames;

    /// <summary>Frames the stream's clock skipped before this block, after falling behind (the internal clock only).</summary>
    public long Skipped;

    /// <summary>Dropouts the stream noticed since the previous block: driver resyncs, and callbacks that overran.</summary>
    public int Xruns;

    public static AudioBlock For(AudioFormat format) => new()
    {
        Inputs = format.Inputs.Select(_ => new float[format.MaxFrames]).ToArray(),
        Outputs = format.Outputs.Select(_ => new float[format.MaxFrames]).ToArray(),
        Capacity = format.MaxFrames,
    };
}

/// <summary>What runs on an audio stream: a simulation, or the audio test.</summary>
public interface IAudioClient
{
    /// <summary>Called while the stream is stopped, before it starts and again after its format changes, so the client
    /// can size its buffers (and rebuild, if the rate changed).</summary>
    void Prepare(AudioFormat format);

    /// <summary>Called on the audio thread for every block. It must fill every output, and must not allocate, lock or
    /// block.</summary>
    void Process(AudioBlock block);
}

/// <summary>What a device offers, for <c>audio.deviceInfo</c> and for checking a configuration.</summary>
public sealed record AudioDeviceInfo(
    string Name,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<int> SampleRates,
    IReadOnlyList<int> BufferSizes,
    int DefaultSampleRate,
    int DefaultBufferSize,
    bool ControlPanel)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["inputs"] = Json.Strings(Inputs),
        ["outputs"] = Json.Strings(Outputs),
        ["sampleRates"] = Json.Array(SampleRates.Select(r => (JsonNode?)r)),
        ["bufferSizes"] = Json.Array(BufferSizes.Select(b => (JsonNode?)b)),
        ["defaultSampleRate"] = DefaultSampleRate,
        ["defaultBufferSize"] = DefaultBufferSize,
        ["controlPanel"] = ControlPanel,
    };
}

/// <summary>
/// The audio configuration (WebSocketApi.md, Audio): a driver, one of its devices, and the channels, rate and buffer
/// size to open it with. Channels are named, as LiveSPICE stores them. A null field means the device's default: its
/// first device; its first input and first two outputs; its current rate; its preferred buffer size.
/// </summary>
public sealed record AudioConfig
{
    public string Driver { get; init; } = ClockDriver.Name;
    public string? Device { get; init; }
    public IReadOnlyList<string>? Inputs { get; init; }
    public IReadOnlyList<string>? Outputs { get; init; }
    public int? SampleRate { get; init; }
    public int? BufferSize { get; init; }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["driver"] = Driver, ["device"] = Device };
        json["inputs"] = Inputs == null ? null : Json.Strings(Inputs);
        json["outputs"] = Outputs == null ? null : Json.Strings(Outputs);
        json["sampleRate"] = SampleRate;
        json["bufferSize"] = BufferSize;
        return json;
    }

    public static AudioConfig FromJson(JsonObject? json)
    {
        if (json == null)
            return new AudioConfig();
        var args = new Args(json);
        return new AudioConfig
        {
            Driver = args.OptionalString("driver") ?? ClockDriver.Name,
            Device = args.OptionalString("device"),
            Inputs = args.Has("inputs") ? args.Strings("inputs") : null,
            Outputs = args.Has("outputs") ? args.Strings("outputs") : null,
            SampleRate = args.Has("sampleRate") ? args.Int("sampleRate", 0) : null,
            BufferSize = args.Has("bufferSize") ? args.Int("bufferSize", 0) : null,
        };
    }

    /// <summary>The device's channels this configuration opens, by index into the device's own lists.</summary>
    public (int[] Inputs, int[] Outputs) Channels(AudioDeviceInfo info)
    {
        static int[] Pick(IReadOnlyList<string>? names, IReadOnlyList<string> available, int fallback, string kind)
        {
            if (names == null)
                return Enumerable.Range(0, Math.Min(fallback, available.Count)).ToArray();
            return names.Select(name =>
            {
                int i = available.ToList().FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : throw new ApiException(ErrorCodes.InvalidValue,
                    $"The device has no {kind} channel '{name}'. Its {kind}s are: {List(available)}.", Json.Strings(available));
            }).Distinct().ToArray();
        }
        return (Pick(Inputs, info.Inputs, 1, "input"), Pick(Outputs, info.Outputs, 2, "output"));
    }

    /// <summary>Checks the configuration against what the device offers, and fills in the device's name.</summary>
    public AudioConfig Checked(AudioDeviceInfo info)
    {
        Channels(info);
        if (SampleRate is int rate && !info.SampleRates.Contains(rate))
            throw new ApiException(ErrorCodes.InvalidValue,
                $"The device doesn't run at {rate} Hz. It supports {List(info.SampleRates.Select(r => r.ToString()))} Hz.",
                Json.Array(info.SampleRates.Select(r => (JsonNode?)r)));
        if (BufferSize is int size && !info.BufferSizes.Contains(size))
            throw new ApiException(ErrorCodes.InvalidValue,
                $"The device doesn't take buffers of {size} samples. It supports {List(info.BufferSizes.Select(b => b.ToString()))}.",
                Json.Array(info.BufferSizes.Select(b => (JsonNode?)b)));
        return this with { Device = info.Name };
    }

    private static string List(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "(none)";
}

/// <summary>A running stream on a device. Made by <see cref="AudioDriver.Open"/>, already started.</summary>
public abstract class AudioStream
{
    public abstract string Driver { get; }
    public abstract string Device { get; }
    public abstract AudioFormat Format { get; }
    public abstract int BufferSize { get; }
    public virtual int InputLatency => 0;
    public virtual int OutputLatency => 0;

    /// <summary>Raised, off the audio thread, when the device stops on its own: unplugged, or a driver fault.</summary>
    public event Action<AudioStream, string>? Failed;

    /// <summary>Raised, off the audio thread, after the driver changed the format (the client has been prepared for
    /// the new one) or its latencies.</summary>
    public event Action<AudioStream>? Changed;

    protected void OnFailed(string message) => Failed?.Invoke(this, message);

    protected void OnChanged() => Changed?.Invoke(this);

    /// <summary>Stops the stream and closes the device. No callback runs after it returns.</summary>
    public abstract Task Close();
}

/// <summary>A kind of audio device: the internal clock, or ASIO.</summary>
public abstract class AudioDriver
{
    public abstract string DriverName { get; }

    public abstract Task<IReadOnlyList<string>> Devices();

    /// <summary>What a device offers. For ASIO this loads the driver, unless it's the one already open.</summary>
    public abstract Task<AudioDeviceInfo> Info(string device);

    /// <summary>Opens the configuration's device (null: the first), prepares the client for its format, and starts
    /// it.</summary>
    public abstract Task<AudioStream> Open(AudioConfig config, IAudioClient client);

    public virtual Task ShowControlPanel(string device) =>
        throw new ApiException(ErrorCodes.Unsupported, $"{DriverName} devices have no control panel.");

    /// <summary>The configuration's device, or the driver's first.</summary>
    protected async Task<string> DeviceOf(AudioConfig config)
    {
        if (config.Device != null)
            return config.Device;
        IReadOnlyList<string> devices = await Devices();
        return devices.Count > 0 ? devices[0] : throw new ApiException(ErrorCodes.AudioUnavailable, $"There are no {DriverName} devices.");
    }
}
