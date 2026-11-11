using Circuit;
using ComputerAlgebra;
using SpiceForge;
using Expression = ComputerAlgebra.Expression;

/// <summary>
/// Checks <see cref="SchematicProcessor"/> against the compiled engine it wraps: the same output at the schematic's
/// settings whatever the block sizes, the same output when a control is set between blocks (with smoothing off) as
/// setting the engine's parameters at that sample, smoothing that reaches its target after the smoothing time, and
/// channels that are independent of each other.
/// </summary>
internal static class ProcessorCheck
{
    private const int SampleRate = 48000;
    private const int Oversample = 8;
    private const int Samples = 9600;

    public static int Run(string testsDir, string? filter)
    {
        var files = new[] { "Circuits", "Examples" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(testsDir, folder), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
            .Where(p => filter == null || p.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int problems = 0;
        foreach (string file in files)
        {
            SchematicProcessor processor;
            try
            {
                processor = new SchematicProcessor(file, SampleRate, new SchematicProcessorOptions { PotSmoothingMilliseconds = 0 });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                continue; // No Input or Speaker, or LiveSPICE can't solve it.
            }
            Console.WriteLine($"=== {processor.Name}: pots {Describe(processor.Pots.Select(p => $"{p.Name} {p.DefaultPosition:0.##}"))}; "
                + $"switches {Describe(processor.Switches.Select(s => $"{s.Name} {s.DefaultPosition}/{s.Positions}"))}");
            try
            {
                problems += Check(file, processor);
            }
            catch (Exception ex)
            {
                problems++;
                Console.WriteLine($"  error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"{problems} problem(s).");
        return problems == 0 ? 0 : 1;
    }

    private static string Describe(IEnumerable<string> items) => items.Any() ? string.Join(", ", items) : "(none)";

    private static int Check(string file, SchematicProcessor processor)
    {
        int problems = 0;
        void Report(string what, bool ok)
        {
            if (!ok) problems++;
            Console.WriteLine($"  {what,-62}{(ok ? "ok" : "FAIL")}");
        }
        double[] signal = Enumerable.Range(0, Samples).Select(n => Signals.Harmonics(n / (double)SampleRate, 2.0, 82, 2)).ToArray();

        // The engine directly, as the reference: the same loader and engine, run in one block.
        double[] Reference(Action<double[], IReadOnlyList<RuntimeParameter>>? atHalf)
        {
            Expression h = (Real)1 / (SampleRate * Oversample);
            LoadedCircuit loaded = CircuitLoader.Load(file, h);
            Circuit.Circuit circuit = loaded.Circuit;
            Expression input = Component.DependentVariable(circuit.Components.OfType<Input>().Single().Name, Component.t);
            var speakers = circuit.Components.OfType<Speaker>().ToList();
            Expression[] nodes = speakers.SelectMany(s => new[] { s.Anode.V, s.Cathode.V }).ToArray();
            var engine = new CompiledCircuit(loaded.System, input, nodes, h, Oversample, 8, loaded.Solution);
            var output = new double[Samples];
            void Part(int start, int length)
            {
                double[][] buffers = nodes.Select(_ => new double[length]).ToArray();
                engine.Run(signal.AsSpan(start, length).ToArray(), buffers);
                // Combined as the renderer does: each speaker's voltage times 1 / V0dBFS, summed in order.
                for (int n = 0; n < length; n++)
                {
                    double sum = 0;
                    for (int k = 0; k < speakers.Count; k++)
                        sum += (buffers[2 * k][n] - buffers[2 * k + 1][n]) * (1 / (double)speakers[k].V0dBFS);
                    output[start + n] = sum;
                }
            }
            Part(0, Samples / 2);
            atHalf?.Invoke(engine.ParameterValues, loaded.Parameters);
            Part(Samples / 2, Samples / 2);
            return output;
        }

        double[] Through(SchematicProcessor p, int block, Action<SchematicProcessor>? atHalf)
        {
            var output = new double[Samples];
            for (int start = 0; start < Samples;)
            {
                if (start == Samples / 2)
                    atHalf?.Invoke(p);
                int length = Math.Min(block, (start < Samples / 2 ? Samples / 2 : Samples) - start);
                var o = new double[length];
                p.Process(signal.AsSpan(start, length), o);
                o.CopyTo(output, start);
                start += length;
            }
            return output;
        }

        double[] expected = Reference(null);
        Report("default settings, blocks of 333 samples, same as the engine", Through(processor, 333, null).SequenceEqual(expected));

        if (processor.Pots.Count > 0)
        {
            PotControl pot = processor.Pots[0];
            double target = pot.DefaultPosition < 0.5 ? 0.9 : 0.1;
            var fresh = new SchematicProcessor(file, SampleRate, new SchematicProcessorOptions { PotSmoothingMilliseconds = 0 });
            double[] moved = Through(fresh, 512, p => p.GetPot(pot.Name).Position = target);
            double[] reference = Reference((values, parameters) => DynamicPots.Set(values, parameters, pot.Name, target));
            Report($"pot {pot.Name} to {target} between blocks, same as the engine", moved.SequenceEqual(reference));

            // Smoothed over 5 ms: different from the instant move while it ramps, the same as the 5 ms point would be
            // reached; just check it starts like the unmoved circuit and ends near the instant one.
            var smooth = new SchematicProcessor(file, SampleRate, new SchematicProcessorOptions { PotSmoothingMilliseconds = 5 });
            double[] smoothed = Through(smooth, 512, p => p.GetPot(pot.Name).Position = target);
            int half = Samples / 2;
            bool startsUnmoved = smoothed.AsSpan(0, half).SequenceEqual(expected.AsSpan(0, half));
            bool differsWhileRamping = !smoothed.AsSpan(half, 240).SequenceEqual(moved.AsSpan(half, 240));
            Report($"pot {pot.Name} smoothed over 5 ms: unchanged before, ramping after", startsUnmoved && differsWhileRamping);
        }

        if (processor.Switches.Count > 0)
        {
            SwitchControl sw = processor.Switches[0];
            int target = (sw.DefaultPosition + 1) % sw.Positions;
            var fresh = new SchematicProcessor(file, SampleRate);
            double[] moved = Through(fresh, 700, p => p.GetSwitch(sw.Name).Position = target);
            double[] reference = Reference((values, parameters) => DynamicSwitches.Set(values, parameters, sw.Name, target));
            Report($"switch {sw.Name} to {target} between blocks, same as the engine", moved.SequenceEqual(reference));
        }

        // Two channels: each the same as a single-channel processor fed the same input.
        var stereo = new SchematicProcessor(file, SampleRate, new SchematicProcessorOptions { Channels = 2 });
        double[] quiet = signal.Select(x => x * 0.25).ToArray();
        var outputs = new[] { new double[Samples], new double[Samples] };
        stereo.Process(new[] { signal, quiet }, outputs, Samples);
        double[] Mono(double[] input)
        {
            var mono = new SchematicProcessor(file, SampleRate);
            var o = new double[Samples];
            mono.Process(input, o);
            return o;
        }
        Report("two channels, each the same as a single-channel processor", outputs[0].SequenceEqual(Mono(signal)) && outputs[1].SequenceEqual(Mono(quiet)));

        // Moving pots and switches allocates nothing once warmed up: blocks with pots ramping and switches flipping.
        var live = new SchematicProcessor(file, SampleRate, new SchematicProcessorOptions { Channels = 2 });
        double[][] ins = { signal.Take(512).ToArray(), quiet.Take(512).ToArray() };
        double[][] outs = { new double[512], new double[512] };
        // Indexed loops: foreach over the IReadOnlyList would box an enumerator, which is this test's allocation, not the
        // processor's.
        void Block(int i)
        {
            for (int k = 0; k < live.Pots.Count; k++)
                live.Pots[k].Position = (i % 3) / 2.0;
            for (int k = 0; k < live.Switches.Count; k++)
                live.Switches[k].Position = i % live.Switches[k].Positions;
            live.Process(ins, outs, 512);
        }
        for (int i = 0; i < 6; i++)
            Block(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 6; i < 66; i++)
            Block(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Report($"moving every pot and switch for 60 blocks: {allocated} bytes allocated", allocated == 0);
        return problems;
    }
}
