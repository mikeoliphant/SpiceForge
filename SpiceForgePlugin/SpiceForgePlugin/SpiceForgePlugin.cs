using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioPlugSharp;
using SpiceForge;
using UILayout;

namespace SpiceForgePlugin
{
    /// <summary>
    /// The plugin's saved state: the schematic, the engine settings, and every pot and switch position by name.
    /// </summary>
    public class SpiceForgeSaveState : AudioPluginSaveState
    {
        public string SchematicPath { get; set; } = "";
        public int Oversample { get; set; } = SpiceForgePlugin.DefaultOversample;
        public int Iterations { get; set; } = SpiceForgePlugin.DefaultIterations;
        public bool AutoReload { get; set; }
        public bool SincResampling { get; set; }
        public List<ControlSetting> Pots { get; set; } = new List<ControlSetting>();
        public List<ControlSetting> Switches { get; set; } = new List<ControlSetting>();
    }

    public class ControlSetting
    {
        public string Name { get; set; }
        public double Value { get; set; }
    }

    /// <summary>
    /// Runs audio through a LiveSPICE schematic with SpiceForge's <see cref="SchematicProcessor"/>. The left input
    /// channel goes through the circuit, and its output goes to both output channels.
    /// </summary>
    /// <remarks>
    /// Schematics are built on a background thread, since compiling takes a second or two; the audio passes through
    /// unchanged until the first build is ready, and a rebuild (new settings, or the schematic edited on disk) swaps in
    /// when it's done, starting from the same pot and switch positions. The editor polls <see cref="Version"/> to
    /// notice a new processor or status.
    /// </remarks>
    public class SpiceForgePlugin : AudioPluginBase
    {
        // 4× rather than LiveSPICE's 8×: with the cap of 32 below it was within 1-3 dB of 8× on every example, for
        // 35-45% less CPU (see Oversampling in LessonsLearned.md).
        public const int DefaultOversample = 4;
        // A cap of 32 rather than LiveSPICE's 8: it only costs anything on steps that would hit the cap, and at 8× it
        // took the Rat's 5 kHz aliasing from -22 to -39 dB at the same CPU (see Oversampling in LessonsLearned.md).
        public const int DefaultIterations = 32;
        public static readonly int[] OversampleChoices = { 1, 2, 4, 8, 16 };
        public static readonly int[] IterationChoices = { 1, 2, 4, 8, 16, 32, 64 };

        /// <summary>The schematic loaded (or being loaded), or empty.</summary>
        public string SchematicPath { get; private set; } = "";
        public int Oversample { get; private set; } = DefaultOversample;
        public int Iterations { get; private set; } = DefaultIterations;
        public bool AutoReload { get; private set; }

        /// <summary>Whether to resample with windowed sincs (a flat band and little aliasing, about 40 samples of
        /// latency the host isn't told about) instead of LiveSPICE's linear interpolation and averaging.</summary>
        public bool SincResampling { get; private set; }

        /// <summary>The running processor, or null while there's none (audio passes through).</summary>
        public SchematicProcessor CurrentProcessor => Volatile.Read(ref processor);

        /// <summary>What the plugin is doing, if not just running: building, or why it stopped. Null when running.</summary>
        public string Status { get; private set; }

        /// <summary>Bumped whenever <see cref="CurrentProcessor"/> or <see cref="Status"/> changes.</summary>
        public int Version => Volatile.Read(ref version);

        /// <summary>Whether a build has been started and hasn't finished (succeeded or failed) yet.</summary>
        public bool IsBuilding => Volatile.Read(ref finishedRequest) != Volatile.Read(ref buildRequest);

        DoubleAudioIOPort stereoInput;
        DoubleAudioIOPort stereoOutput;

        // One channel through the circuit: its input and output, as SchematicProcessor takes them.
        double[][] monoIn = { Array.Empty<double>() };
        double[][] monoOut = { Array.Empty<double>() };

