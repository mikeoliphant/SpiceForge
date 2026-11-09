using Circuit;
using ComputerAlgebra;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Util;

namespace SpiceForge.ReferenceCapture
{
    /// <summary>
    /// Records reference output and Newton iteration counts from LiveSPICE for every test circuit.
    /// Usage: SpiceForge.ReferenceCapture [--samples N] [--filter text] [--out dir]
    /// </summary>
    internal static class Program
    {
        private const int SampleRate = 48000;
        private const int Oversample = 8;
        private const int Iterations = 8;
        private const int FormatVersion = 1;

        private static readonly string[] CircuitFolders = { "Circuits", "Examples", "Unstable" };

        // The same waveform as LiveSPICE's own test runner, at its level and at a hotter one
        // that pushes nonlinear circuits into the samples that are hard to converge.
        private static readonly (string Name, double Amplitude)[] Stimuli = { ("harmonics-0.5", 0.5), ("harmonics-5", 5.0) };

        private static int Main(string[] args)
        {
            int samples = 4800;
            string filter = null;
            string repo = FindRepoRoot();
            string outDir = Path.Combine(repo, "Reference");

            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    return Usage();
                switch (args[i])
                {
                    case "--samples": samples = int.Parse(args[i + 1]); break;
                    case "--filter": filter = args[i + 1]; break;
                    case "--out": outDir = Path.GetFullPath(args[i + 1]); break;
                    default: return Usage();
                }
            }

            string testsDir = Path.Combine(repo, "Dependencies", "LiveSPICE", "Tests");
            var files = CircuitFolders
                .SelectMany(f => Directory.GetFiles(Path.Combine(testsDir, f), "*.schx").OrderBy(p => p, StringComparer.Ordinal))
                .Where(p => filter == null || p.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var entries = new List<object>();
            int failures = 0;
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(testsDir, file).Replace('\\', '/');
                Console.WriteLine(relative);
                foreach (var stimulus in Stimuli)
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        object entry = Capture(file, relative, stimulus.Name, stimulus.Amplitude, samples, outDir, out bool identical);
                        entries.Add(entry);
                        if (!identical)
                        {
                            failures++;
                            Console.WriteLine($"  {stimulus.Name}: MISMATCH against stock Simulation");
                        }
                        else
                        {
                            Console.WriteLine($"  {stimulus.Name}: ok ({watch.Elapsed.TotalSeconds:F1}s)");
                        }
                    }
                    catch (Exception ex)
                    {
                        entries.Add(new { circuit = relative, stimulus = stimulus.Name, status = "error", error = ex.GetType().Name + ": " + ex.Message });
                        Console.WriteLine($"  {stimulus.Name}: error: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            Directory.CreateDirectory(outDir);
            var manifest = new { formatVersion = FormatVersion, sampleRate = SampleRate, oversample = Oversample, iterations = Iterations, samples, circuits = entries };
            File.WriteAllText(Path.Combine(outDir, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine($"{entries.Count} captures, {failures} mismatches against stock Simulation.");
            return failures == 0 ? 0 : 1;
        }

        private static object Capture(string file, string relative, string stimulus, double amplitude, int samples, string outDir, out bool identical)
        {
            var circuit = Schematic.Load(file, new NullLog()).Build();
            Analysis analysis = circuit.Analyze();
            TransientSolution solution = TransientSolution.Solve(analysis, (Real)1 / (SampleRate * Oversample));

            Expression input = circuit.Components.Where(i => i is Input)
                .Select(i => Component.DependentVariable(i.Name, Component.t))
                .DefaultIfEmpty("V[t]")
                .Single();
            Expression[] outputs = circuit.Nodes.Select(i => i.V).ToArray();

            double[] inputBuffer = new double[samples];
            for (int n = 0; n < samples; n++)
                inputBuffer[n] = Harmonics(n / (double)SampleRate, amplitude, 82, 2);

            double[][] stock = NewBuffers(outputs.Length, samples);
            new Simulation(solution) { Oversample = Oversample, Iterations = Iterations, Input = new[] { input }, Output = outputs }
                .Run(inputBuffer, stock);

            var recorder = new NewtonRecorder();
            double[][] recorded = NewBuffers(outputs.Length, samples);
            new InstrumentedSimulation(solution, recorder) { Oversample = Oversample, Iterations = Iterations, Input = new[] { input }, Output = outputs }
                .Run(inputBuffer, recorded);

            identical = stock.Zip(recorded, BitIdentical).All(same => same);

            string name = relative.Replace(".schx", "") + "." + stimulus + ".ref";
            string path = Path.Combine(outDir, "data", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] content = Serialize(outputs, stock, recorder);
            File.WriteAllBytes(path, content);

            var steps = recorder.Steps.SelectMany(s => s).ToList();
            return new
            {
                circuit = relative,
                stimulus,
                status = identical ? "ok" : "mismatch",
                sha256 = Convert.ToHexString(SHA256.HashData(content)),
                outputs = outputs.Length,
                newtonSystems = recorder.Steps.Length,
                newtonSizes = solution.Solutions.OfType<NewtonIteration>().Select(s => s.Unknowns.Count()).ToArray(),
                totalIterations = steps.Sum(b => (long)(b & ~NewtonRecorder.CapFlag)),
                cappedSteps = steps.Count(b => (b & NewtonRecorder.CapFlag) != 0),
                nonFinite = stock.Any(o => o.Any(v => double.IsNaN(v) || double.IsInfinity(v))),
            };
        }

        // Layout: version, samples, output count, output names, output samples (one column per output),
        // Newton system count, then for each system its step count and step bytes.
        private static byte[] Serialize(Expression[] outputs, double[][] data, NewtonRecorder recorder)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(FormatVersion);
                writer.Write(data.Length == 0 ? 0 : data[0].Length);
                writer.Write(outputs.Length);
                foreach (Expression output in outputs)
                    writer.Write(output.ToString());
                foreach (double[] column in data)
                    foreach (double value in column)
                        writer.Write(value);
                writer.Write(recorder.Steps.Length);
                foreach (List<byte> steps in recorder.Steps)
                {
                    writer.Write(steps.Count);
                    writer.Write(steps.ToArray());
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static double[][] NewBuffers(int outputs, int samples) =>
            Enumerable.Range(0, outputs).Select(_ => new double[samples]).ToArray();

        private static bool BitIdentical(double[] a, double[] b) =>
            a.Zip(b, (x, y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)).All(same => same);

        private static double Harmonics(double t, double amplitude, double f0, int count)
        {
            double s = 0;
            for (int i = 1; i <= count; ++i)
                s += Math.Sin(t * f0 * 2 * 3.1415 * i) / count;
            return amplitude * s;
        }

        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "SpiceForge.slnx")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("SpiceForge.slnx not found above " + AppContext.BaseDirectory);
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage: SpiceForge.ReferenceCapture [--samples N] [--filter text] [--out dir]");
            return 2;
        }
    }
}
