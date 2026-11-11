using System;
using System.Diagnostics;
using System.Threading;
using AudioPlugSharp;
using NAudio.Wave;

namespace SpiceForgeHost
{
    /// <summary>
    /// Hosts the plugin without an audio device, for testing: the input comes from a WAV file (or is silent), the
    /// output can go to a WAV file, and blocks are processed on a background thread at real-time pace, so the editor
    /// behaves as it would with a device.
    /// </summary>
    /// <remarks>
    /// Unlike the ASIO host, this doesn't load or save the plugin's state, so a test run starts from a known state
    /// and leaves the user's settings alone.
    /// </remarks>
    sealed class FileAudioHost : IAudioHost, IDisposable
    {
        public double SampleRate { get; }
        public uint MaxAudioBufferSize { get; }
        public uint CurrentAudioBufferSize { get; }
        public EAudioBitsPerSample BitsPerSample => EAudioBitsPerSample.Bits32;
        public double BPM => 120;
        public long CurrentProjectSample { get; private set; }
        public bool IsPlaying => true;

        /// <summary>Raised on the processing thread when the input file has been played (and not looped).</summary>
        public event Action InputFinished;

        readonly SpiceForgePlugin.SpiceForgePlugin plugin;
        readonly float[] input;
        readonly int inputChannels;
        readonly bool loop;
        // Stop at the end of the input, so the recording is exactly as long as the input.
        readonly bool stopAtInputEnd;
        readonly WaveFileWriter writer;
        readonly float[] channelBuffer;
        readonly float[] interleaved;
        int inputPosition;
        bool inputDone;

        Thread thread;
        volatile bool running;

        public FileAudioHost(SpiceForgePlugin.SpiceForgePlugin plugin, HostOptions options)
        {
            this.plugin = plugin;
            loop = options.Loop;
            stopAtInputEnd = options.ExitWhenDone;
            MaxAudioBufferSize = CurrentAudioBufferSize = (uint)options.BufferSize;

            if (options.InputPath != null)
            {
                (input, inputChannels, int rate) = ReadWav(options.InputPath);
                SampleRate = rate;
            }
            else
            {
                SampleRate = options.SampleRate;
            }

            (plugin as IAudioPlugin).Host = this;
            plugin.Initialize();
            foreach (AudioIOPort port in plugin.InputPorts)
                port.ForceBackingBuffer = true;
            foreach (AudioIOPort port in plugin.OutputPorts)
                port.ForceBackingBuffer = true;

            int outputChannels = 0;
            foreach (AudioIOPort port in plugin.OutputPorts)
                outputChannels += (int)port.NumChannels;
            channelBuffer = new float[options.BufferSize];
            interleaved = new float[options.BufferSize * outputChannels];
            if (options.OutputPath != null)
                writer = new WaveFileWriter(options.OutputPath, WaveFormat.CreateIeeeFloatWaveFormat((int)SampleRate, outputChannels));
        }

        static (float[] samples, int channels, int rate) ReadWav(string path)
        {
            using var reader = new AudioFileReader(path);
            var samples = new float[reader.Length / sizeof(float)];
            int total = 0, read;
            while (total < samples.Length && (read = ((ISampleProvider)reader).Read(samples.AsSpan(total))) > 0)
                total += read;
            Array.Resize(ref samples, total);
            return (samples, reader.WaveFormat.Channels, reader.WaveFormat.SampleRate);
        }

        /// <summary>
        /// Starts processing. With <paramref name="waitForBuild"/>, the clock starts once the schematic being loaded
        /// has finished building, so a recording isn't the dry input while the circuit compiles.
        /// </summary>
        public void Start(bool waitForBuild)
        {
            plugin.InitializeProcessing();
            plugin.SetMaxAudioBufferSize(MaxAudioBufferSize, BitsPerSample);

            running = true;
            thread = new Thread(() => Run(waitForBuild)) { IsBackground = true, Name = "SpiceForgeHost audio" };
            thread.Start();
        }

