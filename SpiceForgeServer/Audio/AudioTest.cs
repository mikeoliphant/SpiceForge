using System.Diagnostics;
using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>What the audio test plays: a sine tone, or the sum of the inputs (LiveSPICE's Test).</summary>
public enum AudioTestSignal { Tone, Inputs }

/// <summary>
/// <c>audio.test</c>: plays a 440 Hz tone on every output, or loops the sum of the device's inputs to them, as
/// LiveSPICE's Test button does. Either way the inputs' sum streams to every connection as scope stream 0 (source 0),
/// so a tone looped back by a cable shows there, and <c>audio.levels</c> carries each channel's peak, at about 10 Hz.
///
/// The output starts and stops without a click: it fades in over <see cref="FadeSeconds"/> with a raised cosine, and
/// <see cref="FadeOut"/> fades it out the same way, finishing once two whole blocks of silence have followed (a device
/// plays one block while the next is filled), so the device closes on silence. The tone's phase runs on from zero and is never reset while it plays.
/// </summary>
public sealed class AudioTest : IAudioClient
{
    private const int MonitorRate = 60;
    private const int LevelRate = 10;
    private const double LevelDecaySeconds = 0.25;
    public const double ToneHz = 440;
    /// <summary>The tone's level: −18 dBFS, loud enough to hear and well clear of clipping.</summary>
    public static readonly float ToneAmplitude = (float)Math.Pow(10, -18 / 20.0);
    public const double FadeSeconds = 0.02;
    private static readonly int[] Streams = { 0 };

    private readonly Hub hub;
    private readonly AudioTestSignal signal;
    private readonly ScopeRing scope = new(18, 9);
    private readonly Thread monitor;
    private volatile bool stopping;
    private volatile AudioFormat format = new(48000, 0, Array.Empty<string>(), Array.Empty<string>());
    private double[] inputPeaks = Array.Empty<double>(), outputPeaks = Array.Empty<double>();
    private long sample;

    // The audio thread's: the tone's phase (in cycles) and the fade's position, 0 (silent) to 1 (full).
    private double phase;
    private double fade;
    private volatile bool fadingOut;
    private int silentBlocks;
    // Set once the fade-out has finished and two whole blocks of silence have followed it. Completed on the audio thread;
    // its continuations run elsewhere.
    private readonly TaskCompletionSource silent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AudioTest(Hub hub, AudioTestSignal signal = AudioTestSignal.Tone)
    {
        this.hub = hub;
        this.signal = signal;
        monitor = new Thread(Monitor) { IsBackground = true, Name = "Audio test monitor" };
    }

    public AudioTestSignal Signal => signal;

    public void Start() => monitor.Start();

    /// <summary>Fades the output out, and completes once it's silent, or after <paramref name="timeout"/> if the device
    /// has stopped calling (then there's nothing to hear anyway).</summary>
    public async Task FadeOut(TimeSpan timeout)
    {
        fadingOut = true;
        await Task.WhenAny(silent.Task, Task.Delay(timeout));
    }

    public void Stop() => stopping = true;

    public void Prepare(AudioFormat newFormat)
    {
        inputPeaks = new double[newFormat.Inputs.Count];
        outputPeaks = new double[newFormat.Outputs.Count];
        format = newFormat;
    }

    public void Process(AudioBlock block)
    {
        int frames = block.Frames;
        double[] inPeaks = inputPeaks, outPeaks = outputPeaks;
        bool scoping = scope.Begin(1, frames);
        double outPeak = 0;
        double rate = format.SampleRate > 0 ? format.SampleRate : 48000;
        double step = ToneHz / rate;
        double fadeStep = 1 / (FadeSeconds * rate);
        bool fadingOut = this.fadingOut;
        bool wasSilent = fade <= 0;
        for (int n = 0; n < frames; n++)
        {
            float sum = 0;
            for (int i = 0; i < block.Inputs.Length; i++)
                sum += block.Inputs[i][n];

            fade = fadingOut ? Math.Max(0, fade - fadeStep) : Math.Min(1, fade + fadeStep);
            float gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * fade));
            float output;
            if (signal == AudioTestSignal.Tone)
            {
                output = gain * ToneAmplitude * (float)Math.Sin(2 * Math.PI * phase);
                phase += step;
                if (phase >= 1)
                    phase -= 1;
            }
            else
                output = gain * sum;

            for (int o = 0; o < block.Outputs.Length; o++)
                block.Outputs[o][n] = output;
            outPeak = Math.Max(outPeak, Math.Abs(output));
            if (scoping)
                scope.Put(0, n, sum);
        }
        // Whole blocks of silence after the fade-out. Two, since a device plays one block while the next is filled (ASIO
        // double-buffers): after the second, the fade's last block has been played, and the device can close quietly.
        if (fadingOut && wasSilent && ++silentBlocks >= 2)
            silent.TrySetResult();
        for (int i = 0; i < block.Inputs.Length && i < inPeaks.Length; i++)
        {
            double peak = 0;
            float[] input = block.Inputs[i];
            for (int n = 0; n < frames; n++)
                peak = Math.Max(peak, Math.Abs(input[n]));
            Accumulate.Max(ref inPeaks[i], peak);
        }
        for (int o = 0; o < outPeaks.Length; o++)
            Accumulate.Max(ref outPeaks[o], outPeak);
        scope.Commit(sample, Streams);
        sample += frames;
    }

    private void Monitor()
    {
        var clock = Stopwatch.StartNew();
        double nextLevels = 0, last = 0;
        double[] shownIn = Array.Empty<double>(), shownOut = Array.Empty<double>();
        while (!stopping)
        {
            Thread.Sleep(1000 / MonitorRate);
            foreach (byte[] frame in scope.Drain(0, (float)format.SampleRate))
                foreach (Session session in hub.Sessions)
                    session.TrySendBinary(frame);

            double now = clock.Elapsed.TotalSeconds;
            if (now < nextLevels)
                continue;
            nextLevels = now + 1.0 / LevelRate;
            double decay = Math.Exp(-(now - last) / LevelDecaySeconds);
            last = now;
            shownIn = Levels(inputPeaks, shownIn, decay);
            shownOut = Levels(outputPeaks, shownOut, decay);
            hub.Broadcast("audio.levels", new JsonObject
            {
                ["inputs"] = Json.Array(shownIn.Select(p => (JsonNode?)p)),
                ["outputs"] = Json.Array(shownOut.Select(p => (JsonNode?)p)),
            });
        }
    }

    // Each level: its peak since the last time, or the last level decayed, whichever is higher.
    private static double[] Levels(double[] peaks, double[] shown, double decay)
    {
        if (shown.Length != peaks.Length)
            shown = new double[peaks.Length];
        for (int i = 0; i < peaks.Length; i++)
            shown[i] = Math.Max(shown[i] * decay, Interlocked.Exchange(ref peaks[i], 0));
        return shown;
    }
}
