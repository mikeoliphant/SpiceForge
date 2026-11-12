using System.Text.Json.Nodes;
using JackSharp;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>
/// The JACK server, through JackSharpCore: Windows (JACK2), Linux (JACK2, or PipeWire's JACK) and macOS. Its one device
/// is the running JACK server. The channels a configuration chooses are other clients' ports, by full name
/// (<c>system:capture_1</c>, <c>system:playback_1</c>): SpiceForge registers a port of its own for each, and connects
/// it there. The rate and buffer size are the server's; SpiceForge reports them, and never starts a server or changes
/// its settings, which are the user's.
/// <para>The client stays in the graph while JACK is the configured driver, playing silence when nothing runs, so
/// connections made to its ports in a patchbay last; simulations and the test swap in and out of it. Changes to its
/// connections, and ports coming and going, are reported as they happen, and the simulation's latency is reported to
/// JACK, so programs downstream can compensate.</para>
/// </summary>
public sealed class JackDriver : AudioDriver
{
    public const string Name = "JACK";
    public const string DeviceName = "JACK server";
    /// <summary>The client's name in the JACK graph, which patchbays show.</summary>
    public const string ClientName = "SpiceForge";

    private readonly object sync = new();
    private JackStream? open;

    /// <summary>Whether the JACK client library is installed (a server may still not be running).</summary>
    public static bool IsAvailable
    {
        get
        {
            try { return JackLibrary.IsAvailable; }
            catch (Exception) { return false; }
        }
    }

    public override string DriverName => Name;

    public override bool KeepsOpen => true;

    public override Task<IReadOnlyList<string>> Devices() =>
        Task.FromResult<IReadOnlyList<string>>(IsAvailable ? new[] { DeviceName } : Array.Empty<string>());

    public override Task<AudioDeviceInfo> Info(string device) => Task.Run(() =>
    {
        CheckDevice(device);
        JackStream? stream;
        lock (sync)
            stream = open;
        if (stream != null)
            return InfoOf(stream.Processor);
        using var query = OpenClient($"{ClientName}-query", () => new Controller($"{ClientName}-query"));
        return InfoOf(query);
    });

    public override Task<AudioStream> Open(AudioConfig config, IAudioClient? client) => Task.Run<AudioStream>(() =>
    {
        CheckDevice(config.Device ?? DeviceName);
        lock (sync)
            if (open != null)
                throw new ApiException(ErrorCodes.AudioBusy, "SpiceForge's JACK client is already open.");
        Processor processor = OpenClient(ClientName, () => new Processor(ClientName));
        try
        {
            AudioDeviceInfo info = InfoOf(processor);
            config = config.Checked(info);
            var (inputs, outputs) = config.Channels(info);
            var stream = new JackStream(this, processor, inputs.Select(i => info.Inputs[i]).ToArray(), outputs.Select(o => info.Outputs[o]).ToArray(), client);
            lock (sync)
                open = stream;
            return stream;
        }
        catch
        {
            processor.Dispose();
            throw;
        }
    });

    private void Closed(JackStream stream)
    {
        lock (sync)
            if (open == stream)
                open = null;
    }

