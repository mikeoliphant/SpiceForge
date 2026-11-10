using Circuit;
using ComputerAlgebra;
using NAudio.Wave;
using SpiceForge;
using Util;
using Expression = ComputerAlgebra.Expression;

/// <summary>Runs a WAV file through the compiled engine and stock Simulation, and reports where they differ.</summary>
internal static class WavCheck
{
    private const int Oversample = 8;
    private const int IterationCap = 8;
    private const int BlockSize = 4800;
    public static bool UseInterpreter;

    public static int Run(string schematic, string wav, int show)
    {
        using var reader = new WaveFileReader(wav);
        int rate = reader.WaveFormat.SampleRate;
        var all = new List<float>();
        var buffer = new float[BlockSize * reader.WaveFormat.Channels];
        ISampleProvider provider = reader.ToSampleProvider();
        int read;
        while ((read = provider.Read(buffer.AsSpan())) > 0)
            for (int i = 0; i < read; i += reader.WaveFormat.Channels)
                all.Add(buffer[i]);
        double[] input = all.Select(x => (double)x).ToArray();

        var circuit = Schematic.Load(schematic, new NullLog()).Build();
        Expression inputSymbol = Component.DependentVariable(circuit.Components.OfType<Input>().Single().Name, Component.t);
        Speaker speaker = circuit.Components.OfType<Speaker>().Single();
        Expression[] nodes = { speaker.Anode.V, speaker.Cathode.V };
        double scale = 1 / (double)speaker.V0dBFS;

        Expression h = (Real)1 / (rate * Oversample);
        Analysis analysis = circuit.Analyze();
        TransientSolution solution = TransientSolution.Solve(analysis, h);
        ExtractedSystem system = Extractor.Extract(analysis, h);

        var stock = new Simulation(solution) { Oversample = Oversample, Iterations = IterationCap, Input = new[] { inputSymbol }, Output = new[] { speaker.Out } };
        dynamic compiled = UseInterpreter
            ? new CircuitInterpreter(system, inputSymbol, nodes, h, Oversample, IterationCap, solution)
            : new CompiledCircuit(system, inputSymbol, nodes, h, Oversample, IterationCap, solution);

        var stockOut = new double[input.Length];
        var ours = new double[input.Length];
        var block = new double[BlockSize];
        double[][] nodeBlocks = { new double[BlockSize], new double[BlockSize] };
        for (int start = 0; start < input.Length; start += BlockSize)
        {
            int count = Math.Min(BlockSize, input.Length - start);
            double[] inBlock = count == BlockSize ? block : new double[count];
            Array.Copy(input, start, inBlock, 0, count);
            if (count != BlockSize)
                nodeBlocks = new[] { new double[count], new double[count] };

            var outBlock = new double[count];
            stock.Run(inBlock, outBlock);
            Array.Copy(outBlock, 0, stockOut, start, count);

            compiled.Run(inBlock, nodeBlocks);
            for (int n = 0; n < count; n++)
                ours[start + n] = (nodeBlocks[0][n] - nodeBlocks[1][n]) * scale;
        }

        List<byte> counts = compiled.IterationCounts;
        // 100 ms windows.
        int window = rate / 10;
        var rows = new List<(int Index, double StockPeak, double OurPeak, double Error, int Capped, double InputPeak)>();
        double overallPeak = stockOut.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max();
        for (int w = 0; w * window < input.Length; w++)
        {
            int from = w * window, to = Math.Min(input.Length, from + window);
            double stockPeak = 0, ourPeak = 0, error = 0, inputPeak = 0;
            for (int n = from; n < to; n++)
            {
                stockPeak = Math.Max(stockPeak, Math.Abs(stockOut[n]));
                ourPeak = Math.Max(ourPeak, Math.Abs(ours[n]));
                error = Math.Max(error, Math.Abs(stockOut[n] - ours[n]));
                inputPeak = Math.Max(inputPeak, Math.Abs(input[n]));
            }
            int capped = 0;
            for (int s = from * Oversample; s < to * Oversample && s < counts.Count; s++)
                if ((counts[s] & 0x80) != 0)
                    capped++;
            rows.Add((w, stockPeak, ourPeak, error, capped, inputPeak));
        }

        string Db(double x) => x == 0 ? "-inf" : (20 * Math.Log10(x / Math.Max(overallPeak, 1e-30))).ToString("F1");
        int badWindows = rows.Count(r => r.Error > overallPeak * 1e-3 || !double.IsFinite(r.Error));
        int cappedSteps = counts.Count(b => (b & 0x80) != 0);
        int run = 0, stuckAt = -1;
        for (int s = 0; s < counts.Count && stuckAt < 0; s++)
        {
            run = (counts[s] & 0x80) != 0 ? run + 1 : 0;
            if (run >= 2000)
                stuckAt = s - run + 1;
        }
        if (stuckAt >= 0)
        {
            Console.WriteLine($"Capped continuously from step {stuckAt} ({stuckAt / (double)(rate * Oversample):F6} s). Counts before: "
                + string.Join(" ", counts.Skip(Math.Max(0, stuckAt - 20)).Take(24).Select(b => b.ToString("X2"))));
            int sample = stuckAt / Oversample;
            Console.WriteLine($"Output around it, stock / ours: " + string.Join("  ", Enumerable.Range(sample - 3, 6).Select(n => $"{stockOut[n]:G4}/{ours[n]:G4}")));
        }
        Console.WriteLine($"{input.Length / (double)rate:F1} s at {rate} Hz. Stock peak {overallPeak:G4}. Windows with error above -60 dB re peak: {badWindows} of {rows.Count}. Our capped steps: {cappedSteps} of {counts.Count}.");
        Console.WriteLine($"{"time s",8}{"input pk",10}{"stock pk",10}{"ours pk",12}{"error dB",10}{"capped",8}");
        foreach (var r in rows.OrderByDescending(r => double.IsFinite(r.Error) ? r.Error : double.MaxValue).Take(show).OrderBy(r => r.Index))
            Console.WriteLine($"{r.Index / 10.0,8:F1}{r.InputPeak,10:F3}{r.StockPeak,10:G4}{r.OurPeak,12:G4}{Db(r.Error),10}{r.Capped,8}");
        return 0;
    }
}