        void Run(bool waitForBuild)
        {
            while (waitForBuild && running && plugin.IsBuilding)
                Thread.Sleep(10);

            var clock = Stopwatch.StartNew();
            long processed = 0;
            while (running)
            {
                // Process every block that's due, then sleep until the next one.
                long due = (long)(clock.Elapsed.TotalSeconds * SampleRate);
                while (running && processed + CurrentAudioBufferSize <= due)
                {
                    ProcessBlock();
                    processed += CurrentAudioBufferSize;
                }
                Thread.Sleep(1);
            }
        }

        void ProcessBlock()
        {
            if (stopAtInputEnd && inputDone)
                return;

            int size = (int)CurrentAudioBufferSize;
            int record = stopAtInputEnd ? Math.Min(size, input.Length / inputChannels - inputPosition) : size;

            int inputCount = 0;
            foreach (AudioIOPort port in plugin.InputPorts)
            {
                port.SetCurrentBufferSize(CurrentAudioBufferSize);
                for (int channel = 0; channel < port.NumChannels; channel++, inputCount++)
                {
                    FillInput(inputCount, size);
                    port.CopyFrom(channelBuffer, channel);
                }
            }
            foreach (AudioIOPort port in plugin.OutputPorts)
                port.SetCurrentBufferSize(CurrentAudioBufferSize);

            plugin.PreProcess();
            plugin.Process();
            plugin.PostProcess();

            AdvanceInput(size);
            CurrentProjectSample += size;

            if (writer != null)
            {
                int outputChannels = interleaved.Length / size;
                int outputCount = 0;
                foreach (AudioIOPort port in plugin.OutputPorts)
                {
                    for (int channel = 0; channel < port.NumChannels; channel++, outputCount++)
                    {
                        port.CopyTo(channelBuffer, channel);
                        for (int n = 0; n < record; n++)
                            interleaved[n * outputChannels + outputCount] = channelBuffer[n];
                    }
                }
                writer.WriteSamples(interleaved, 0, record * outputChannels);
            }
        }

        // Fills channelBuffer with the input for one of the plugin's channels, reusing the file's channels if it has
        // fewer, as the ASIO host does with device inputs.
        void FillInput(int pluginChannel, int size)
        {
            if (input == null || inputDone)
            {
                Array.Clear(channelBuffer, 0, size);
                return;
            }

            int channel = pluginChannel % inputChannels;
            int frames = input.Length / inputChannels;
            int position = inputPosition;
            for (int n = 0; n < size; n++, position++)
            {
                if (position >= frames)
                {
                    if (!loop)
                    {
                        Array.Clear(channelBuffer, n, size - n);
                        return;
                    }
                    position = 0;
                }
                channelBuffer[n] = input[position * inputChannels + channel];
            }
        }

        void AdvanceInput(int size)
        {
            if (input == null || inputDone)
                return;

            int frames = input.Length / inputChannels;
            inputPosition += size;
            if (inputPosition < frames)
                return;
            if (loop)
            {
                inputPosition %= frames;
                return;
            }
            inputDone = true;
            InputFinished?.Invoke();
        }

        /// <summary>Stops processing and finishes the output file.</summary>
        public void Stop()
        {
            running = false;
            thread?.Join();
            thread = null;
            writer?.Flush();
        }

        public void Dispose()
        {
            Stop();
            writer?.Dispose();
        }

        public void BeginEdit(int parameter)
        {
        }

        public void EndEdit(int parameter)
        {
            PerformEdit(parameter, (plugin as IAudioPluginEditor).Parameters[parameter].NormalizedEditValue);
        }

        public void PerformEdit(int parameter, double normalizedValue)
        {
            (plugin as IAudioPluginProcessor).Parameters[parameter].NormalizedProcessValue = normalizedValue;
        }

        public void ProcessAllEvents()
        {
        }

        public int ProcessEvents() => (int)CurrentAudioBufferSize;

        public void SendCC(int channel, int ccNumber, int ccValue, int sampleOffset)
        {
        }

        public void SendNoteOff(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public void SendNoteOn(int channel, int noteNumber, float velocity, int sampleOffset)
        {
        }

        public void SendPolyPressure(int channel, int noteNumber, float pressure, int sampleOffset)
        {
        }
    }
}
