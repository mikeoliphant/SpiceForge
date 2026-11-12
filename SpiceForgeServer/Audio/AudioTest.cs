using System.Diagnostics;
using System.Text.Json.Nodes;
using SpiceForgeServer.Protocol;

namespace SpiceForgeServer.Audio;

/// <summary>
/// <c>audio.test</c>: loops the sum of the device's inputs to every output, as LiveSPICE's Test button does. The summed
/// signal streams to every connection as scope stream 0 (source 0), and <c>audio.levels</c> carries each channel's
/// peak, at about 10 Hz.
/// </summary>
public sealed class AudioTest : IAudioClient
{
    private const int MonitorRate = 60;
    private const int LevelRate = 10;
    private const double LevelDecaySeconds = 0.25;
    private static readonly int[] Streams = { 0 };

    private readonly Hub hub;
    private readonly ScopeRing scope = new(18, 9);
    private readonly Thread monitor;
    private volatile bool stopping;
    private volatile AudioFormat format = new(48000, 0, Array.Empty<string>(), Array.Empty<string>());
    private double[] inputPeaks = Array.Empty<double>(), outputPeaks = Array.Empty<double>();
    private long sample;

    public AudioTest(Hub hub)
    {
        this.hub = hub;
        monitor = new Thread(Monitor) { IsBackground = true, Name = "Audio test monitor" };
    }

    public void Start() => monitor.Start();

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
        for (int n = 0; n < frames; n++)
        {
            float sum = 0;
            for (int i = 0; i < block.Inputs.Length; i++)
                sum += block.Inputs[i][n];
            for (int o = 0; o < block.Outputs.Length; o++)
                block.Outputs[o][n] = sum;
            outPeak = Math.Max(outPeak, Math.Abs(sum));
            if (scoping)
                scope.Put(0, n, sum);
        }
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
