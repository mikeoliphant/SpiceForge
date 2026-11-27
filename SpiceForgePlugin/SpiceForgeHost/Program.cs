using System;
using System.Runtime.InteropServices;
using AudioPlugSharp;
using AudioPlugSharpStandalone;

namespace SpiceForgeHost
{
    /// <summary>
    /// Runs the SpiceForge plugin as an application, in AudioPlugSharp's standalone host: audio through ASIO, ALSA or
    /// JACK, or with no device (the internal clock), chosen in the host's settings and saved. The plugin's state (the
    /// schematic and its settings) is saved too, so the next run starts where this one ended.
    /// </summary>
    static class Program
    {
        const string Usage =
@"Usage: SpiceForgeHost [options]

  --save-folder <folder>   where the plugin's state and audio settings are kept (default: SpiceForge in the
                           user's application data)
  --audio <driver>         None (the internal clock), ASIO, ALSA or JACK
  --audio-device <name>    the driver's device
  --sample-rate <hz>       the device's sample rate
  --buffer-size <frames>   the device's buffer size

Audio choices are saved, and used from then on.";

        [STAThread]
        static int Main(string[] args)
        {
            StandaloneHost host = StandaloneHost.ForPlugin<SpiceForgePlugin.SpiceForgePlugin>();

            try
            {
                for (int i = 0; i < args.Length; i += 2)
                {
                    string value = (i + 1 < args.Length) ? args[i + 1] : throw new UsageException();

                    switch (args[i])
                    {
                        case "--save-folder": host.SaveFolder = System.IO.Path.GetFullPath(value); break;
                        case "--audio": host.AudioDriver = value; break;
                        case "--audio-device": host.AudioDevice = value; break;
                        case "--sample-rate": host.AudioSampleRate = Number(value); break;
                        case "--buffer-size": host.AudioBufferSize = Number(value); break;
                        default: throw new UsageException();
                    }
                }
            }
            catch (UsageException)
            {
                WriteConsole(Usage);

                return 2;
            }

            try
            {
                host.Run();
            }
            finally
            {
                // The log is written on a thread of its own: its last lines, before the process ends.
                Logger.FlushAndShutdown();
            }

            return 0;
        }

        static int Number(string value) => int.TryParse(value, out int n) && (n > 0) ? n : throw new UsageException();

        class UsageException : Exception
        {
        }

        // A WinExe has no console of its own: write to the one it was started from, if any.
        static void WriteConsole(string text)
        {
            if (OperatingSystem.IsWindows() && !AttachConsole(AttachParentProcess))
                return;

            Console.Error.WriteLine();
            Console.Error.WriteLine(text);
        }

        const int AttachParentProcess = -1;

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);
    }
}