    private static void CheckDevice(string device)
    {
        if (!IsAvailable)
            throw new ApiException(ErrorCodes.AudioUnavailable, "JACK isn't installed: its client library (libjack) wasn't found.");
        if (!string.Equals(device, DeviceName, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(ErrorCodes.NotFound, $"The JACK driver's device is '{DeviceName}', not '{device}'.");
    }

    private static T OpenClient<T>(string name, Func<T> open) where T : Client
    {
        try
        {
            return open();
        }
        catch (JackException ex)
        {
            throw new ApiException(ErrorCodes.AudioUnavailable, ex.NoServer ? "No JACK server is running: start one (with qjackctl, say)." : ex.Message);
        }
    }

    /// <summary>The server's ports SpiceForge can connect to, physical ones first, its own left out; its rate and buffer
    /// size, as the only ones offered.</summary>
    private static AudioDeviceInfo InfoOf(Client client)
    {
        static IReadOnlyList<string> Ports(Client client, Direction direction) =>
            client.GetPorts(type: PortType.Audio, direction: direction)
                .Where(p => p.ClientName != ClientName && !p.ClientName.StartsWith(ClientName + "-", StringComparison.Ordinal))
                .OrderBy(p => p.IsPhysical ? 0 : 1)
                .Select(p => p.FullName)
                .ToList();
        // Sources (capture ports, other programs' outputs) feed SpiceForge's inputs; destinations take its outputs.
        return new AudioDeviceInfo(
            DeviceName,
            Ports(client, Direction.Out),
            Ports(client, Direction.In),
            new[] { client.SampleRate },
            new[] { client.BufferSize },
            client.SampleRate,
            client.BufferSize,
            ControlPanel: false);
    }

    private sealed class JackStream : AudioStream
    {
        private readonly JackDriver owner;
        private volatile IAudioClient? client;
        private readonly Timer changes;
        private readonly string[] sources, destinations;
        private readonly AudioPort[] inputs, outputs;
        private volatile AudioBlock block = new();
        private volatile AudioFormat format;
        private int xruns;
        private int restarting;
        private bool closed;

        public JackStream(JackDriver owner, Processor processor, string[] sources, string[] destinations, IAudioClient? client)
        {
            this.owner = owner;
            Processor = processor;
            this.client = client;
            this.sources = sources;
            this.destinations = destinations;
            inputs = sources.Select((_, i) => processor.RegisterAudioPort($"in_{i + 1}", Direction.In)).ToArray();
            outputs = destinations.Select((_, i) => processor.RegisterAudioPort($"out_{i + 1}", Direction.Out)).ToArray();
            processor.Xrun += (_, _) => Interlocked.Increment(ref xruns);
            processor.Shutdown += (_, e) =>
                OnFailed(e.Reason.Length > 0 ? $"The JACK server shut down: {e.Reason}" : "The JACK server shut down.");
            processor.BufferSizeChanged += (_, _) => Restart();
            processor.SampleRateChanged += (_, _) => Restart();
            // The graph changing (connections to SpiceForge's ports, from a patchbay, say; ports coming and going) is
            // reported, a moment after the last change of a burst.
            changes = new Timer(_ => OnChanged());
            processor.PortsConnected += (_, e) =>
            {
                if (Mine(e.Source) || Mine(e.Destination))
                    changes.Change(100, Timeout.Infinite);
            };
            processor.PortRegistered += (_, _) => changes.Change(100, Timeout.Infinite);
            processor.AddedLatency = client?.Latency ?? 0;
            format = Prepare();
            processor.Process = Process;
            processor.Activate();
            Connect();
        }

        public override bool Resident => true;

        private bool Mine(string port) => port.StartsWith(Processor.Name + ":", StringComparison.Ordinal);

        public override void SetClient(IAudioClient? newClient)
        {
            lock (this)
            {
                // The new client is prepared before the real-time thread can see it; the old one may finish a block.
                newClient?.Prepare(format);
                Interlocked.Exchange(ref xruns, 0);   // Dropouts while nothing ran aren't the new client's.
                client = newClient;
                SetLatency(newClient?.Latency ?? 0);
            }
        }

        public override void LatencyChanged(int samples) => SetLatency(samples);

        private void SetLatency(int samples)
        {
            try
            {
                if (!closed && Processor.AddedLatency != samples)
                    Processor.AddedLatency = samples;
            }
            catch (Exception)
            {
                // The server has gone: the stream is about to fail.
            }
        }

        public override JsonArray? Connections()
        {
            var ports = new JsonArray();
            void Add(AudioPort port, string channel)
            {
                IReadOnlyList<string> connected;
                try { connected = port.IsRegistered ? port.GetConnections() : Array.Empty<string>(); }
                catch (Exception) { connected = Array.Empty<string>(); }
                ports.Add(new JsonObject { ["port"] = port.FullName, ["channel"] = channel, ["connected"] = Json.Strings(connected) });
            }
            try
            {
                for (int i = 0; i < inputs.Length; i++)
                    Add(inputs[i], sources[i]);
                for (int o = 0; o < outputs.Length; o++)
                    Add(outputs[o], destinations[o]);
            }
            catch (Exception)
            {
                return new JsonArray();
            }
            return ports;
        }

        public Processor Processor { get; }
        public override string Driver => Name;
        public override string Device => DeviceName;
        public override AudioFormat Format => format;
        public override int BufferSize => format.MaxFrames;

        // What the connected ports report: how long ago what arrives was captured, how long until what leaves is heard.
        public override int InputLatency => Latency(inputs, LatencyMode.Capture);
        public override int OutputLatency => Latency(outputs, LatencyMode.Playback);

        private static int Latency(AudioPort[] ports, LatencyMode mode)
        {
            try { return ports.Length == 0 ? 0 : (int)ports.Max(p => p.GetLatency(mode).Max); }
            catch (Exception) { return 0; }
        }

        private AudioFormat Prepare()
        {
            var f = new AudioFormat(Processor.SampleRate, Processor.BufferSize, sources, destinations);
            block = AudioBlock.For(f);
            format = f;
            client?.Prepare(f);
            return f;
        }

        private void Connect()
        {
            for (int i = 0; i < inputs.Length; i++)
                inputs[i].ConnectTo(sources[i]);
            for (int o = 0; o < outputs.Length; o++)
                outputs[o].ConnectTo(destinations[o]);
        }

        // The server's buffer size or rate changed: prepare the client for it while nothing runs (deactivating drops the
        // connections, so they're made again), off JACK's notification thread.
        private void Restart()
        {
            if (Interlocked.Exchange(ref restarting, 1) == 1)
                return;
            Task.Run(() =>
            {
                try
                {
                    lock (this)
                    {
                        if (closed)
                            return;
                        Processor.Deactivate();
                        Prepare();
                        Processor.Activate();
                        Connect();
                    }
                    OnChanged();
                }
                catch (Exception ex)
                {
                    OnFailed($"Couldn't restart after the JACK server's settings changed: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref restarting, 0);
                }
            });
        }

        public override Task Close() => Task.Run(() =>
        {
            lock (this)
            {
                if (closed)
                    return;
                closed = true;
            }
            changes.Dispose();
            Processor.Dispose();
            owner.Closed(this);
        });

        // JACK's real-time thread.
        private void Process(ProcessContext context)
        {
            AudioBlock b = block;
            int frames = context.Frames;
            IAudioClient? c = client;
            if (c == null || frames > b.Capacity || b.Inputs.Length != inputs.Length || b.Outputs.Length != outputs.Length)
            {
                // Nothing running; or between a change of buffer size and the restart that prepares for it.
                foreach (AudioPort port in outputs)
                    port.GetBuffer(context).Clear();
                return;
            }
            for (int i = 0; i < inputs.Length; i++)
                inputs[i].GetBuffer(context).CopyTo(b.Inputs[i]);
            b.Frames = frames;
            b.Skipped = 0;
            b.Xruns = Interlocked.Exchange(ref xruns, 0);
            try
            {
                c.Process(b);
            }
            catch (Exception)
            {
                // The client reports its own failures; never let one unwind into JACK.
                foreach (AudioPort port in outputs)
                    port.GetBuffer(context).Clear();
                return;
            }
            for (int o = 0; o < outputs.Length; o++)
                b.Outputs[o].AsSpan(0, frames).CopyTo(outputs[o].GetBuffer(context));
        }
    }
}