        SchematicProcessor processor;
        double sampleRate;
        int version;
        // Set by the audio thread when the circuit fails, so it passes the audio through until the next build.
        volatile bool failed;

        // Builds run one at a time, newest wins: a build checks it's still the latest request before starting and
        // before swapping in.
        readonly object buildLock = new object();
        int buildRequest;
        // The last request that finished, or needed no build; IsBuilding while it's behind buildRequest.
        int finishedRequest;

        // Positions to start the next build at, when there's no processor to take them from.
        Dictionary<string, double> pendingPots = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> pendingSwitches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        FileSystemWatcher watcher;
        Timer reloadTimer;

        MonoGameHost GameHost;

        public SpiceForgePlugin()
        {
            Company = "Mike Oliphant";
            Website = "github.com/mikeoliphant";
            Contact = "contact@nostatic.org";
            PluginName = "SpiceForge";
            PluginCategory = "Fx";
            PluginVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(this.GetType().Assembly.Location).FileVersion;

            PluginID = 0x47B5882C49394EAD;

            HasUserInterface = true;
            EditorWidth = 800;
            EditorHeight = 400;

            SaveStateData = new SpiceForgeSaveState();
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { stereoInput = new DoubleAudioIOPort("Stereo Input", EAudioChannelConfiguration.Stereo) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new DoubleAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };
        }

        /// <summary>Loads a schematic, at its own pot and switch settings.</summary>
        public void LoadSchematic(string path)
        {
            SchematicPath = path ?? "";
            pendingPots.Clear();
            pendingSwitches.Clear();
            Volatile.Write(ref processor, null);
            WatchSchematic();
            Rebuild(keepPositions: false);
        }

        /// <summary>Unloads the schematic; the audio passes through.</summary>
        public void ClearSchematic() => LoadSchematic("");

        /// <summary>Rebuilds the schematic from disk, keeping the current pot and switch positions.</summary>
        public void ReloadSchematic() => Rebuild(keepPositions: true);

        public void SetOversample(int oversample)
        {
            if (oversample == Oversample)
                return;
            Oversample = oversample;
            Rebuild(keepPositions: true);
        }

        public void SetIterations(int iterations)
        {
            if (iterations == Iterations)
                return;
            Iterations = iterations;
            Rebuild(keepPositions: true);
        }

        public void SetSincResampling(bool sinc)
        {
            if (sinc == SincResampling)
                return;
            SincResampling = sinc;
            Rebuild(keepPositions: true);
        }

        /// <summary>Whether the schematic is rebuilt when its file changes, as when it's edited in LiveSPICE.</summary>
        public void SetAutoReload(bool autoReload)
        {
            AutoReload = autoReload;
            WatchSchematic();
        }

