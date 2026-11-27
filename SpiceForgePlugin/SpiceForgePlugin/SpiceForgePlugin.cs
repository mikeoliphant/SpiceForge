using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioPlugSharp;
using AudioPlugSharpUILayout;
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
        // On by default; a state saved without it (before the option existed) gets sinc too.
        public bool SincResampling { get; set; } = true;
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
    /// <para>Schematics are built on a background thread, since compiling takes a second or two; the audio passes
    /// through unchanged until the first build is ready, and a rebuild (new settings, or the schematic edited on disk)
    /// swaps in when it's done, starting from the same pot and switch positions. The editor polls
    /// <see cref="Version"/> to notice a new processor or status.</para>
    /// <para>The circuit's pots and switches are host parameters from a fixed pool (a plugin's parameters can't change
    /// once the host has them): the circuit's first <see cref="PotCount"/> pots are "Pot 1", "Pot 2"... in the order
    /// the processor lists them, and its first <see cref="SwitchCount"/> switches "Switch 1"... The parameters drive
    /// the circuit's controls each block; after each build they are set to where the circuit's controls are, which
    /// reports a newly loaded schematic's positions to the host.</para>
    /// </remarks>
    public class SpiceForgePlugin : AudioPluginUILayout
    {
        // 4× rather than LiveSPICE's 8×: with the cap of 32 below it was within 1-3 dB of 8× on every example, for
        // 35-45% less CPU (see Oversampling in LessonsLearned.md).
        public const int DefaultOversample = 4;
        // A cap of 32 rather than LiveSPICE's 8: it only costs anything on steps that would hit the cap, and at 8× it
        // took the Rat's 5 kHz aliasing from -22 to -39 dB at the same CPU (see Oversampling in LessonsLearned.md).
        public const int DefaultIterations = 32;
        public static readonly int[] OversampleChoices = { 1, 2, 4, 8, 16 };
        public static readonly int[] IterationChoices = { 1, 2, 4, 8, 16, 32, 64 };

        /// <summary>How many of a circuit's pots and switches are host parameters (and shown in the editor).</summary>
        public const int PotCount = 16;
        public const int SwitchCount = 8;

        /// <summary>The pot parameters: the circuit's pots, in order, from 0 to 1.</summary>
        public IReadOnlyList<AudioPluginParameter> PotParameters => potParameters;

        /// <summary>The switch parameters: the circuit's switches, in order (see <see cref="SwitchPositionParameter"/>).</summary>
        public IReadOnlyList<SwitchPositionParameter> SwitchParameters => switchParameters;

        /// <summary>The schematic loaded (or being loaded), or empty.</summary>
        public string SchematicPath { get; private set; } = "";
        public int Oversample { get; private set; } = DefaultOversample;
        public int Iterations { get; private set; } = DefaultIterations;
        public bool AutoReload { get; private set; }

        /// <summary>Whether to resample with windowed sincs (a flat band and little aliasing, about 40 samples of
        /// latency the host isn't told about) instead of LiveSPICE's linear interpolation and averaging.</summary>
        public bool SincResampling { get; private set; } = true;

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

        readonly AudioPluginParameter[] potParameters = new AudioPluginParameter[PotCount];
        readonly SwitchPositionParameter[] switchParameters = new SwitchPositionParameter[SwitchCount];

        SpiceForgeEditor editor;

        public SpiceForgePlugin()
        {
            Company = "Mike Oliphant";
            Website = "github.com/mikeoliphant";
            Contact = "contact@nostatic.org";
            PluginName = "SpiceForge";
            PluginCategory = "Fx";
            PluginVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(this.GetType().Assembly.Location).FileVersion;

            PluginID = 0x47B5882C49394EAD;

            EditorWidth = 800;
            EditorHeight = 400;

            SaveStateData = new SpiceForgeSaveState();
        }

        public override void Initialize()
        {
            base.Initialize();

            InputPorts = new AudioIOPort[] { stereoInput = new DoubleAudioIOPort("Stereo Input", EAudioChannelConfiguration.Stereo) };
            OutputPorts = new AudioIOPort[] { stereoOutput = new DoubleAudioIOPort("Stereo Output", EAudioChannelConfiguration.Stereo) };

            for (int i = 0; i < PotCount; i++)
            {
                AddParameter(potParameters[i] = new AudioPluginParameter
                {
                    ID = "pot" + (i + 1),
                    Name = "Pot " + (i + 1),
                    MinValue = 0,
                    MaxValue = 1,
                    DefaultValue = 0.5,
                    ValueFormat = "{0:0.00}",
                });
            }

            for (int i = 0; i < SwitchCount; i++)
            {
                AddParameter(switchParameters[i] = new SwitchPositionParameter
                {
                    ID = "switch" + (i + 1),
                    Name = "Switch " + (i + 1),
                });
            }
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

                        // The controls may have moved while it built: the running circuit has their latest positions.
                        if (keepPositions)
                            CopyPositions(CurrentProcessor, built);

                        SyncParameters(built);

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

        // Sets the new circuit's pots and switches to where the running circuit's of the same names are.
        static void CopyPositions(SchematicProcessor from, SchematicProcessor to)
        {
            if (from == null)
                return;

            foreach (PotControl pot in to.Pots)
            {
                if (from.GetPot(pot.Name) is PotControl old)
                    pot.Position = old.Position;
            }

            foreach (SwitchControl sw in to.Switches)
            {
                if ((from.GetSwitch(sw.Name) is SwitchControl old) && (old.Position < sw.Positions))
                    sw.Position = old.Position;
            }
        }

        // Sets the pool's parameters to the circuit's positions, before it runs (so the audio thread doesn't move its
        // controls back to the old values). Only values that differ are set, so a rebuild or a restored state reports
        // nothing to the host, and a new schematic reports its own positions. An edit made here, off the UI thread, is
        // forwarded to the host on its next editor frame.
        void SyncParameters(SchematicProcessor circuit)
        {
            for (int i = 0; i < Math.Min(circuit.Pots.Count, PotCount); i++)
                SetParameter(potParameters[i], circuit.Pots[i].Position);

            for (int i = 0; i < Math.Min(circuit.Switches.Count, SwitchCount); i++)
            {
                SwitchControl sw = circuit.Switches[i];

                switchParameters[i].Positions = sw.Positions;

                SetParameter(switchParameters[i], SwitchPositionParameter.ToValue(sw.Position, sw.Positions));
            }
        }

        static void SetParameter(AudioPluginParameter parameter, double value)
        {
            if (parameter.EditValue == value)
                return;

            parameter.ProcessValue = value;
            parameter.EditValue = value;
        }

        // [audio thread] The parameters move the circuit's controls (which ramp to where they're set).
        void ApplyParameters(SchematicProcessor circuit)
        {
            IReadOnlyList<PotControl> pots = circuit.Pots;
            IReadOnlyList<SwitchControl> switches = circuit.Switches;

            for (int i = 0; i < Math.Min(pots.Count, PotCount); i++)
                pots[i].Position = Math.Clamp(potParameters[i].ProcessValue, 0, 1);

            for (int i = 0; i < Math.Min(switches.Count, SwitchCount); i++)
                switches[i].Position = SwitchPositionParameter.ToPosition(switchParameters[i].ProcessValue, switches[i].Positions);
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

        // ---- editor (UI thread) ----

        protected override UIElement CreateEditorUI()
        {
            editor = new SpiceForgeEditor(this);

            return editor.Root;
        }

        protected override void EditorUpdate(float secondsElapsed)
        {
            editor?.Update();
        }

        protected override void EditorClosing()
        {
            editor = null;
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

            ApplyParameters(current);

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
