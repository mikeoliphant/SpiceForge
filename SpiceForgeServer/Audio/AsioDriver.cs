using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.Wave;
using NAudio.Wave.Asio;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>
/// ASIO devices, through NAudio's <see cref="AsioDevice"/> in duplex mode: one callback per buffer reads the inputs and
/// writes the outputs, converted to and from the driver's native format by NAudio. Every call into a driver is made on
/// one STA thread (<see cref="StaWorker"/>). One device is open at a time: ASIO drivers generally allow a single client,
/// and loading a second driver while one runs can disturb both.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AsioDriver : AudioDriver, IDisposable
{
    public const string Name = "ASIO";

    // The rates offered when a driver supports them; its current rate is always offered too.
    private static readonly int[] StandardRates = { 44100, 48000, 88200, 96000, 176400, 192000 };

    private readonly StaWorker worker = new("ASIO");
    private AsioStream? open;   // Touched only on the worker.
    // Devices left as they were when they couldn't restart (see AsioStream.Close), held so their callbacks stay alive
    // for a driver that might still call them. Touched only on the worker.
    private readonly List<AsioDevice> abandoned = new();

    public override string DriverName => Name;

    public override Task<IReadOnlyList<string>> Devices() => worker.Run(() => (IReadOnlyList<string>)AsioDevice.GetDriverNames());

    public override Task<AudioDeviceInfo> Info(string device) => worker.Run(() =>
    {
        if (open != null)
            return SameName(open.Device, device) ? open.Info
                : throw new ApiException(ErrorCodes.AudioBusy, $"The ASIO device '{open.Device}' is open; close it to look at another.");
        using AsioDevice d = OpenDevice(device);
        return InfoOf(d, device);
    });

    public override Task<AudioStream> Open(AudioConfig config, IAudioClient? client) => worker.Run<AudioStream>(() =>
    {
        ArgumentNullException.ThrowIfNull(client);
        if (open != null)
            throw new ApiException(ErrorCodes.AudioBusy, $"The ASIO device '{open.Device}' is already open.");
        string name = config.Device ?? AsioDevice.GetDriverNames().FirstOrDefault()
            ?? throw new ApiException(ErrorCodes.AudioUnavailable, "There are no ASIO drivers installed.");
        AsioDevice device = OpenDevice(name);
        try
        {
            AudioDeviceInfo info = InfoOf(device, name);
            config = config.Checked(info);
            var stream = new AsioStream(this, device, info, config, client);
            open = stream;
            return stream;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    });

    public override Task ShowControlPanel(string device) => worker.Run(() =>
    {
        if (open != null)
        {
            if (!SameName(open.Device, device))
                throw new ApiException(ErrorCodes.AudioBusy, $"The ASIO device '{open.Device}' is open; close it to configure another.");
            open.ShowControlPanel();
            return;
        }
        using AsioDevice d = OpenDevice(device);
        d.ShowControlPanel();
    });

    public void Dispose()
    {
        if (open != null)
            open.Close().Wait(TimeSpan.FromSeconds(5));
        worker.Dispose();
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static AsioDevice OpenDevice(string name)
    {
        if (!AsioDevice.GetDriverNames().Any(n => SameName(n, name)))
            throw new ApiException(ErrorCodes.NotFound, $"There's no ASIO driver '{name}'.");
        try
        {
            return AsioDevice.Open(AsioDevice.GetDriverNames().First(n => SameName(n, name)));
        }
        catch (Exception ex)
        {
            throw new ApiException(ErrorCodes.AudioUnavailable, $"Couldn't open the ASIO driver '{name}': {ex.Message.Trim()}");
        }
    }

    private static AudioDeviceInfo InfoOf(AsioDevice device, string name)
    {
        AsioDriverCapability caps = device.Capabilities;
        // Some drivers (Realtek's) report a rate of 0 until they're started: then 48 kHz, or the first they take, is
        // the default.
        int current = device.CurrentSampleRate;
        var rates = StandardRates.Where(r => r == current || Supports(device, r)).ToList();
        if (current > 0 && !rates.Contains(current))
            rates.Add(current);
        rates.Sort();
        if (current <= 0)
            current = rates.Contains(ClockDriver.DefaultSampleRate) ? ClockDriver.DefaultSampleRate : rates.FirstOrDefault();
        return new AudioDeviceInfo(
            name,
            ChannelNames(caps.InputChannelInfos, caps.NbInputChannels, "In"),
            ChannelNames(caps.OutputChannelInfos, caps.NbOutputChannels, "Out"),
            rates,
            BufferSizes(caps.BufferMinSize, caps.BufferMaxSize, caps.BufferPreferredSize, caps.BufferGranularity),
            current,
            caps.BufferPreferredSize,
            ControlPanel: true);
    }

    private static bool Supports(AsioDevice device, int rate)
    {
        try { return device.IsSampleRateSupported(rate); }
        catch (Exception) { return false; }
    }

    /// <summary>The driver's channel names, made unique (and given a name where the driver has none), since a
    /// configuration names its channels.</summary>
    private static List<string> ChannelNames(AsioChannelInfo[]? infos, int count, string prefix)
    {
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string name = infos != null && i < infos.Length && !string.IsNullOrWhiteSpace(infos[i].name) ? infos[i].name.Trim() : $"{prefix} {i + 1}";
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                name = $"{name} ({i + 1})";
            names.Add(name);
        }
        return names;
    }

    /// <summary>The buffer sizes a driver takes, from ASIO's minimum, maximum, preferred size and granularity: a
    /// granularity of -1 means powers of two, 0 means only the preferred size.</summary>
    public static IReadOnlyList<int> BufferSizes(int min, int max, int preferred, int granularity)
    {
        var sizes = new SortedSet<int> { preferred };
        if (min <= 0 || max < min)
            return sizes.ToList();
        if (granularity == -1)
        {
            for (int size = 1; size <= max; size *= 2)
                if (size >= min)
                    sizes.Add(size);
        }
        else if (granularity > 0)
        {
            // A fine granularity would give hundreds of sizes: offer the powers of two among them, and the ends.
            bool fine = (max - min) / granularity > 32;
            for (int size = min; size <= max; size += granularity)
                if (!fine || (size & (size - 1)) == 0)
                    sizes.Add(size);
            sizes.Add(min);
            sizes.Add(max);
        }
        return sizes.ToList();
    }

    private sealed class AsioStream : AudioStream
    {
        private readonly AsioDriver owner;
        private readonly AsioDevice device;
        private readonly IAudioClient client;
        private readonly string[] inputNames, outputNames;
        private volatile AudioBlock block = new();
        private volatile AudioFormat format;
        private long budget;   // Stopwatch ticks a callback may take before it counts as an overrun.
        private int xruns;
        private bool closing;   // Touched only on the worker.
        private bool broken;    // A reset failed partway: the device mustn't be called again. Only on the worker.

        public AsioStream(AsioDriver owner, AsioDevice device, AudioDeviceInfo info, AudioConfig config, IAudioClient client)
        {
            this.owner = owner;
            this.device = device;
            this.client = client;
            Info = info;
            Device = info.Name;
            var (inputs, outputs) = config.Channels(info);
            inputNames = inputs.Select(i => info.Inputs[i]).ToArray();
            outputNames = outputs.Select(o => info.Outputs[o]).ToArray();
            if (info.Outputs.Count == 0)
                throw new ApiException(ErrorCodes.AudioUnavailable, $"The ASIO device '{Device}' has no outputs.");
            device.InitDuplex(new AsioDuplexOptions
            {
                InputChannels = inputs.Length > 0 ? inputs : null,
                // NAudio needs an output to run duplex: with none chosen, open the first and leave it silent.
                OutputChannels = outputs.Length > 0 ? outputs : new[] { 0 },
                SampleRate = config.SampleRate ?? (device.CurrentSampleRate > 0 ? null : info.DefaultSampleRate),
                BufferSize = config.BufferSize,
                Processor = Process,
            });
            device.DriverResetRequest += (_, _) => owner.worker.Post(Reset, ex =>
            {
                broken = true;
                OnFailed($"The ASIO driver couldn't restart: {ex.Message}");
            });
            device.ResyncOccurred += (_, _) => Interlocked.Increment(ref xruns);
            device.LatenciesChanged += (_, _) => OnChanged();
            device.Stopped += (_, e) => owner.worker.Post(() =>
            {
                // Our own stops (closing, or a reset that has restarted it by now) aren't failures.
                if (!closing && device.State != AsioDeviceState.Running)
                    OnFailed(e.Exception != null ? $"The ASIO driver stopped: {e.Exception.Message}" : "The ASIO driver stopped.");
            });
            format = Prepare();
            device.Start();
        }

        public AudioDeviceInfo Info { get; }
        public override string Driver => Name;
        public override string Device { get; }
        public override AudioFormat Format => format;
        public override int BufferSize => format.MaxFrames;
        public override int InputLatency => device.InputLatencySamples;
        public override int OutputLatency => device.OutputLatencySamples;

        private AudioFormat Prepare()
        {
            var f = new AudioFormat(device.CurrentSampleRate, device.FramesPerBuffer, inputNames, outputNames);
            block = AudioBlock.For(f);
            budget = (long)(Stopwatch.Frequency * f.MaxFrames / f.SampleRate);
            format = f;
            client.Prepare(f);
            return f;
        }

        public void ShowControlPanel() => device.ShowControlPanel();

        // The driver's settings changed (its control panel, or a clock change): NAudio's recovery, then tell the client.
        private void Reset()
        {
            if (closing)
                return;
            device.Stop();
            device.Reinitialize();
            Prepare();
            device.Start();
            OnChanged();
        }

        // A device whose reset failed partway (its hardware unplugged: Reinitialize stopped it, then failed with
        // ASE_NoClock) is left alone rather than disposed: disposing it then corrupted the process's heap and killed
        // the server. It's kept, never called again, and the driver can be opened afresh.
        public override Task Close() => owner.worker.Run(() =>
        {
            if (closing)
                return;
            closing = true;
            if (broken)
            {
                owner.abandoned.Add(device);
                owner.open = null;
                return;
            }
            try
            {
                if (device.State == AsioDeviceState.Running)
                    device.Stop();
            }
            finally
            {
                device.Dispose();
                owner.open = null;
            }
        });

        // The audio thread.
        private void Process(in AsioProcessBuffers buffers)
        {
            AudioBlock b = block;
            int frames = buffers.Frames;
            if (frames > b.Capacity || b.Inputs.Length != buffers.InputChannelCount)
                return;   // Not the block Prepare made for this configuration: NAudio leaves the outputs silent.
            for (int i = 0; i < b.Inputs.Length; i++)
                buffers.GetInput(i).CopyTo(b.Inputs[i]);
            b.Frames = frames;
            b.Skipped = 0;
            b.Xruns = Interlocked.Exchange(ref xruns, 0);
            long began = Stopwatch.GetTimestamp();
            try
            {
                client.Process(b);
            }
            catch (Exception)
            {
                // The client reports its own failures; never let one unwind into the driver.
                return;
            }
            if (Stopwatch.GetTimestamp() - began > budget)
                Interlocked.Increment(ref xruns);
            for (int o = 0; o < b.Outputs.Length; o++)
                b.Outputs[o].AsSpan(0, frames).CopyTo(buffers.GetOutput(o));
        }
    }
}