        /// <summary>Opens the schematic in whatever edits .schx files (LiveSPICE, normally).</summary>
        public void EditSchematic()
        {
            if (string.IsNullOrEmpty(SchematicPath))
                return;
            try
            {
                System.Diagnostics.Process.Start(new ProcessStartInfo(SchematicPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't open the schematic for editing: " + ex.Message);
            }
        }

        void SetStatus(string status)
        {
            Status = status;
            Interlocked.Increment(ref version);
        }

        // Starts a build of the schematic at the current settings. With keepPositions the controls start where they are
        // now; otherwise at the schematic's settings (or the pending ones from a restored state).
        void Rebuild(bool keepPositions)
        {
            SchematicProcessor current = CurrentProcessor;
            if (keepPositions && current != null)
            {
                pendingPots = current.Pots.ToDictionary(p => p.Name, p => p.Position, StringComparer.OrdinalIgnoreCase);
                pendingSwitches = current.Switches.ToDictionary(s => s.Name, s => s.Position, StringComparer.OrdinalIgnoreCase);
            }

            int request = Interlocked.Increment(ref buildRequest);
            string path = SchematicPath;
            if (string.IsNullOrEmpty(path))
            {
                Volatile.Write(ref processor, null);
                Volatile.Write(ref finishedRequest, request);
                SetStatus(null);
                return;
            }
            // Without a sample rate yet (the host restored state before starting processing), InitializeProcessing builds.
            if (sampleRate <= 0)
            {
                Volatile.Write(ref finishedRequest, request);
                SetStatus("Waiting for the host to start processing.");
                return;
            }

            double rate = sampleRate;
            var options = new SchematicProcessorOptions
            {
                Oversample = Oversample,
                IterationCap = Iterations,
                Resampling = SincResampling ? Resampling.Sinc : Resampling.Linear,
                PotPositions = new Dictionary<string, double>(pendingPots),
                SwitchPositions = new Dictionary<string, int>(pendingSwitches),
                IgnoreUnknownControls = true,
            };
            SetStatus($"Building {Path.GetFileNameWithoutExtension(path)}...");

            Task.Run(() =>
            {
                lock (buildLock)
                {
                    if (request != Volatile.Read(ref buildRequest))
                        return;
                    try
                    {
                        Logger.Log($"Building {path} at {rate} Hz, oversample {options.Oversample}, iterations {options.IterationCap}");
                        var built = new SchematicProcessor(path, rate, options);
                        if (request != Volatile.Read(ref buildRequest))
                            return;
                        Volatile.Write(ref processor, built);
                        failed = false;
                        Volatile.Write(ref finishedRequest, request);
                        SetStatus(null);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Build failed with: " + ex);
                        if (request != Volatile.Read(ref buildRequest))
                            return;
                        Volatile.Write(ref processor, null);
                        Volatile.Write(ref finishedRequest, request);
                        SetStatus($"Couldn't build {Path.GetFileName(path)}: {ex.Message}");
                    }
                }
            });
        }

        void WatchSchematic()
        {
            watcher?.Dispose();
            watcher = null;
            if (!AutoReload || string.IsNullOrEmpty(SchematicPath) || !File.Exists(SchematicPath))
                return;

            watcher = new FileSystemWatcher(Path.GetDirectoryName(Path.GetFullPath(SchematicPath)), Path.GetFileName(SchematicPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // Editors save in several writes; rebuild once things have been quiet for half a second.
            reloadTimer ??= new Timer(_ => ReloadSchematic());
            FileSystemEventHandler changed = (_, _) => reloadTimer.Change(500, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (_, _) => reloadTimer.Change(500, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
        }

        public override byte[] SaveState()
        {
            var state = (SpiceForgeSaveState)SaveStateData;
            state.SchematicPath = SchematicPath;
            state.Oversample = Oversample;
            state.Iterations = Iterations;
            state.AutoReload = AutoReload;
            state.SincResampling = SincResampling;

            SchematicProcessor current = CurrentProcessor;
            state.Pots = current != null
                ? current.Pots.Select(p => new ControlSetting { Name = p.Name, Value = p.Position }).ToList()
                : pendingPots.Select(p => new ControlSetting { Name = p.Key, Value = p.Value }).ToList();
            state.Switches = current != null
                ? current.Switches.Select(s => new ControlSetting { Name = s.Name, Value = s.Position }).ToList()
                : pendingSwitches.Select(s => new ControlSetting { Name = s.Key, Value = s.Value }).ToList();

            return base.SaveState();
        }

        public override void RestoreState(byte[] stateData)
        {
            base.RestoreState(stateData);

            if (SaveStateData is not SpiceForgeSaveState state)
            {
                SaveStateData = new SpiceForgeSaveState();
                return;
            }

            Oversample = OversampleChoices.Contains(state.Oversample) ? state.Oversample : DefaultOversample;
            Iterations = IterationChoices.Contains(state.Iterations) ? state.Iterations : DefaultIterations;
            AutoReload = state.AutoReload;
            SincResampling = state.SincResampling;
            SchematicPath = state.SchematicPath ?? "";
            pendingPots = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (ControlSetting pot in state.Pots ?? new List<ControlSetting>())
                pendingPots[pot.Name] = pot.Value;
            pendingSwitches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ControlSetting sw in state.Switches ?? new List<ControlSetting>())
                pendingSwitches[sw.Name] = (int)sw.Value;

            Volatile.Write(ref processor, null);
            WatchSchematic();
            Rebuild(keepPositions: false);
        }

        IntPtr parentWindow;

        public override void ShowEditor(IntPtr parentWindow)
        {
            Logger.Log("Show Editor");

            this.parentWindow = parentWindow;

            if (parentWindow == IntPtr.Zero)
            {
                RunGame();
            }
            else
            {
                Thread thread = new Thread(new ThreadStart(RunGame));

                thread.SetApartmentState(ApartmentState.STA);

                thread.Start();
            }
        }

        void RunGame()
        {
            Logger.Log("Start Host");

            try
            {
                int screenWidth = (int)EditorWidth;
                int screenHeight = (int)EditorHeight;

                SpiceForgeLayout layout = new SpiceForgeLayout(this);

                layout.Scale = 0.35f;

                using (GameHost = new MonoGameHost(parentWindow, screenWidth, screenHeight, fullscreen: false))
                {
                    GameHost.IsMouseVisible = true;

                    GameHost.StartGame(layout);
                }

                layout = null;
            }
            catch (Exception ex)
            {
                Logger.Log("Host run failed with: " + ex.ToString());
            }
        }

        public override void ResizeEditor(uint newWidth, uint newHeight)
        {
            base.ResizeEditor(newWidth, newHeight);

            if (GameHost != null)
            {
                GameHost.RequestResize((int)newWidth, (int)newHeight);
            }
        }

        public override void HideEditor()
        {
            base.HideEditor();

            GameHost.Exit();
        }

        public override void InitializeProcessing()
        {
            base.InitializeProcessing();

            Logger.Log("Sample rate: " + Host.SampleRate + " Max Buffer Size: " + Host.MaxAudioBufferSize);

            if (Host.SampleRate != sampleRate)
            {
                sampleRate = Host.SampleRate;
                Rebuild(keepPositions: true);
            }
        }

        public override void SetMaxAudioBufferSize(uint maxSamples, EAudioBitsPerSample bitsPerSample)
        {
            base.SetMaxAudioBufferSize(maxSamples, bitsPerSample);

            monoIn = new[] { new double[maxSamples] };
            monoOut = new[] { new double[maxSamples] };
        }

        public override void Process()
        {
            base.Process();

            Host.ProcessAllEvents();

            SchematicProcessor current = CurrentProcessor;
            int count = (int)Host.CurrentAudioBufferSize;

            if (current == null || failed || current.SampleRate != sampleRate || count > monoIn[0].Length)
            {
                stereoInput.PassThroughTo(stereoOutput);
                return;
            }

            stereoInput.GetAudioBuffer(0).CopyTo(monoIn[0]);

            try
            {
                current.Process(monoIn, monoOut, count);
            }
            catch (Exception ex)
            {
                Fail("The simulation failed: " + ex.Message);
                stereoInput.PassThroughTo(stereoOutput);
                return;
            }

            double[] output = monoOut[0];
            for (int n = 0; n < count; n++)
            {
                if (!double.IsFinite(output[n]))
                {
                    Fail("The simulation diverged. Reload, or try more oversampling or iterations.");
                    stereoInput.PassThroughTo(stereoOutput);
                    return;
                }
            }

            ReadOnlySpan<double> result = output.AsSpan(0, count);
            result.CopyTo(stereoOutput.GetAudioBuffer(0));
            result.CopyTo(stereoOutput.GetAudioBuffer(1));
        }

        // Stops running the circuit (the audio passes through) until the next build, and says why.
        void Fail(string why)
        {
            failed = true;
            Logger.Log(why);
            SetStatus(why);
        }
    }
}
