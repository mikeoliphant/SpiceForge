using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AudioPlugSharp;
using AudioPlugSharpHost;

namespace SpiceForgeHost
{
    /// <summary>The command line: which audio to use, and what to load.</summary>
    sealed class HostOptions
    {
        public string SchematicPath;
        public bool NoAudio;
        public string InputPath;
        public string OutputPath;
        public bool Loop;
        public int SampleRate = 48000;
        public bool SampleRateGiven;
        public int BufferSize = 256;
        public bool ExitWhenDone;

        /// <summary>Whether to run without an ASIO device.</summary>
        public bool UseFiles => NoAudio || InputPath != null || OutputPath != null;
    }

    /// <summary>
    /// Runs the SpiceForge plugin as a stand-alone Windows app. By default the audio goes through an ASIO device; for
    /// testing, it can run with no device, from and to WAV files.
    /// </summary>
    static class Program
    {
        const string Usage =
@"Usage: SpiceForgeHost [options]

With no options, audio goes through an ASIO device (chosen on first run).

  --schematic <file.schx>  Load this schematic at startup.
  --no-audio               Run without an audio device, on silent input.
  --input <file.wav>       Run without an audio device, playing this file as the input.
                           The sample rate is the file's.
  --output <file.wav>      Run without an audio device, recording the output to this file.
  --loop                   Loop the input file.
  --exit-when-done         Close once the input file has played (needs --input, not --loop).
  --sample-rate <hz>       Sample rate without an input file (default 48000).
  --buffer-size <samples>  Block size without an audio device (default 256).

Without an audio device, the plugin's saved state isn't loaded or saved, and when a schematic is
given, playback starts once it has built.";

        [STAThread]
        static int Main(string[] args)
        {
            HostOptions options;
            try
            {
                options = Parse(args);
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message + "\n\n" + Usage);
            }
            if (options == null)
            {
                WriteConsole(Usage);
                return 0;
            }

            var plugin = new SpiceForgePlugin.SpiceForgePlugin();
            return options.UseFiles ? RunWithFiles(plugin, options) : RunWithAsio(plugin, options);
        }

        static int RunWithAsio(SpiceForgePlugin.SpiceForgePlugin plugin, HostOptions options)
        {
            var host = new WindowsFormsHost<SpiceForgePlugin.SpiceForgePlugin>(plugin);
            if (options.SchematicPath != null)
                plugin.LoadSchematic(options.SchematicPath);
            host.Run();
            return 0;
        }

        static int RunWithFiles(SpiceForgePlugin.SpiceForgePlugin plugin, HostOptions options)
        {
            FileAudioHost host;
            try
            {
                host = new FileAudioHost(plugin, options);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }

            using (host)
            {
                if (options.SchematicPath != null)
                    plugin.LoadSchematic(options.SchematicPath);
                if (options.ExitWhenDone)
                    host.InputFinished += () =>
                    {
                        // Raised on the audio thread; a failure here would end the process.
                        try { plugin.HideEditor(); }
                        catch (Exception ex) { Logger.Log("Couldn't close the editor: " + ex); }
                    };

                host.Start(waitForBuild: options.SchematicPath != null);
                try
                {
                    plugin.ShowEditor(IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    Logger.Log("Plugin failed with: " + ex);
                }
                host.Stop();
            }

            Logger.FlushAndShutdown();

            if (plugin.Status != null)
                WriteConsole(plugin.Status);
            return 0;
        }

        // Returns null for --help.
        static HostOptions Parse(string[] args)
        {
            var options = new HostOptions();
            bool bufferSizeGiven = false;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{arg} needs a value.");
                int Number()
                {
                    string value = Value();
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n <= 0)
                        throw new ArgumentException($"{arg} needs a positive number, not '{value}'.");
                    return n;
                }

                switch (arg)
                {
                    case "--schematic": options.SchematicPath = ExistingFile(Value()); break;
                    case "--no-audio": options.NoAudio = true; break;
                    case "--input": options.InputPath = ExistingFile(Value()); break;
                    case "--output": options.OutputPath = Path.GetFullPath(Value()); break;
                    case "--loop": options.Loop = true; break;
                    case "--exit-when-done": options.ExitWhenDone = true; break;
                    case "--sample-rate": options.SampleRate = Number(); options.SampleRateGiven = true; break;
                    case "--buffer-size": options.BufferSize = Number(); bufferSizeGiven = true; break;
                    case "--help": case "-h": case "/?": return null;
                    default: throw new ArgumentException($"Unknown option '{arg}'.");
                }
            }

            if (options.ExitWhenDone && (options.InputPath == null || options.Loop))
                throw new ArgumentException("--exit-when-done needs --input, and can't be used with --loop.");
            if (options.Loop && options.InputPath == null)
                throw new ArgumentException("--loop needs --input.");
            if (options.SampleRateGiven && options.InputPath != null)
                throw new ArgumentException("--sample-rate can't be used with --input, which uses the file's rate.");
            if ((options.SampleRateGiven || bufferSizeGiven) && !options.UseFiles)
                throw new ArgumentException("--sample-rate and --buffer-size are only for running without an audio device.");
            return options;
        }

        static string ExistingFile(string path)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"Can't find '{path}'.");
            return Path.GetFullPath(path);
        }

        static int Fail(string message)
        {
            WriteConsole(message);
            return 1;
        }

        // A WinExe has no console of its own; write to the one it was started from, if any.
        static void WriteConsole(string text)
        {
            if (AttachConsole(AttachParentProcess))
            {
                using var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                error.WriteLine();
                error.WriteLine(text);
            }
        }

        const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);
    }
}
